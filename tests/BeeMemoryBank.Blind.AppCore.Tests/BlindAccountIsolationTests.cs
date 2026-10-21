using System.Collections.Concurrent;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Two accounts in ONE process, each against its own fake hub, each hub with a canary of its own: whatever one account is, holds or sends is nowhere in the other's
/// folder, log, state, requests or keys; removing one while the other syncs does not disturb it; the long-job lock is the device's, not the process's.
/// </summary>
public sealed class BlindAccountIsolationTests : IDisposable
{
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>One handler for the whole device that hands each request to the hub of its origin (a request for an origin no hub owns is a 404 the test would see).</summary>
    private sealed class Router(params FakeBlindHub[] hubs) : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> _byAuthority = hubs.ToDictionary(
            h => new Uri(h.Origin).Authority, h => new HttpMessageInvoker(h, disposeHandler: false), StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _byAuthority.TryGetValue(request.RequestUri!.Authority, out var invoker)
                ? invoker.SendAsync(request, ct)
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    /// <summary>The handler an account's pipeline sits on; disposing it (a pin change, a wipe) leaves the router and the hubs alone.</summary>
    private sealed class Forward(HttpMessageHandler target) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _invoker.SendAsync(request, ct);
    }

    private sealed class World
    {
        public required BlindAccountHost Host { get; init; }
        public required string A { get; init; }
        public required string B { get; init; }
        public required FakeBlindHub HubA { get; init; }
        public required FakeBlindHub HubB { get; init; }
        public required BlindAccountEntry EntryA { get; init; }
        public required BlindAccountEntry EntryB { get; init; }
        public string FolderA => Path.Combine(Root, "accounts", A);
        public string FolderB => Path.Combine(Root, "accounts", B);
        public required string Root { get; init; }
    }

