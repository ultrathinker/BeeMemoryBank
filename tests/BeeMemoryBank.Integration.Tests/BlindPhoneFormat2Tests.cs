using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;
using BeeMemoryBank.Api.Services.Recovery;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A blind phone or desktop copy after the activation (docs/design/step-a-spec.md 13, 16.4 item 8), against a real Docker blind node that a real
/// activated PC seeded: the format-2 install (whitelist, control rows, blobs, the baseline adopted, deltas stored by the relay applier with their
/// origin, the pull position), the legacy plaintext of a format-1 copy gone after it (canary scan of the copy's own files), the newer baseline taken
/// with <c>?have=</c> and what it covers of the copy's log trimmed, the capabilities a pull reports, and what a damaged package does.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindPhoneFormat2Tests : IAsyncLifetime
{
    private const string Password = "blindPhone2Pw1!";
    private readonly BlindNodeFactory _blind = new(drainWaitSeconds: 10);
    private readonly HubPcFactory _pc = new();
    private readonly Tamper _hub;
    private HttpClient _pcClient = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_phone_f2_" + Guid.NewGuid().ToString("N"));
    private List<string> _canaries = [];
    private StepANode.SeededVault _seeded = null!;
    private Guid _blindId;

    public BlindPhoneFormat2Tests()
    {
        _hub = new Tamper(_blind);
    }

    /// <summary>A PC that also has the format-2 writer a full node ships blind nodes with (track 2's hook H3 registers the real one).</summary>
    private sealed class HubPcFactory : StepAFactory
    {
        protected override void ConfigureStepAServices(IServiceCollection services)
        {
            // The tests produce the PC's baselines themselves, at the moments they name; the scheduler's own early production
            // after the activation (which BaselineSchedulerTests cover) would otherwise race with them.
            services.Replace(ServiceDescriptor.Singleton<IBaselineScheduler>(new QuietScheduler()));
            services.TryAddScoped<IBlindPackageV2Writer>(sp => new BlindPackageV2Writer(
                sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<IWhitelistRepository>(), sp.GetRequiredService<ISyncPositionRepository>(),
                sp.GetRequiredService<IEventLogRepository>(),
                new BlindPackageV2WriterOptions(
                    (identity, payload) => sp.GetRequiredService<INodeAuthSigner>().SignChallenge(identity, payload),
                    Path.Combine(Path.GetTempPath(), "bmb_phone_f2_out"), Path.Combine(Path.GetTempPath(), "bmb_phone_f2_stage"),
                    ServingNodeIsSuperadmin: _ => Task.FromResult(true))));
        }
    }

    private sealed class QuietScheduler : IBaselineScheduler
    {
        public void RequestEarly(string reasonCode) { }
    }

    /// <summary>The hub's replica answer, passed through, or with one byte of the archive changed.</summary>
    private sealed class Tamper : DelegatingHandler
    {
        public Tamper(BlindNodeFactory blind) : base(blind.Server.CreateHandler()) { }
        public bool Damage { get; set; }
        public List<string> ReplicaQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath != "/api/blind/replica") return response;
            ReplicaQueries.Add(request.RequestUri.Query);
            if (!Damage || response.StatusCode != HttpStatusCode.OK) return response;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            bytes[bytes.Length / 2] ^= 0x40;
            var damaged = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            foreach (var header in response.Headers) damaged.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return damaged;
        }
    }

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_hub);
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        Directory.CreateDirectory(_work);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    // ── the hub: a real activated PC and a real Docker blind node it seeded ────────────────────────

    private async Task PairBlindNodeAsync()
    {
        _seeded = await StepANode.SeedWithCanariesAsync(_pc);
        _canaries = _seeded.Canaries;
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        var code = BlindPairCode.Parse(body.GetProperty("code").GetString()!);
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
        _blindId = code.NodeId;
    }

    /// <summary>The owner's activation through the real route, the baseline, the blind node told and reseeded with it.</summary>
    private async Task<BaselineFiles> ActivateAndReseedAsync()
    {
        var activation = await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password });
        activation.StatusCode.Should().Be(HttpStatusCode.OK, await activation.Content.ReadAsStringAsync());
        var baseline = await ProduceBaselineAsync();
        await PushToBlindAsync();
        await _pc.Services.GetRequiredService<BlindNodeManager>().ReseedAsync(_blindId, CancellationToken.None);
        return baseline;
    }

    private async Task<BaselineFiles> ProduceBaselineAsync() =>
        await _pc.Services.GetRequiredService<IBaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);

    private async Task PushToBlindAsync()
    {
        using var scope = _pc.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_blindId))!;
        using var http = _pc.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SyncScheduler.HttpClientName);
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithPeerAsync(http, row.ApiAddress!, _blindId, CancellationToken.None);
    }

    private async Task WriteNoteAsync(string title)
    {
        using var scope = _pc.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "/" + MetadataCanaries.Ascii("late"), [], "written later");
    }

    // ── the phone: the real AppCore composition, the replica and pull clients built over it ─────────

    private sealed record Phone(
        string Dir, ServiceProvider Services, NodeIdentity Identity, BlindCallCode Target, BlindPhoneReplicaClient Replica, BlindPhonePullClient Pull,
        byte[]? BackupKey = null)
    {
        public DbConnectionFactory Db => Services.GetRequiredService<DbConnectionFactory>();
    }

    /// <param name="hubIsThePc">The copy calls the activated PC itself (a hub), not the Docker blind node.</param>
    /// <param name="pairedByPc">The PC's own endpoint pairs the phone (the whitelist row, the sealed backup key), as the Windows app does.</param>
    private async Task<Phone> CreatePhoneAsync(bool hubIsThePc = false, bool pairedByPc = false)
    {
        var dir = Path.Combine(_work, "phone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "beememorybank.db");
        var services = new ServiceCollection();
        services.AddSingleton<IBlindStateStore, MemoryState>();
        services.AddSingleton<IBlindSecretStore, NoSecrets>();
        services.AddSingleton<IBlindLifecycle, NoLifecycle>();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(dir, dbPath));
        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<BlindStartup>().EnsureReadyAsync();

        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var phoneId = BlindNodeId.NewId();
        var identity = new NodeIdentity
        {
            NodeId = phoneId, DisplayName = "phone", Ed25519PublicKey = publicKey, Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CanGenerateEmbeddings = false, CreatedAt = DateTime.UtcNow,
        };
        await sp.GetRequiredService<INodeIdentityRepository>().CreateAsync(identity);
        var hub = hubIsThePc ? (BmbWebApplicationFactory)_pc : _blind;
        BlindCallCode target;
        byte[]? backupKey = null;
        if (pairedByPc)
        {
            // The PC dials the listener pinned: its row needs the address and the pin the pairing endpoint offers a listener by.
            using (var scope = _pc.Services.CreateScope())
            {
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var row = (await whitelist.GetByNodeIdAsync(_blindId))!;
                row.ApiAddress = BlindNodeFactory.PublicAddress;
                row.TlsSpki = _blind.Services.GetRequiredService<BlindTlsIdentity>().Spki;
                await whitelist.UpdateAsync(row);
            }
            backupKey = RandomNumberGenerator.GetBytes(32);
            var code = new BlindPhoneCode(phoneId, publicKey, BlindPairingSecret.New(), backupKey, "Pixel");
            var paired = await _pcClient.PostAsJsonAsync("/api/blind-nodes/android/", new { code = code.ToString(), listenerId = _blindId });
            paired.StatusCode.Should().Be(HttpStatusCode.OK, await paired.Content.ReadAsStringAsync());
            BlindCallCode.TryParse((await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("callCode").GetString(), out var call).Should().BeTrue();
            target = call!;
            // The blind node learns the phone from the PC's events, as it would from the next sync.
            await PushToBlindAsync();
        }
        else
        {
            await hub.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = phoneId, DisplayName = "phone", Ed25519PublicKey = publicKey, Status = WhitelistStatuses.Active,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            var server = (await hub.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            target = BlindCallCode.Create(BlindNodeFactory.PublicAddress, server.NodeId,
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", server.Ed25519PublicKey, new byte[32]);
        }
        var signer = new SeedSigner(seed);
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var replica = new BlindPhoneReplicaClient(sp.GetRequiredService<DbConnectionFactory>(), sp.GetRequiredService<INodeIdentityRepository>(),
            signer, dir, dbPath, NullLogger<BlindPhoneReplicaClient>.Instance, scopes: scopes);
        var pull = new BlindPhonePullClient(scopes, signer, NullLogger<BlindPhonePullClient>.Instance);
        return new Phone(dir, sp, identity, target, replica, pull, backupKey);
    }

    private HttpClient HubClient() => new(_hub, disposeHandler: false);

    private static async Task<T> ScalarAsync<T>(Phone phone, string sql, object? args = null)
    {
        using var conn = phone.Db.CreateConnection();
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? default!;
    }

    // ── tests ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_copy_installs_a_format2_replica_and_holds_none_of_the_vaults_metadata()
    {
        await PairBlindNodeAsync();
        var baseline = await ActivateAndReseedAsync();
        await WriteNoteAsync(MetadataCanaries.Ascii("after"));
        await PushToBlindAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();

        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        (await ScalarAsync<string>(phone, "SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).Should()
            .BeEquivalentTo(baseline.Manifest.BaselineId.ToString(), "the copy adopted the baseline the hub serves");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_whitelist WHERE is_superadmin = 1")).Should().BeGreaterThan(0, "the package's whitelist was imported");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_metadata_key")).Should().BeGreaterThan(0, "the control rows came with it");
        phone.Services.GetRequiredService<ISyncProtocolPolicy>().Declared.Should().Be(4, "the copy declares the activated protocol from now on");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_article")).Should().Be(0, "a copy holds no article rows after the activation");
        (await ScalarAsync<long>(phone, "SELECT last_sequence_num FROM tbl_sync_position WHERE remote_node_id = @id COLLATE NOCASE",
            new { id = phone.Target.NodeId.ToString() })).Should().BeGreaterThan(0, "the next pull starts at the package's cp_sequence");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event WHERE origin_peer = @id COLLATE NOCASE AND origin_seq IS NOT NULL",
            new { id = phone.Target.NodeId.ToString() })).Should().BeGreaterThan(0, "the deltas were stored with the node they came from and their number in its log");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(phone.Dir, _canaries).Should().BeEmpty("no title, folder, tag or comment of the vault is in any file of the copy");
    }

    [Fact]
    public async Task A_loaded_format1_copy_loses_its_plaintext_when_it_loads_again_after_the_activation()
    {
        await PairBlindNodeAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();
        // Before the activation: the 2.5 load, which holds the vault's metadata in clear.
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(phone.Dir, _canaries).Should().NotBeEmpty("the check below is only worth something if the old load held them");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_article")).Should().BeGreaterThan(0);

        await ActivateAndReseedAsync();
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_article")).Should().Be(0);
        (await ScalarAsync<string>(phone, "SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'")).Should().Be("1");
        MetadataCanaries.ScanDirectory(phone.Dir, _canaries).Should().BeEmpty(
            "the first adoption purged the plaintext, secure-deleted and vacuumed: not in the database, the journal or the wal");
    }

    [Fact]
    public async Task A_newer_baseline_is_taken_with_have_and_what_it_covers_of_the_log_is_trimmed()
    {
        await PairBlindNodeAsync();
        var first = await ActivateAndReseedAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();
        var work = Path.Combine(phone.Dir, "replica");
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        (await phone.Replica.UpdateBaselineAsync(http, phone.Target, work, null, CancellationToken.None)).Should()
            .BeFalse("the hub's baseline is the one the copy holds: 304");
        _hub.ReplicaQueries.Should().Contain(q => q.Contains(first.Manifest.BaselineId.ToString()), "the copy asked with the id of its baseline");

        await WriteNoteAsync(MetadataCanaries.Ascii("second"));
        await PushToBlindAsync();
        // The pull stores the new events with their numbers in the hub's log; the next baseline covers them.
        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);
        var storedBefore = await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event WHERE origin_peer = @id COLLATE NOCASE", new { id = phone.Target.NodeId.ToString() });
        storedBefore.Should().BeGreaterThan(0);
        var second = await ProduceBaselineAsync();
        await PushToBlindAsync();
        await _pc.Services.GetRequiredService<BlindNodeManager>().ReseedAsync(_blindId, CancellationToken.None);

        (await phone.Replica.UpdateBaselineAsync(http, phone.Target, work, null, CancellationToken.None)).Should().BeTrue();

        (await ScalarAsync<string>(phone, "SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).Should()
            .BeEquivalentTo(second.Manifest.BaselineId.ToString());
        var cut = await ScalarAsync<long>(phone, "SELECT local_cut FROM tbl_baseline WHERE is_current = 1");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event WHERE origin_peer = @id COLLATE NOCASE AND origin_seq <= @cut",
            new { id = phone.Target.NodeId.ToString(), cut })).Should().Be(0, "what the baseline covers of the hub's log is gone from the copy");
        (await phone.Replica.UpdateBaselineAsync(http, phone.Target, work, null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_after_the_install_reports_what_the_copy_declares_and_stores_what_it_pulled_with_its_origin()
    {
        await PairBlindNodeAsync();
        await ActivateAndReseedAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);
        var before = await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event");
        await WriteNoteAsync(MetadataCanaries.Ascii("pulled"));
        await PushToBlindAsync();

        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);

        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event")).Should().BeGreaterThan(before);
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event WHERE origin_peer IS NULL")).Should().Be(0, "every stored event says which node delivered it");
        var row = await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(phone.Identity.NodeId);
        row!.LastCapabilities.Should().Contain(MetadataCapabilities.EnvelopeV1, "report-position carries the capabilities (spec 5.1)");
        row.LastProtocolVersion.Should().Be(4, "and the protocol the activated copy declares");
    }

    /// <summary>The events endpoint's default page (docs/design/step-a-spec.md 5.7).</summary>
    private const int PageSize = 1000;

    /// <summary>
    /// After the activation a page of the events endpoint is filled with the rows the copy may have, and it is empty only when none is left
    /// (track 1's review round, spec 5.7). A copy more than one page behind, with protocol-3 rows in front of the sealed ones, must still reach
    /// them: its cursor is the last row it was given, so a page that is cut first and filtered after would have it ask for the same empty page
    /// for ever. The copy stores none of the protocol-3 rows.
    /// </summary>
    [Fact]
    public async Task A_copy_more_than_a_page_behind_reaches_the_sealed_rows_behind_the_protocol3_rows()
    {
        (await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ProduceBaselineAsync();
        var phone = await CreatePhoneAsync(hubIsThePc: true);
        using var http = new HttpClient(_pc.Server.CreateHandler(), disposeHandler: false);
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        await AppendProtocol3RowsAsync(PageSize + 100);
        await WriteNoteAsync(MetadataCanaries.Ascii("behind the protocol-3 rows"));
        var sealedHead = await HubHeadAsync();

        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);

        (await CopyPositionAsync(phone)).Should().Be(sealedHead, "the copy applied the sealed rows that come after the rows it may not have");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'article_create'")).Should().Be(0,
            "no protocol-3 content reached the copy");
    }

    /// <summary>
    /// The same contract from the other side: a copy left with protocol-3 rows only gets an empty page, which is neither an error nor a stall,
    /// and the hub counts the copy as caught up over those rows. Its delivery watermark moves over them, and the copy's own report of its lower
    /// cursor does not move it back (the watermark only ever rises).
    /// </summary>
    [Fact]
    public async Task A_copy_left_with_protocol3_rows_only_gets_an_empty_page_and_the_hub_counts_it_caught_up()
    {
        (await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ProduceBaselineAsync();
        var phone = await CreatePhoneAsync(hubIsThePc: true);
        using var http = new HttpClient(_pc.Server.CreateHandler(), disposeHandler: false);
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);
        await WriteNoteAsync(MetadataCanaries.Ascii("sealed"));
        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);
        var caughtUp = await CopyPositionAsync(phone);

        await AppendProtocol3RowsAsync(PageSize + 5);
        var head = await HubHeadAsync();
        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);

        (await CopyPositionAsync(phone)).Should().Be(caughtUp, "nothing the copy may have is left, so its cursor stays where its last sealed row put it");
        var watermark = await _pc.Services.GetRequiredService<ISyncPushPositionRepository>().GetAsync(phone.Identity.NodeId);
        watermark!.LastPushedSeq.Should().Be(head,
            "the hub's delivery watermark moved over the rows the copy may not have, and the copy's lower report does not move it back");
    }

    /// <summary>Protocol-3 content rows in the hub's log, as a node before the activation wrote them. A copy after the activation is never sent them.</summary>
    private async Task AppendProtocol3RowsAsync(int count)
    {
        using var scope = _pc.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<ILamportClock>();
        var self = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        for (var i = 0; i < count; i++)
            await events.AppendIfNotExistsAsync(new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = self.NodeId, LamportTs = clock.Tick(), EventType = EventTypes.ArticleCreate,
                Payload = "{}", Signature = new byte[64], ProtocolVersion = 3, CreatedAt = DateTime.UtcNow,
            });
    }

    private Task<long> HubHeadAsync() => _pc.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();

    private static async Task<long?> CopyPositionAsync(Phone phone)
    {
        using var scope = phone.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(phone.Target.NodeId))?.LastSequenceNum;
    }

    [Fact]
    public async Task A_damaged_package_is_refused_and_nothing_is_installed()
    {
        await PairBlindNodeAsync();
        await ActivateAndReseedAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();
        _hub.Damage = true;

        var act = () => phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_baseline")).Should().Be(0);
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_whitelist")).Should().Be(0, "no row of a package that did not verify was imported");
    }

    [Fact]
    public async Task A_backup_package_is_fetched_and_verified_through_the_format2_reader_and_not_installed()
    {
        await PairBlindNodeAsync();
        await ActivateAndReseedAsync();
        var phone = await CreatePhoneAsync();
        using var http = HubClient();

        var package = await phone.Replica.FetchVerifiedPackageAsync(http, phone.Target, Path.Combine(phone.Dir, "backup-work"), progress: null, CancellationToken.None);

        File.Exists(package.ArchivePath).Should().BeTrue();
        package.Length.Should().BeGreaterThan(0);
        (await RecoveryRestoreService.IsFormat2PackageAsync(package.ArchivePath)).Should().BeTrue("the backup keeps the package the hub served, sealed as it is");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_baseline")).Should().Be(0, "a backup fetch installs nothing");
    }

    // ── a phone's backup after the activation ─────────────────────────────────────────────────────

    /// <summary>
    /// The whole chain of docs/design/step-a-spec.md 13.4 and 12: the phone is paired by the PC's own endpoint, loads the sealed replica, pulls the
    /// anchor the PC publishes, makes its backup with the classes the app composes (a fresh format-2 package, the phone's events, the sealed pairing
    /// record), and a clean PC restores from that file and the master password alone - from a baseline and deltas, the pairing checked against the anchors
    /// the phone holds - and opens the vault.
    /// </summary>
    [Fact]
    public async Task A_phones_backup_made_after_the_activation_restores_on_a_clean_pc_from_the_file_and_the_master_password()
    {
        await PairBlindNodeAsync();
        using (var scope = _pc.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
            await _pc.Services.GetRequiredService<RecoveryTriggers>().OnSuperadminLogin(admin, Password);
        }
        await ActivateAndReseedAsync();

        // The phone is paired by the PC's endpoint: it adds the whitelist row and seals the backup key under the master key.
        var phone = await CreatePhoneAsync(pairedByPc: true);
        using var http = HubClient();
        var work = Path.Combine(phone.Dir, "replica");
        await phone.Replica.FetchAndInstallAsync(http, phone.Target, work, progress: null, CancellationToken.None);

        // The PC anchors the state that includes the phone; the blind node applies it; the phone pulls it. The listener builds a fresh package.
        await _pc.Services.GetRequiredService<StateAnchorService>().PublishAsync();
        await PushToBlindAsync();
        await _blind.Services.GetRequiredService<BlindReplicaPackageCache>().DisposeAsync();
        await phone.Pull.SyncOnceAsync(http, phone.Target, CancellationToken.None);

        var state = new BlindPhoneState(new MemoryPhoneStore()) { NodeId = phone.Identity.NodeId, CallCode = phone.Target };
        var scopes = phone.Services.GetRequiredService<IServiceScopeFactory>();
        var source = new BlindPhonePackageSource(new FetchWith(phone.Replica, http, phone.Target, work), scopes);
        var runner = new BlindPhoneBackupRunner(state, new FixedPhoneKeys(phone.BackupKey!), source,
            new BlindPhoneRecoverySetSource(phone.Db), new BlindPhoneLog(Path.Combine(phone.Dir, "log.jsonl"), TimeProvider.System),
            Path.Combine(phone.Dir, "backups"), TimeProvider.System);

        var outcome = await runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.Done, outcome.Message);
        using var target = new RecoveryTestFactory();
        var result = await target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(outcome.FilePath!, new RestoreIdentity("admin", "Restored PC", Password), CancellationToken.None, RestoreBoxPolicy.Default);
        result.Replay.Should().NotBeNull("the package is format 2: the restore replayed deltas over a baseline");
        result.Anchor.Found.Should().BeTrue();
        result.Anchor.MatchesAnchor.Should().BeTrue("the restored state equals what the anchor the phone received covers");
        (await target.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        using var restored = target.Services.CreateScope();
        var articles = restored.ServiceProvider.GetRequiredService<ArticleService>();
        (await articles.GetContentAsync(_seeded.ArticleIds[0])).Should().Be("the first body");
        (await articles.GetContentAsync(_seeded.ArticleIds[1])).Should().Be("the second body");
    }

    // ── the activated PC itself as the hub ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_activated_pc_serves_a_format2_replica_as_a_hub_from_its_newest_baseline()
    {
        _canaries = (await StepANode.SeedWithCanariesAsync(_pc)).Canaries;
        (await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        var baseline = await ProduceBaselineAsync();
        var listed = Format2Package.ParseBlobList(await File.ReadAllBytesAsync(Path.Combine(baseline.Directory, "blobs.txt")));
        listed.Should().NotBeEmpty("the vault's bodies are blobs");
        var phone = await CreatePhoneAsync(hubIsThePc: true);
        using var http = new HttpClient(_pc.Server.CreateHandler(), disposeHandler: false);

        await phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        // The hub's own scheduler also asks for a baseline at the activation, so the newest may be one made after the test's own.
        var newest = (await _pc.Services.GetRequiredService<IBaselineSource>().GetServableAsync())!.Files.Manifest.BaselineId;
        (await ScalarAsync<string>(phone, "SELECT baseline_id FROM tbl_baseline WHERE is_current = 1")).ToUpperInvariant().Should()
            .BeOneOf(baseline.Manifest.BaselineId.ToString().ToUpperInvariant(), newest.ToString().ToUpperInvariant(), "the hub served a baseline it produced");
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_article")).Should().Be(0);
        using (var conn = phone.Db.CreateConnection())
            (await conn.QueryAsync<string>("SELECT hash FROM tbl_blob WHERE hash IN @listed", new { listed })).Should().BeEquivalentTo(listed,
                "every blob of the baseline is on the copy before it adopted it");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(phone.Dir, _canaries).Should().BeEmpty();
        await phone.Replica.UpdateBaselineAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), null, CancellationToken.None);
        (await phone.Replica.UpdateBaselineAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), null, CancellationToken.None))
            .Should().BeFalse("once the copy holds the hub's newest baseline there is nothing newer");
    }

    [Fact]
    public async Task An_activated_pc_with_no_baseline_yet_answers_503_with_retry_after_never_format_1()
    {
        (await _pcClient.PostAsJsonAsync("/api/sync/activation", new { password = Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        var phone = await CreatePhoneAsync(hubIsThePc: true);
        using var http = new HttpClient(_pc.Server.CreateHandler(), disposeHandler: false);

        var act = () => phone.Replica.FetchAndInstallAsync(http, phone.Target, Path.Combine(phone.Dir, "replica"), progress: null, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ScalarAsync<long>(phone, "SELECT COUNT(*) FROM tbl_baseline")).Should().Be(0);
    }

    // ── seams of the AppCore composition ───────────────────────────────────────────────────────────

    private sealed class FetchWith(BlindPhoneReplicaClient client, HttpClient http, BlindCallCode target, string work) : IBlindVerifiedPackageFetcher
    {
        public Task<VerifiedReplicaPackage> FetchAsync(Action<long> beforeDownload, IProgress<double>? progress, CancellationToken ct) =>
            client.FetchVerifiedPackageAsync(http, target, work, progress, ct, beforeDownload);
    }

    private sealed class MemoryPhoneStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private sealed class FixedPhoneKeys(byte[] backupKey) : IBlindPhoneKeys
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => (byte[])backupKey.Clone();
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(seed, challengePayload);
    }

    private sealed class MemoryState : IBlindStateStore
    {
        private readonly Dictionary<string, string?> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class NoSecrets : IBlindSecretStore
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public byte[]? LoadBackupKey() => null;
        public byte[]? LoadPairingSecret() => null;
        public void SaveBackupKey(byte[] key) { }
        public void SavePairingSecret(byte[] secret) { }
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

    private sealed class NoLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }
}
