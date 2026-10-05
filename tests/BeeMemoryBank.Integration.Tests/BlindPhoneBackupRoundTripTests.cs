using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A phone's backup, made by the phone's own classes, opened by the real Windows restore source
/// (<see cref="AndroidBackupRestoreSource"/>) with nothing but the file and the master password.
/// The network is <see cref="RestoreSourceFixture"/>'s, one instance per test (pairing the phone and publishing a
/// new anchor change it): a PC with two key rotations, and a listening blind node. The phone is paired by the
/// PC's real endpoint, installs the listener's replica with the real client, and pulls with the real pull
/// client; the package source, recovery-set source and runner are the ones the app composes.
/// </summary>
public sealed class BlindPhoneBackupRoundTripTests : IAsyncLifetime
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", RestoreSourceFixture.Password);
    private readonly RestoreSourceFixture net = new();
    private readonly BlindNodeFactory _phoneHost = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb-s4-roundtrip-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => net.InitializeAsync();

    public async Task DisposeAsync()
    {
        _phoneHost.Dispose();
        await net.DisposeAsync();
    }

    [Fact]
    public async Task APhonesBackup_OpensOnANewPC_FromTheFileAndTheMasterPassword_AfterTheFirstAnchorReachedThePhone()
    {
        var phone = await PairAndInstallAsync();
        await PcPublishesAnAnchorAndThePhonePullsItAsync(phone);

        var outcome = await phone.Runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.Done, outcome.Message);
        using var target = new RecoveryTestFactory();
        var result = await Restorer(target).RestoreAsync(outcome.FilePath!, Who, CancellationToken.None);
        result.RetiredKeys.Should().Be(2, "both older keys come back through the chain, from the recovery set of the PHONE's database");
        result.Anchor.Found.Should().BeTrue();
        result.Anchor.MatchesAnchor.Should().BeTrue("the anchor the phone received after its first load vouches for the package");
        var session = target.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(RestoreSourceFixture.Password)).Should().BeTrue();
        DekFingerprint.Of(session.GetMasterDek()).Should().Be(net.CurrentFingerprint);
        using var scope = target.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        foreach (var (id, body) in net.Bodies)
            (await articles.GetContentAsync(id)).Should().Be(body);
        var listener = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(net.BlindNodeIdentity))!;
        listener.TlsSpki.Should().Be(phone.ListenerPin, "the restored device dials the node the phone was paired to, pinned as the pairing recorded it");
    }

    [Fact]
    public async Task BeforeTheFirstAnchorReachesThePhone_NoBackupIsMade_NoPackageIsDownloaded_AndTheScreenSaysWhy()
    {
        var phone = await PairAndInstallAsync();
        var requestsAfterInstall = phone.ReplicaRequests;

        var outcome = await phone.Runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.NotAvailable);
        outcome.Message.Should().Contain("anchor");
        phone.Runner.Backups().Should().BeEmpty();
        phone.ReplicaRequests.Should().Be(requestsAfterInstall, "a package is fetched only when a restore could use it");
    }

    /// <summary>
    /// Why the body carries a fresh package: made from the first load's package and the events the phone
    /// holds (none yet), the file is one only this phone could use — the Windows restore finds no signed
    /// anchor event for the package's anchor rows and refuses to vouch for who produced it.
    /// </summary>
    [Fact]
    public async Task ABodyMadeFromTheFirstLoadsPackageAndNoAnchorEvent_IsRefusedByTheWindowsRestore()
    {
        var phone = await PairAndInstallAsync();
        var package = await phone.Replica.FetchVerifiedPackageAsync(phone.Http, phone.Target, Path.Combine(_work, "first"),
            progress: null, CancellationToken.None);
        var body = Path.Combine(_work, "first-body.tar");
        var events = Path.Combine(_work, "first-events.json");
        await File.WriteAllTextAsync(events, "[]");
        await BlindPhoneBackupBody.WriteAsync(body, package.ArchivePath, package.Signature, events);
        var file = Path.Combine(_work, "first" + AndroidBackupRestore.Extension);
        var set = await new BlindPhoneRecoverySetSource(phone.Factory).BuildJsonAsync(CancellationToken.None);
        await AndroidBackupWriter.WriteAsync(body, file, phone.BackupKey, phone.PhoneId, SealedSecretService.AndroidBackupName(phone.PhoneId), set);
        using var target = new RecoveryTestFactory();

        var act = () => Restorer(target).RestoreAsync(file, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*No anchor under the master key vouches*");
    }

    // ─── The network and the phone ──────────────────────────────────────────

    private async Task<Phone> PairAndInstallAsync()
    {
        Directory.CreateDirectory(_work);
        var blindId = net.BlindNodeIdentity;
        var listenerPin = net.Blind.Services.GetRequiredService<BlindTlsIdentity>().Spki;

        // The PC knows how to dial the listener (the pairing endpoint only offers a listener it can pin).
        using (var scope = net.Source.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await whitelist.GetByNodeIdAsync(blindId))!;
            row.ApiAddress = BlindNodeFactory.PublicAddress;
            row.TlsSpki = listenerPin;
            await whitelist.UpdateAsync(row);
        }

        // The phone: a blind host's own v=2 identity stands for the app's Keystore identity.
        var phoneIdentity = (await _phoneHost.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var backupKey = RandomNumberGenerator.GetBytes(32);
        var code = new BlindPhoneCode(phoneIdentity.NodeId, phoneIdentity.Ed25519PublicKey, BlindPairingSecret.New(), backupKey, "Pixel");

        // Windows pairs it: the real endpoint adds the whitelist row and seals the backup key under the DEK.
        long before;
        using (var scope = net.Source.Services.CreateScope())
            before = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        using (var pc = net.Source.CreateClient())
        {
            var resp = await pc.PostAsJsonAsync("/api/blind-nodes/android/", new { code = code.ToString(), listenerId = blindId });
            resp.EnsureSuccessStatusCode();
            var paired = await resp.Content.ReadFromJsonAsync<JsonElement>();
            BlindCallCode.TryParse(paired.GetProperty("callCode").GetString(), out var call).Should().BeTrue();
            await DeliverAsync(net.Source, net.Blind, before);

            var http = new HttpClient(new CountingHandler(net.Blind.Server.CreateHandler(), out var requests));
            var dbPath = Path.Combine(_phoneHost.DataPath, "beememorybank.db");
            var factory = _phoneHost.Services.GetRequiredService<DbConnectionFactory>();
            var replica = new BlindPhoneReplicaClient(factory, _phoneHost.Services.GetRequiredService<INodeIdentityRepository>(),
                _phoneHost.Services.GetRequiredService<INodeAuthSigner>(), _phoneHost.DataPath, dbPath,
                NullLogger<BlindPhoneReplicaClient>.Instance);
            await replica.FetchAndInstallAsync(http, call!, Path.Combine(_work, "replica"), progress: null, CancellationToken.None);
            // The listener may serve a package built before the pairing events reached it; the phone's next sync brings them.
            await new BlindPhonePullClient(_phoneHost.Services.GetRequiredService<IServiceScopeFactory>(),
                _phoneHost.Services.GetRequiredService<INodeAuthSigner>(), NullLogger<BlindPhonePullClient>.Instance)
                .SyncOnceAsync(http, call!, CancellationToken.None);

            var store = new MemoryStore();
            var state = new BlindPhoneState(store) { NodeId = phoneIdentity.NodeId, CallCode = call };
            var fetcher = new TestFetcher(replica, http, call!, Path.Combine(_work, "replica"));
            var source = new BlindPhonePackageSource(fetcher, _phoneHost.Services.GetRequiredService<IServiceScopeFactory>());
            var recovery = new BlindPhoneRecoverySetSource(factory);
            var runner = new BlindPhoneBackupRunner(state, new FixedKeys(backupKey), source, recovery,
                new BlindPhoneLog(Path.Combine(_work, "log.jsonl"), TimeProvider.System), Path.Combine(_work, "backups"), TimeProvider.System);
            return new Phone(phoneIdentity.NodeId, backupKey, call!, listenerPin, http, replica, factory, runner, requests);
        }
    }

    /// <summary>The PC anchors the state that now includes the phone; the listener applies it; the phone pulls it.</summary>
    private async Task PcPublishesAnAnchorAndThePhonePullsItAsync(Phone phone)
    {
        long before;
        using (var scope = net.Source.Services.CreateScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
            // "Caught up": the scheduler wants a fresh pull position for every peer that has an address.
            var later = DateTime.UtcNow.AddHours(2);
            await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().UpsertAsync(
                new SyncPosition { RemoteNodeId = net.BlindNodeIdentity, LastSequenceNum = 1, UpdatedAt = later });
            (await net.Source.Services.GetRequiredService<StateAnchorScheduler>().PublishIfDueAsync(later)).Should().BeTrue(
                "the state changed since the last anchor: the phone is in it now");
        }
        await DeliverAsync(net.Source, net.Blind, before);
        // Half an hour on, the listener builds a new package instead of serving the one it cached for the first load.
        await net.Blind.Services.GetRequiredService<BlindReplicaPackageCache>().DisposeAsync();

        var pull = new BlindPhonePullClient(_phoneHost.Services.GetRequiredService<IServiceScopeFactory>(),
            _phoneHost.Services.GetRequiredService<INodeAuthSigner>(), NullLogger<BlindPhonePullClient>.Instance);
        await pull.SyncOnceAsync(phone.Http, phone.Target, CancellationToken.None);
    }

    /// <summary>Applies the events <paramref name="from"/> logged after <paramref name="afterSequence"/> to <paramref name="to"/>, as sync would.</summary>
    private static async Task DeliverAsync(RecoveryTestFactory from, RecoveryTestFactory to, long afterSequence)
    {
        using var fromScope = from.Services.CreateScope();
        using var toScope = to.Services.CreateScope();
        var events = await fromScope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(afterSequence, 10_000);
        var applier = toScope.ServiceProvider.GetRequiredService<EventApplier>();
        foreach (var evt in events) await applier.ApplyAsync(evt);
    }

    private static AndroidBackupRestoreSource Restorer(RecoveryTestFactory target) =>
        target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single();

    private sealed record Phone(
        Guid PhoneId, byte[] BackupKey, BlindCallCode Target, string ListenerPin, HttpClient Http, BlindPhoneReplicaClient Replica,
        DbConnectionFactory Factory, BlindPhoneBackupRunner Runner, Counter Requests)
    {
        public int ReplicaRequests => Requests.Replica;
    }

    private sealed class Counter { public int Replica; }

    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly Counter _counter = new();

        public CountingHandler(HttpMessageHandler inner, out Counter counter) : base(inner) => counter = _counter;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath == "/api/blind/replica") Interlocked.Increment(ref _counter.Replica);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class TestFetcher(BlindPhoneReplicaClient client, HttpClient http, BlindCallCode target, string work) : IBlindVerifiedPackageFetcher
    {
        public Task<VerifiedReplicaPackage> FetchAsync(Action<long> beforeDownload, IProgress<double>? progress, CancellationToken ct) =>
            client.FetchVerifiedPackageAsync(http, target, work, progress, ct, beforeDownload);
    }

    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private sealed class FixedKeys(byte[] backupKey) : IBlindPhoneKeys
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => (byte[])backupKey.Clone();
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

}