    /// <summary>Two accounts, each paired with its own hub through the real call-code route, its database seeded as a first load leaves it.</summary>
    private async Task<World> TwoPairedAccountsAsync(Action<BlindAccountHostOptions>? configure = null)
    {
        var hubA = new FakeBlindHub("hub-a.test");
        var hubB = new FakeBlindHub("hub-b.test");
        var router = new Router(hubA, hubB);
        var host = _rig.Host(configure, handler: _ => () => new Forward(router));
        await host.StartAsync();
        var a = (await host.AddAsync("Friend A")).AccountId!;
        var b = (await host.AddAsync("Friend B")).AccountId!;
        var entryA = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == a).Entry;
        var entryB = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == b).Entry;
        host.AcceptCallCode(hubA.CallCodeFor(_rig.Secrets.For(entryA).LoadPairingSecret()!)).Should().BeNull();
        host.AcceptCallCode(hubB.CallCodeFor(_rig.Secrets.For(entryB).LoadPairingSecret()!)).Should().BeNull();
        await host.SeedHubAsync(a, hubA);
        await host.SeedHubAsync(b, hubB);
        return new World { Host = host, A = a, B = b, HubA = hubA, HubB = hubB, EntryA = entryA, EntryB = entryB, Root = _rig.Root };
    }

    /// <summary>Everything that identifies account <paramref name="who"/> and its hub: ids, keys, pin, canary.</summary>
    private List<(string Label, byte[] Needle)> Marks(World w, bool a)
    {
        var entry = a ? w.EntryA : w.EntryB;
        var hub = a ? w.HubA : w.HubB;
        var tag = a ? "A" : "B";
        var secrets = _rig.Secrets.For(entry);
        var marks = new List<(string, byte[])>();
        marks.AddRange(LeakScan.Needles(tag + " node id", w.Host.Get<BlindPhoneState>(entry.Id).NodeId!.Value));
        marks.AddRange(LeakScan.Needles(tag + " hub node id", hub.NodeId));
        marks.AddRange(LeakScan.Needles(tag + " hub canary", hub.Canary));
        marks.AddRange(LeakScan.Needles(tag + " pin", hub.Pin));
        marks.AddRange(LeakScan.Needles(tag + " hub key", hub.PublicKey));
        marks.AddRange(LeakScan.Needles(tag + " identity seed", secrets.LoadIdentitySeed()!));
        marks.AddRange(LeakScan.Needles(tag + " backup key", secrets.LoadBackupKey()!));
        marks.AddRange(LeakScan.Needles(tag + " public key", w.Host.Get<BlindPhoneState>(entry.Id).PublicKey!));
        marks.AddRange(LeakScan.Needles(tag + " hub origin", hub.Origin));
        return marks;
    }

    [Fact]
    public async Task TwoAccountsSyncAgainstTwoHubs_AndNothingOfOneIsAnywhereInTheOther()
    {
        var w = await TwoPairedAccountsAsync();
        await using var host = w.Host;
        w.HubA.AddFolder();
        w.HubA.AddFolder(w.HubA.Canary + "-second");
        w.HubB.AddFolder();

        (await host.For(w.A).RequestSyncAsync()).Should().Be("Synced.");
        (await host.For(w.B).RequestSyncAsync()).Should().Be("Synced.");

        // Each database holds its own hub's folders and no others.
        (await host.FolderNamesAsync(w.A)).Should().BeEquivalentTo(w.HubA.Canary, w.HubA.Canary + "-second");
        (await host.FolderNamesAsync(w.B)).Should().BeEquivalentTo(w.HubB.Canary);

        // Each hub was called by exactly its own account.
        w.HubA.Callers().Should().Equal(host.Get<BlindPhoneState>(w.A).NodeId!.Value);
        w.HubB.Callers().Should().Equal(host.Get<BlindPhoneState>(w.B).NodeId!.Value);
        w.HubA.Requests.Should().NotBeEmpty();
        w.HubB.Requests.Select(r => r.Url).Should().OnlyContain(u => u.StartsWith("https://hub-b.test:5610/", StringComparison.Ordinal), "an account only ever calls the hub it was paired with");

        var marksA = Marks(w, a: true);
        var marksB = Marks(w, a: false);

        // The canary of a fresh sync sits in the -wal: SQLite checkpoints it into the database only when the last connection closes, and a pooled connection is never really closed.
        // A pool-clear by a parallel test's Dispose closes those idle handles at any moment: the last close checkpoints the -wal and deletes it, and a scan that read the database a
        // moment before and the -wal a moment after sees the canary in NEITHER file (the positive control comes back empty). One clear here first: both accounts checkpoint, and the
        // folders stand still while they are scanned.
        SqliteConnection.ClearAllPools();

        // The positive control: the scan does find an account's own canary in its own folder (database or its -wal).
        LeakScan.InFolder(w.FolderA, LeakScan.Needles("A canary", w.HubA.Canary)).Should().NotBeEmpty("the scanner finds what is really there");
        LeakScan.InFolder(w.FolderB, LeakScan.Needles("B canary", w.HubB.Canary)).Should().NotBeEmpty();

        // Nothing of B in A's folder (database, WAL, log, state file, backups...), nothing of A in B's.
        LeakScan.InFolder(w.FolderA, marksB).Should().BeEmpty("account B left no mark in account A's folder");
        LeakScan.InFolder(w.FolderB, marksA).Should().BeEmpty("account A left no mark in account B's folder");

        // Nothing of B in any request A sent (to its hub, which is the only place it sends to), and the other way round.
        LeakScan.InText(w.HubA.AllRequestText(), marksB).Should().BeEmpty("no request of A carries anything of B");
        LeakScan.InText(w.HubB.AllRequestText(), marksA).Should().BeEmpty("no request of B carries anything of A");

        // Keys are not in any account's folder at all (they are in the host's secret store), nor in the device registry.
        var keys = marksA.Concat(marksB).Where(m => m.Label.Contains("seed") || m.Label.Contains("backup key")).ToList();
        LeakScan.InFolder(w.FolderA, keys).Should().BeEmpty();
        LeakScan.InFolder(w.FolderB, keys).Should().BeEmpty();
        var registryOnly = Path.Combine(_rig.Root, "registry-only");
        Directory.CreateDirectory(registryOnly);
        File.Copy(Path.Combine(_rig.Root, "accounts.json"), Path.Combine(registryOnly, "accounts.json"));
        LeakScan.InFolder(registryOnly, marksA.Concat(marksB)).Should().BeEmpty("the registry holds names and numbers, never ids of a mesh, keys or pins");

        // Nothing of B in A's state, and A's own state does hold its pairing (the control).
        var stateA = string.Join("\n", _rig.States.Snapshot(w.EntryA).Select(kv => kv.Key + "=" + kv.Value));
        var stateB = string.Join("\n", _rig.States.Snapshot(w.EntryB).Select(kv => kv.Key + "=" + kv.Value));
        stateA.Should().Contain("hub-a.test", "the control: A's own call code is in A's state (its address is URL-encoded in the code)");
        stateB.Should().Contain("hub-b.test");
        LeakScan.InText(stateA, marksB).Should().BeEmpty();
        LeakScan.InText(stateB, marksA).Should().BeEmpty();

        // The logs the screens show.
        string.Join("\n", host.For(w.A).GetStatus().RecentLog.Select(e => e.Message)).Should().NotContain(w.HubB.Origin).And.NotContain("hub-b.test");
        string.Join("\n", host.For(w.B).GetStatus().RecentLog.Select(e => e.Message)).Should().NotContain(w.HubA.Origin).And.NotContain("hub-a.test");

        // The keys of the two accounts are their own.
        _rig.Secrets.For(w.EntryA).LoadIdentitySeed().Should().NotEqual(_rig.Secrets.For(w.EntryB).LoadIdentitySeed());
        host.Get<BlindPhoneState>(w.A).CallCode!.NodeId.Should().Be(w.HubA.NodeId);
        host.Get<BlindPhoneState>(w.B).CallCode!.NodeId.Should().Be(w.HubB.NodeId);
    }

    [Fact]
    public async Task SyncingOneAccountAgainAndAgain_NeverReachesTheOthersHub()
    {
        var w = await TwoPairedAccountsAsync();
        await using var host = w.Host;
        w.HubA.AddFolder();

        for (var i = 0; i < 3; i++) (await host.For(w.A).RequestSyncAsync()).Should().Be("Synced.");

        w.HubB.Requests.Should().BeEmpty("account B was never asked to sync, and A never calls B's hub");
        (await host.FolderNamesAsync(w.B)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingB_WhileAIsSyncing_DoesNotDisturbA_AndLeavesNothingOfBBehind()
    {
        var w = await TwoPairedAccountsAsync();
        await using var host = w.Host;
        w.HubA.AddFolder();
        w.HubB.AddFolder();
        (await host.For(w.B).RequestSyncAsync()).Should().Be("Synced.");
        var seedA = _rig.Secrets.For(w.EntryA).LoadIdentitySeed();
        var nodeA = host.Get<BlindPhoneState>(w.A).NodeId;
        w.HubA.HoldEvents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var syncA = host.For(w.A).RequestSyncAsync();
        await w.HubA.EventsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.For(w.A).GetStatus().ActiveJob.Should().Be("Sync");

        await host.RemoveAsync(w.B).WaitAsync(TimeSpan.FromSeconds(30));

        syncA.IsCompleted.Should().BeFalse("A's round is still waiting for its hub: B's removal did not wait for it, and did not cancel it");
        // B is gone completely, A is whole.
        Directory.Exists(w.FolderB).Should().BeFalse();
        _rig.Secrets.BlobCount(w.EntryB).Should().Be(0, "B's keys are cleared");
        _rig.States.HasState(w.EntryB).Should().BeFalse("B's state is erased");
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(w.A);
        File.ReadAllText(Path.Combine(_rig.Root, "accounts.json")).Should().NotContain(w.B);
        var gone = () => host.For(w.B);
        gone.Should().Throw<KeyNotFoundException>();
        Directory.Exists(w.FolderA).Should().BeTrue();
        _rig.Secrets.For(w.EntryA).LoadIdentitySeed().Should().Equal(seedA);

        w.HubA.HoldEvents.SetResult();
        (await syncA.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("Synced.");
        (await host.FolderNamesAsync(w.A)).Should().Equal(w.HubA.Canary);
        host.Get<BlindPhoneState>(w.A).NodeId.Should().Be(nodeA);
        host.For(w.A).GetStatus().LastSyncAt.Should().NotBeNull();
    }

    // ---- the device's lock for a long job ----------------------------------------------------------------------------------------------------

    /// <summary>A first load that waits for the test.</summary>
    private sealed class HeldReplica : IBlindReplicaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;

        public async Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            Started.TrySetResult();
            await Finish.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task OneLongJobAtATime_OnTheWholeDevice_WhicheverAccountAsks()
    {
        var replicas = new ConcurrentDictionary<string, HeldReplica>();
        var hub = new FakeBlindHub("hub.test");
        await using var host = _rig.Host(overrides: (services, entry) => services.AddSingleton<IBlindReplicaSource>(replicas.GetOrAdd(entry.Id, _ => new HeldReplica())));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        foreach (var id in new[] { a, b })
        {
            var entry = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == id).Entry;
            host.AcceptCallCode(hub.CallCodeFor(_rig.Secrets.For(entry).LoadPairingSecret()!)).Should().BeNull();
            host.For(id).SetSchedule(BlindBackupSchedule.Off);
        }

        var loadA = host.For(a).RunHeavyAsync(false);
        await replicas[a].Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await host.For(b).RunHeavyAsync(false)).Should().Be("Already running.", "the device's one long-job lock is held by account A");
        replicas[b].Starts.Should().Be(0);

        replicas[a].Finish.SetResult();
        await loadA.WaitAsync(TimeSpan.FromSeconds(10));
        replicas[b].Finish.SetResult();
        (await host.For(b).RunHeavyAsync(false).WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("Nothing due.");
        replicas[b].Starts.Should().Be(1);
        host.For(a).GetStatus().InitialLoadDone.Should().BeTrue();
        host.For(b).GetStatus().InitialLoadDone.Should().BeTrue();
    }

    [Fact]
    public async Task ASlowSecretStoreOfOneAccount_DoesNotHoldUpTheStartOfAnother()
    {
        // The identity gate used to be a static: one lock for the process, so one account's slow start kept every other account's waiting.
        var blocking = new BlockingFirstSeedSecrets(_rig.Secrets);
        await using var host = _rig.Host(secrets: blocking);
        await host.StartAsync();

        var slow = host.AddAsync("slow");
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        slow.IsCompleted.Should().BeFalse("the first account is stuck inside its secret store");

        var other = await host.AddAsync("other").WaitAsync(TimeSpan.FromSeconds(10));

        other.Ok.Should().BeTrue("a second account makes its identity while the first is still waiting for its keystore");
        slow.IsCompleted.Should().BeFalse();
        blocking.Release.SetResult();
        (await slow.WaitAsync(TimeSpan.FromSeconds(10))).Ok.Should().BeTrue();
        host.GetSnapshot().Accounts.Should().HaveCount(2).And.OnlyContain(c => c.Status != null && c.Status.NodeId != null);
    }

    /// <summary>The first identity seed to be saved waits for the test; every later one goes straight through.</summary>
    private sealed class BlockingFirstSeedSecrets(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        private int _first;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IBlindSecretStore For(BlindAccountEntry account) => new Store(this, inner.For(account));

        private sealed class Store(BlockingFirstSeedSecrets owner, IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed)
            {
                if (Interlocked.Exchange(ref owner._first, 1) == 0)
                {
                    owner.Entered.TrySetResult();
                    owner.Release.Task.GetAwaiter().GetResult();
                }
                real.SaveIdentitySeed(seed);
            }

            public byte[]? LoadIdentitySeed() => real.LoadIdentitySeed();
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => real.LoadBackupKey();
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => real.LoadPairingSecret();
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() => real.Clear();
        }
    }

    // ---- the gate, and the statics that are gone ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDeviceGate_AdmitsOne_AndReleasesOnce()
    {
        var gate = new BlindDeviceGate();
        gate.IsBusy.Should().BeFalse();

        var first = gate.TryEnter();
        first.Should().NotBeNull();
        gate.IsBusy.Should().BeTrue();
        gate.TryEnter().Should().BeNull("the second long job is refused at once, not queued");

        first!.Dispose();
        first.Dispose();
        gate.IsBusy.Should().BeFalse();
        var again = gate.TryEnter();
        again.Should().NotBeNull("a double dispose released the gate once, not twice");
        gate.TryEnter().Should().BeNull();
        again!.Dispose();
    }

    [Fact]
    public void TwoGates_AreTwoLocks_ACompositionWithoutOneGetsItsOwn()
    {
        var one = new BlindDeviceGate();
        var two = new BlindDeviceGate();
        using var held = one.TryEnter();
        two.TryEnter().Should().NotBeNull("a gate is not a process-wide lock");
    }

    [Fact]
    public void NoProcessStatic_RemainsThatWouldSerialiseOrCrossAccounts()
    {
        var appCore = Path.Combine(RepoRoot(), "libs", "BeeMemoryBank.Blind.AppCore");
        var sources = Directory.EnumerateFiles(appCore, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).ToList();
        sources.Should().NotBeEmpty();

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            System.Text.RegularExpressions.Regex.IsMatch(text, @"static\s+(readonly\s+)?SemaphoreSlim").Should().BeFalse(file + " holds a static lock: one lock for every account of the process");
        }

        var reset = File.ReadAllText(Path.Combine(appCore, "Services", "Blind", "BlindPhoneReset.cs"));
        reset.Should().NotContain("ClearAllPools", "the wipe closes only this account's pool");
        reset.Should().NotContain("SpecialFolder.LocalApplicationData", "an account always passes its own folder");
        File.ReadAllText(Path.Combine(appCore, "Core", "Services", "BlindPhone", "BlindHeavyWork.cs")).Should().NotContain("OneAtATime");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        return dir!;
    }
}
