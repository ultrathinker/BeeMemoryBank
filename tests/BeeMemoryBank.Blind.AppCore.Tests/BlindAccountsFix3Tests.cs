using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Hardening of the accounts, part 3: the record that an account was set up is written before its identity can exist, an unwritable record stops that account's start, and a
/// folder that is gone from an account that holds any trace of an identity is repaired by hand whatever the record says. Written against what round 2 had, so that they can be run before the fix.
/// </summary>
public sealed class BlindAccountsFix3Tests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountRig _rig = new();

    private string AccountsDir => Path.Combine(_rig.Root, "accounts");

    public void Dispose() => _rig.Dispose();

    private sealed class ScriptedCrash(Action<string> reach) : IBlindCrashPoints
    {
        public void Reach(string step) => reach(step);
    }

    private static BlindAccountEntry Dir(string id, string name = "x", int order = 0) => new(id, BlindAccountLayouts.Dir, name, Order: order);

    // ---- 5. the marker -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMarkerThatCannotBeWritten_StopsThatAccountsStart_SaysSo_AndTheNextStartTriesAgain()
    {
        var armed = false;
        var crash = new ScriptedCrash(step =>
        {
            if (step == "add:registered") armed = true;
            else if (armed && step == "registry:before-write") throw new IOException("There is not enough space on the disk.");
        });
        string id;
        await using (var host = _rig.Host(crash: crash, time: new FakeClock(Noon)))
        {
            await host.StartAsync();

            var added = await host.AddAsync("A");

            id = added.AccountId!;
            added.Ok.Should().BeFalse("the account was added to the list, and could not be started");
            var card = host.GetSnapshot().Accounts.Single();
            card.Problem.Should().Contain("could not be started").And.Contain("being set up");
            card.Status.Should().BeNull("no runtime holds an identity the list does not know of");
            card.Entry.Established.Should().BeFalse();
            ((Action)(() => host.ServicesFor(id))).Should().Throw<KeyNotFoundException>();
            Directory.GetFiles(Path.Combine(AccountsDir, id), "beememorybank.db*").Should().BeEmpty("nothing was initialised");
            _rig.Secrets.For(card.Entry).LoadIdentitySeed().Should().BeNull("no identity was made");
        }
        SqliteConnection.ClearAllPools();

        await using var next = _rig.Host(time: new FakeClock(Noon));
        await next.StartAsync();

        var started = next.GetSnapshot().Accounts.Single();
        started.Problem.Should().BeNull();
        started.Entry.Established.Should().BeTrue("the next start wrote the record, and then made the identity");
        started.Status!.NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task AnAccountThatHoldsAnIdentity_ButWhoseRecordWasNeverWritten_IsRepairedByHand_NotMadeAgain()
    {
        string id;
        await using (var host = _rig.Host(time: new FakeClock(Noon)))
        {
            await host.StartAsync();
            id = (await host.AddAsync("A")).AccountId!;
            // What a marker write that failed after the identity was made leaves (an account of an earlier version, or a disk that refused the write).
            host.Registry.Update(file => file with { Accounts = file.Entries.Select(e => e.Id == id ? e with { Established = false } : e).ToList() });
        }
        SqliteConnection.ClearAllPools();
        var entry = Dir(id);
        var seed = _rig.Secrets.For(entry).LoadIdentitySeed();
        seed.Should().NotBeNull("the account has made its identity");
        Directory.Delete(Path.Combine(AccountsDir, id), recursive: true); // the folder is lost

        await using var restarted = _rig.Host(time: new FakeClock(Noon));
        await restarted.StartAsync();

        var snapshot = restarted.GetSnapshot();
        snapshot.Accounts.Single().Problem.Should().Contain("gone");
        snapshot.RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.FolderMissing && i.AccountId == id);
        Directory.Exists(Path.Combine(AccountsDir, id)).Should().BeFalse("nothing was created: the record that was never written is no permission to start from nothing");
        _rig.Secrets.For(entry).LoadIdentitySeed().Should().Equal(seed, "its keys were not touched");
    }

    private sealed class UnreadableSecrets(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public IBlindSecretStore For(BlindAccountEntry account) => new Store(inner.For(account));

        private sealed class Store(IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed) => real.SaveIdentitySeed(seed);
            public byte[]? LoadIdentitySeed() => throw new IOException("the key store is not answering");
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => throw new IOException("the key store is not answering");
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => throw new IOException("the key store is not answering");
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() => real.Clear();
        }
    }

    [Fact]
    public async Task AKeyStoreThatCannotBeRead_IsATraceOfAnIdentity_SoAMissingFolderIsNotMadeAgain()
    {
        var id = BlindAccountEntry.NewId();
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: [Dir(id, "A", 1)])); // listed, never recorded as set up, and its folder is not there

        await using var host = _rig.Host(secrets: new UnreadableSecrets(_rig.Secrets), time: new FakeClock(Noon));
        await host.StartAsync();

        host.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.FolderMissing, "what cannot be read cannot be said to hold nothing");
        Directory.Exists(Path.Combine(AccountsDir, id)).Should().BeFalse();
    }

    [Fact]
    public async Task AnEntryThatWasNeverSetUpAndHoldsNothing_StillHasItsMissingFolderMade()
    {
        var id = BlindAccountEntry.NewId();
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: [Dir(id, "pending", 1)]));

        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();

        host.GetSnapshot().Accounts.Single().Problem.Should().BeNull("there is nothing in its keys or state to lose");
        Directory.Exists(Path.Combine(AccountsDir, id)).Should().BeTrue();
        host.GetSnapshot().Accounts.Single().Entry.Established.Should().BeTrue();
    }

    // ---- 6. a job that was let go of still holds the device's gate -----------------------------------------------------------------------------

    /// <summary>A first load that does not look at its token: it ends when the test says, and not before (a thread cannot be killed).</summary>
    private sealed class HangingReplica : IBlindReplicaSource
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) => Release.Task;
    }

    private sealed class QuickReplica : IBlindReplicaSource
    {
        public int Loads;

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AJobThatWasLetGoOf_HoldsTheRealGate_SoAnotherAccountsLongJobSaysWhy_WaitsOnABackOff_AndTheCardSaysSo()
    {
        var clock = new FakeClock(Noon);
        var hung = new HangingReplica();
        var quick = new QuickReplica();
        await using var host = _rig.Host(time: clock, overrides: (services, entry) =>
        {
            services.ReplacePullClient(new RecordingPullClient());
            services.AddSingleton<IBlindReplicaSource>(entry.Name == "A" ? hung : quick);
        });
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"), loaded: false);
        await host.PairAsync(_rig, b, new FakeBlindHub("hub-b.test"), loaded: false);
        var scheduler = new BlindDeviceScheduler(host, clock, new BlindDeviceSchedulerOptions
        {
            SyncTimeout = TimeSpan.FromMilliseconds(150),
            HeavyTimeout = TimeSpan.FromMilliseconds(150),
            StopGrace = TimeSpan.FromMilliseconds(100),
        });

        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(15)); // A's first load hangs and is let go of; B's long job then meets the gate

        quick.Loads.Should().Be(0, "the gate is held by A's job, which cannot be ended from outside");
        var cards = host.GetSnapshot().Accounts.ToDictionary(c => c.Entry.Id);
        cards[a].Problem.Should().Contain("did not stop").And.Contain("let go of");
        cards[b].Problem.Should().BeNull();
        var asked = await host.For(b).RunHeavyAsync(false);
        asked.Should().StartWith("Paused: the device is busy").And.Contain("A").And.NotBe("Already running.");
        var schedule = scheduler.ScheduleOf(b)!;
        schedule.RetryAttempts.Should().Be(0, "waiting for a gate that cannot be freed is not a failed first load: no back-off of failures grows");
        schedule.HeavyRetryAt.Should().NotBeNull("it is asked again after a wait of its own, not at once and not never");
        schedule.HeavyRetryAt.Should().BeAfter(clock.GetUtcNow());

        hung.Release.SetResult(); // A's job finally ends
        for (var i = 0; i < 300 && quick.Loads == 0; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(20)); // past any wait: the next pass may find the gate not yet free (A's job is still winding up), and then waits again
            await scheduler.TickAsync();
            await Task.Delay(20);
        }

        quick.Loads.Should().Be(1, "the gate is free: B's first load ran");
        host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a).Problem.Should().BeNull("A's job ended: its card is as it was");
        SqliteConnection.ClearAllPools();
    }

    // ---- 8. every occupied valid-id path is said; the courtesy call may come twice ------------------------------------------------------------

    [Fact]
    public async Task ARegularFileWithAnAccountsNameUnderAccounts_IsReportedWithAKindOfItsOwn_NotSkipped()
    {
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: []));
        var id = BlindAccountEntry.NewId();
        Directory.CreateDirectory(AccountsDir);
        File.WriteAllText(Path.Combine(AccountsDir, id), "not a folder");

        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();

        var snapshot = host.GetSnapshot();
        var issue = snapshot.RepairIssues.Should().ContainSingle().Subject;
        issue.Kind.Should().Be("file-in-place-of-folder");
        issue.AccountId.Should().Be(id);
        issue.CanAdopt.Should().BeFalse();
        issue.CanForget.Should().BeFalse();
        snapshot.RegistryProblem.Should().NotBeNull("data the list does not name stops everything, as for an unlisted folder");
        (await host.AdoptFolderAsync(id)).Should().NotBeNull("a file is not an account that can be adopted");
        File.ReadAllText(Path.Combine(AccountsDir, id)).Should().Be("not a folder", "it was left where it was");
    }

    private sealed class CountingLeave : IBlindAccountLeaveHook
    {
        public List<string> Calls { get; } = [];

        public Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
        {
            lock (Calls) Calls.Add(context.Account.Id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ARemovalCutOffAfterTheCourtesyCall_IsFinishedAtTheNextStart_AndTheSourceIsToldAgain()
    {
        var hook = new CountingLeave();
        string a;
        await using (var host = _rig.Host(crash: new CrashAtNamed("remove:left"), leave: hook, time: new FakeClock(Noon)))
        {
            await host.StartAsync();
            a = (await host.AddAsync("A")).AccountId!;
            await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"), loaded: false);

            var cutOff = await Record.ExceptionAsync(() => host.RemoveAsync(a));

            cutOff.Should().BeOfType<SimulatedCrash>("the process dies right after the courtesy call");
            hook.Calls.Should().Equal(a);
        }
        SqliteConnection.ClearAllPools();

        await using var next = _rig.Host(leave: hook, time: new FakeClock(Noon));
        await next.StartAsync();

        hook.Calls.Should().Equal(a, a).And.HaveCount(2, "nothing remembers the first call: the hook is called again, and it is idempotent at the source by contract");
        next.GetSnapshot().Accounts.Should().BeEmpty("the removal was finished");
        Directory.Exists(Path.Combine(AccountsDir, a)).Should().BeFalse();
    }

    // ---- 9. what the app opens by name inside an account's folder is not a link -------------------------------------------------------------

    public static TheoryData<string, bool> OpenedByName() => new()
    {
        { "beememorybank.db", false },
        { "beememorybank.db-wal", false },
        { "state.json", false },
        { "blind-log.jsonl", false },
        { "usage.json", false },
        { "media", true },
        { "blind-replica", true },
        { "blind-backups", true },
        { "baselines", true },
    };

    [Theory]
    [MemberData(nameof(OpenedByName))]
    public async Task AChildThatTheAppOpensByName_AndThatIsALink_IsThatAccountsRepairState_AndNothingIsOpenedThroughIt(string name, bool folder)
    {
        string a, b;
        await using (var first = _rig.Host(time: new FakeClock(Noon)))
        {
            await first.StartAsync();
            a = (await first.AddAsync("A")).AccountId!;
            b = (await first.AddAsync("B")).AccountId!;
        }
        SqliteConnection.ClearAllPools();
        var child = Path.Combine(AccountsDir, a, name);
        var elsewhere = Path.Combine(_rig.Root, "elsewhere-" + name);
        if (folder)
        {
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "outside");
        }
        else
        {
            File.WriteAllText(elsewhere, "outside");
        }
        // The account's own child is taken away (it is the test's data) and a link takes its place.
        if (File.Exists(child)) File.Delete(child);
        else if (Directory.Exists(child)) Directory.Delete(child, recursive: true);
        var made = folder ? TestLinks.TryCreateDirectoryLink(child, elsewhere) : TestLinks.TryCreateFileLink(child, elsewhere);
        if (!made) return; // this machine cannot make that kind of link; the Linux run carries the check

        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();

        var snapshot = host.GetSnapshot();
        var cardA = snapshot.Accounts.Single(c => c.Entry.Id == a);
        cardA.Problem.Should().Contain("is a link");
        cardA.Status.Should().BeNull("no runtime was composed over what a link leads to");
        snapshot.RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.UnsafePath && i.AccountId == a);
        snapshot.Accounts.Single(c => c.Entry.Id == b).Problem.Should().BeNull("the other account is not affected");
        snapshot.Accounts.Single(c => c.Entry.Id == b).Status.Should().NotBeNull();
        TestLinks.IsLink(child).Should().BeTrue("the link was left where it was, for a person");
        (folder ? File.ReadAllText(Path.Combine(elsewhere, "keep.txt")) : File.ReadAllText(elsewhere)).Should().Be("outside", "nothing was written through the link");
    }
}
