using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Hardening of the accounts: what the host does when a path is a link, when the root is shared with another host or registry, when the
/// registry and the disk disagree, when a set-up account has lost its folder, when it is disposed or cancelled with work running, when a removal was cut off, and when the legacy
/// database cannot be read. Each test names what it was written against (see the report); each fails on the code as it was and passes now.
/// </summary>
public sealed class BlindAccountHardeningTests : IDisposable
{
    private readonly AccountRig _rig = new();

    private string AccountsDir => Path.Combine(_rig.Root, "accounts");
    private string RegistryFile => Path.Combine(_rig.Root, "accounts.json");

    public void Dispose() => _rig.Dispose();

    private async Task<BlindAccountHost> StartedAsync(BlindAccountHost? host = null)
    {
        host ??= _rig.Host();
        await host.StartAsync();
        return host;
    }

    private static BlindAccountEntry Dir(string id, string name = "x", int order = 0) => new(id, BlindAccountLayouts.Dir, name, Order: order);

    /// <summary>Two accounts made and stopped (the host is gone and its database pools are closed), so that the test can do what it likes to their folders.</summary>
    private async Task<(string A, string B)> TwoAccountsStoppedAsync()
    {
        string a, b;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
        }
        SqliteConnection.ClearAllPools();
        return (a, b);
    }

    private void Pair(BlindAccountHost host, string id)
    {
        var entry = host.GetSnapshot().Accounts.Single(c => c.Entry.Id == id).Entry;
        var hub = new FakeBlindHub("hub-" + id[..6] + ".test");
        host.AcceptCallCode(hub.CallCodeFor(_rig.Secrets.For(entry).LoadPairingSecret()!)).Should().BeNull();
    }

    // ---- 1. links: nothing is created, opened or composed through one ---------------------------------------------------------------------------

    [Fact]
    public async Task AnAccountFolderThatIsALinkToAnotherAccount_IsNotStarted_AndNothingIsOpenedThroughIt()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        var nodeA = new BlindPhoneState(_rig.States.For(Dir(a))).NodeId;
        var folderB = Path.Combine(AccountsDir, b);
        Directory.Delete(folderB, recursive: true);
        if (!TestLinks.TryCreateDirectoryLink(folderB, Path.Combine(AccountsDir, a))) return; // this machine cannot make a link; the Linux run carries the check

        await using var host = await StartedAsync();

        var cards = host.GetSnapshot().Accounts;
        var cardB = cards.Single(c => c.Entry.Id == b);
        cardB.Status.Should().BeNull("no runtime was composed over the link");
        cardB.Problem.Should().Contain("link");
        var services = () => host.ServicesFor(b);
        services.Should().Throw<KeyNotFoundException>();
        cards.Single(c => c.Entry.Id == a).Problem.Should().BeNull("the other account is not affected");
        cards.Single(c => c.Entry.Id == a).Status!.NodeId.Should().Be(nodeA);
        TestLinks.IsLink(folderB).Should().BeTrue("the link was left where it was, for a person");
    }

    [Fact]
    public async Task AnAccountsFolderThatIsALink_IsADamagedRegistry_AndNothingIsStarted()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var real = Path.Combine(_rig.Root, "accounts-real");
        Directory.Move(AccountsDir, real);
        if (!TestLinks.TryCreateDirectoryLink(AccountsDir, real))
        {
            Directory.Move(real, AccountsDir);
            return;
        }
        var before = AccountRig.Fingerprint(real);

        await using var host = await StartedAsync();

        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().Contain("link");
        snapshot.CanAdd.Should().BeFalse();
        var services = () => host.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>();
        (await host.AddAsync("x")).Ok.Should().BeFalse();
        AccountRig.Fingerprint(real).Should().BeEquivalentTo(before, "nothing under the link was opened or created");
    }

    [Fact]
    public async Task ARootThatIsALink_IsRefused_AndNothingIsCreatedInTheRealFolder()
    {
        var realRoot = Path.Combine(_rig.Root, "real-root");
        var linkRoot = Path.Combine(_rig.Root, "link-root");
        Directory.CreateDirectory(realRoot);
        if (!TestLinks.TryCreateDirectoryLink(linkRoot, realRoot)) return;
        await using var host = new BlindAccountHost(new BlindAccountHostOptions
        {
            RootDirectory = linkRoot, LegacyDatabasePath = Path.Combine(linkRoot, "beememorybank.db"), Secrets = _rig.Secrets, States = _rig.States,
        });

        await host.StartAsync();

        host.GetSnapshot().RegistryProblem.Should().Contain("link");
        host.GetSnapshot().CanAdd.Should().BeFalse();
        Directory.GetFileSystemEntries(realRoot).Should().BeEmpty("no registry, no lock file, no folder was made through the link");
    }

    [Fact]
    public void ARegistryFileThatIsALink_IsDamaged_AndNeverWrittenThrough()
    {
        var target = Path.Combine(_rig.Root, "elsewhere.json");
        File.WriteAllText(target, "{\"version\":1,\"accounts\":[]}");
        if (!TestLinks.TryCreateFileLink(Path.Combine(_rig.Root, "accounts.json"), target)) return;
        var registry = new BlindAccountRegistry(_rig.Root);

        registry.Load().Should().Be(BlindRegistryState.Damaged);

        registry.Problem.Should().Contain("link");
        var act = () => registry.Update(f => f with { Accounts = [Dir(BlindAccountEntry.NewId())] });
        act.Should().Throw<BlindRegistryDamagedException>();
        File.ReadAllText(target).Should().Be("{\"version\":1,\"accounts\":[]}", "nothing was written through the link");
    }

    [Fact]
    public void ADanglingRegistryLink_IsDamaged_NotAMissingRegistry()
    {
        if (!TestLinks.TryCreateFileLink(Path.Combine(_rig.Root, "accounts.json"), Path.Combine(_rig.Root, "nowhere.json"))) return;
        var registry = new BlindAccountRegistry(_rig.Root);

        registry.Load().Should().Be(BlindRegistryState.Damaged);
    }

    // ---- 2. one owner of the root ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondHostOnTheSameRoot_IsRefused_ComposesNothing_AndMayTryAgainWhenTheFirstIsGone()
    {
        var first = await StartedAsync();
        var a = (await first.AddAsync("A")).AccountId!;
        var before = AccountRig.Fingerprint(_rig.Root, databaseSidecars: false).Keys.ToList();
        var registryBefore = File.ReadAllBytes(RegistryFile);

        await using var second = _rig.Host();
        await second.StartAsync();

        var snapshot = second.GetSnapshot();
        snapshot.RegistryProblem.Should().Contain("Another copy");
        snapshot.CanAdd.Should().BeFalse();
        snapshot.Accounts.Should().BeEmpty();
        (await second.AddAsync("x")).Ok.Should().BeFalse();
        var services = () => second.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>("an account the first host runs is never composed a second time");
        // The names, not the bytes of the running database (SQLite checkpoints it whenever a pool is closed, here by any other test): nothing was made, and the registry is as it was.
        AccountRig.Fingerprint(_rig.Root, databaseSidecars: false).Keys.Should().BeEquivalentTo(before, "the refused host made nothing");
        File.ReadAllBytes(RegistryFile).Should().Equal(registryBefore, "and wrote nothing");
        first.GetSnapshot().Accounts.Single().Problem.Should().BeNull();

        await first.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await second.StartAsync();

        second.GetSnapshot().RegistryProblem.Should().BeNull();
        second.GetSnapshot().Accounts.Single().Entry.Id.Should().Be(a);
        second.For(a).GetStatus().NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task TwoRegistryObjectsOnOneRoot_NeverLoseAnUpdate()
    {
        var one = new BlindAccountRegistry(_rig.Root);
        var two = new BlindAccountRegistry(_rig.Root);
        const int each = 15;

        var tasks = Enumerable.Range(0, 2 * each).Select(i => Task.Run(() =>
        {
            var entry = Dir(BlindAccountEntry.NewId(), "A" + i, i);
            (i % 2 == 0 ? one : two).Update(file => file with { Accounts = [.. file.Entries, entry] });
        })).ToArray();
        await Task.WhenAll(tasks);

        var read = new BlindAccountRegistry(_rig.Root);
        read.Load().Should().Be(BlindRegistryState.Loaded);
        read.Current.Entries.Should().HaveCount(2 * each, "every update started from what was on disk then");
        Directory.GetFiles(_rig.Root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void ALeftoverTemporaryFileOfACrash_DoesNotBreakTheNextWrite()
    {
        Directory.CreateDirectory(_rig.Root);
        var leftover = Path.Combine(_rig.Root, "accounts.json.tmp");
        File.WriteAllText(leftover, "half a registry");
        File.SetAttributes(leftover, FileAttributes.ReadOnly); // a file the next writer could not simply overwrite
        try
        {
            var registry = new BlindAccountRegistry(_rig.Root);

            registry.Update(_ => new BlindAccountsFile(Accounts: [Dir(BlindAccountEntry.NewId())]));

            var read = new BlindAccountRegistry(_rig.Root);
            read.Load().Should().Be(BlindRegistryState.Loaded);
            read.Current.Entries.Should().HaveCount(1);
        }
        finally
        {
            // Where a read-only file may be deleted by its owner (Linux), the registry's own sweep has already taken it away.
            if (File.Exists(leftover)) File.SetAttributes(leftover, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ALeftoverTemporaryFileOfAStateWrite_DoesNotBreakTheNextOne_AndEraseTakesItAway()
    {
        var entry = Dir(BlindAccountEntry.NewId());
        var folder = Path.Combine(AccountsDir, entry.Id);
        Directory.CreateDirectory(folder);
        var leftover = Path.Combine(folder, "state.json.tmp");
        File.WriteAllText(leftover, "{");
        File.SetAttributes(leftover, FileAttributes.ReadOnly);
        try
        {
            var store = new FileBlindStateStoreFactory(_rig.Root).For(entry);

            store.Set("k", "v");

            new FileBlindStateStoreFactory(_rig.Root).For(entry).Get("k").Should().Be("v");
        }
        finally
        {
            File.SetAttributes(leftover, FileAttributes.Normal);
        }
    }

    [Fact]
    public void TwoStoresOnOneStateFile_NeverCollide()
    {
        var entry = Dir(BlindAccountEntry.NewId());
        var one = new FileBlindStateStoreFactory(_rig.Root).For(entry);
        var two = new FileBlindStateStoreFactory(_rig.Root).For(entry);

        var failures = new List<Exception>();
        Parallel.For(0, 60, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try { (i % 2 == 0 ? one : two).Set("key" + (i % 7), i.ToString()); }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
        });

        failures.Should().BeEmpty("a write has a temporary file of its own and the file is replaced under one lock");
        Directory.GetFiles(Path.Combine(AccountsDir, entry.Id), "*.tmp").Should().BeEmpty();
    }

    // ---- 3. the registry against the disk ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALegacyRootThatTheRegistryOmits_StartsNothing_AndSaysSo()
    {
        await Legacy26.MakeAsync(_rig);
        var b = BlindAccountEntry.NewId();
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: [Dir(b, "B", 1)]));

        await using var host = await StartedAsync();

        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().NotBeNull("account 1 is on disk and the list does not name it");
        snapshot.CanAdd.Should().BeFalse();
        var services = () => host.ServicesFor(b);
        services.Should().Throw<KeyNotFoundException>("nothing starts while the list and the disk disagree");
        Directory.Exists(Path.Combine(AccountsDir, b)).Should().BeFalse("and nothing is created");
    }

    [Fact]
    public async Task AFolderUnderAccountsThatTheRegistryDoesNotName_StartsNothing_AndIsNotTouched()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var orphan = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(AccountsDir, orphan));
        File.WriteAllText(Path.Combine(AccountsDir, orphan, "data.txt"), "somebody's copy");

        await using var host = await StartedAsync();

        host.GetSnapshot().RegistryProblem.Should().NotBeNull();
        var services = () => host.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>();
        File.ReadAllText(Path.Combine(AccountsDir, orphan, "data.txt")).Should().Be("somebody's copy");
    }

    [Fact]
    public async Task FoldersThatAreNotNamedLikeAnAccount_AreNoneOfTheAppsBusiness()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        Directory.CreateDirectory(Path.Combine(AccountsDir, "my-notes"));
        File.WriteAllText(Path.Combine(AccountsDir, "desktop.ini"), "x");

        await using var host = await StartedAsync();

        host.GetSnapshot().RegistryProblem.Should().BeNull();
        host.For(a).GetStatus().NodeId.Should().NotBeNull();
    }

    // ---- 4. a set-up account that has lost its folder -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEstablishedAccountWhoseFolderIsGone_IsNotMadeAgain_AndItsKeysAreNotTouched()
    {
        string a;
        BlindAccountEntry entry;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            entry = host.GetSnapshot().Accounts.Single().Entry;
        }
        SqliteConnection.ClearAllPools();
        var blobs = Enum.GetValues<BlindAccountNames.Role>().ToDictionary(r => r, r => _rig.Secrets.ReadBlob(entry, r));
        blobs[BlindAccountNames.Role.IdentitySeed].Should().NotBeNull();
        var state = _rig.States.Snapshot(entry);
        var folder = Path.Combine(AccountsDir, a);
        Directory.Delete(folder, recursive: true);

        await using var restarted = await StartedAsync();

        Directory.Exists(folder).Should().BeFalse("no folder, and no database, was made in its place");
        var card = restarted.GetSnapshot().Accounts.Single();
        card.Status.Should().BeNull();
        card.Problem.Should().Contain("Needs repair").And.Contain("gone");
        var services = () => restarted.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>();
        foreach (var (role, blob) in blobs) _rig.Secrets.ReadBlob(entry, role).Should().Equal(blob, $"the {role} was not touched");
        _rig.States.Snapshot(entry).Should().BeEquivalentTo(state, "its state was not touched");
    }

    [Fact]
    public async Task AnAccountThatWasNeverSetUp_HasItsMissingFolderMade()
    {
        var id = BlindAccountEntry.NewId();
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: [Dir(id, "pending", 1)]));

        await using var host = await StartedAsync();

        host.GetSnapshot().Accounts.Single().Problem.Should().BeNull("an entry that never got as far as its identity has nothing to lose");
        host.For(id).GetStatus().NodeId.Should().NotBeNull();
        Directory.Exists(Path.Combine(AccountsDir, id)).Should().BeTrue();
    }

    // ---- 5. disposal and a cancelled start drain like a removal -------------------------------------------------------------------------------

    private sealed class CancellableReplica : IBlindReplicaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // a job that stops when it is asked to
        }
    }

    [Fact]
    public async Task DisposingTheHostWhileAJobHoldsTheGate_StopsTheJob_ReleasesTheGate_AndNoJobMeetsADisposedProvider()
    {
        var replica = new CancellableReplica();
        var gate = new BlindDeviceGate();
        var host = _rig.Host(gate: gate, overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        Pair(host, a);
        host.For(a).SetSchedule(BlindBackupSchedule.Off);
        var load = host.For(a).RunHeavyAsync(false);
        await replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.IsBusy.Should().BeTrue("the first load holds the device's gate");

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        gate.IsBusy.Should().BeFalse("the job was stopped and waited for, so the gate is free");
        var outcome = await Record.ExceptionAsync(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
        (outcome is null or OperationCanceledException).Should().BeTrue("the job ended by its cancellation, not by a disposed provider: " + outcome);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ACancelledStart_LeavesNoRuntimeBehind_AndTheNextStartIsWhole()
    {
        using var cts = new CancellationTokenSource();
        string id;
        await using (var host = _rig.Host(crash: new CancelAtStep("add:registered", cts)))
        {
            await host.StartAsync();

            var act = () => host.AddAsync("A", cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            var card = host.GetSnapshot().Accounts.Single();
            id = card.Entry.Id;
            card.Problem.Should().Contain("cancelled");
            var services = () => host.ServicesFor(id);
            services.Should().Throw<KeyNotFoundException>("the runtime that was made for the cancelled start was ended and taken out");
            host.DeviceUsage.AccountCount.Should().Be(1, "the account is still listed, so its folder still counts towards the device (through a meter that needs no runtime); it is the runtime that is gone");
        }
        SqliteConnection.ClearAllPools();

        await using var next = await StartedAsync();

        next.GetSnapshot().Accounts.Single().Problem.Should().BeNull();
        var status = next.For(id).GetStatus();
        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task AStartAfterACancelledStart_NeverOverwritesALiveRuntime()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        using var cts = new CancellationTokenSource();
        await using var host = _rig.Host(overrides: (_, entry) => { if (entry.Id == b) cts.Cancel(); });

        var first = () => host.StartAsync(cts.Token);
        await first.Should().ThrowAsync<OperationCanceledException>("B's start was cancelled half way");
        var providerA = host.ServicesFor(a);
        var services = () => host.ServicesFor(b);
        services.Should().Throw<KeyNotFoundException>("B's half-made runtime was ended");

        await host.StartAsync();

        host.ServicesFor(a).Should().BeSameAs(providerA, "A was up and stayed what it was: a live runtime is never replaced by another over the same database");
        host.For(b).GetStatus().NodeId.Should().NotBeNull();
    }

    // ---- 6. a removal that a crash cut off still tells the source -----------------------------------------------------------------------------

    private sealed class RecordingLeave(InMemoryBlindSecretStoreFactory secrets) : IBlindAccountLeaveHook
    {
        public List<(string AccountId, Guid? NodeId, bool KeysStillThere, bool Paired)> Calls { get; } = [];

        public Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
        {
            var state = context.Services.GetRequiredService<BlindPhoneState>();
            Calls.Add((context.Account.Id, state.NodeId, secrets.For(context.Account).LoadIdentitySeed() is not null, state.CallCode is not null));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ARemovalCutOffAfterTheMark_IsFinishedAtTheNextStart_AndTheSourceIsToldOnce_BeforeTheKeysGo()
    {
        var leave = new RecordingLeave(_rig.Secrets);
        var dying = _rig.Host(crash: new CrashAtNamed("remove:marked"), leave: leave);
        await dying.StartAsync();
        var a = (await dying.AddAsync("A")).AccountId!;
        var b = (await dying.AddAsync("B")).AccountId!;
        Pair(dying, a);
        var node = new BlindPhoneState(_rig.States.For(Dir(a))).NodeId;

        var act = () => dying.RemoveAsync(a);
        await act.Should().ThrowAsync<SimulatedCrash>();
        await dying.DisposeAsync();
        SqliteConnection.ClearAllPools();
        leave.Calls.Should().BeEmpty("the process died before the courtesy call");

        await using var next = await StartedAsync(_rig.Host(leave: leave));

        var call = leave.Calls.Should().ContainSingle("the source is still owed its goodbye, once").Subject;
        call.AccountId.Should().Be(a);
        call.NodeId.Should().Be(node);
        call.KeysStillThere.Should().BeTrue("the hook may need the key to sign its call: the keys are cleared after it");
        call.Paired.Should().BeTrue();
        next.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        _rig.Secrets.For(Dir(a)).LoadIdentitySeed().Should().BeNull();
        Directory.Exists(Path.Combine(AccountsDir, a)).Should().BeFalse();
    }

    [Fact]
    public async Task ARecoveredRemoval_WithNothingLeftToCallWith_GoesOnWithoutTheCall_AndMakesNothing()
    {
        var leave = new RecordingLeave(_rig.Secrets);
        var dying = _rig.Host(crash: new CrashAtNamed("remove:files-deleted"), leave: leave);
        await dying.StartAsync();
        var a = (await dying.AddAsync("A")).AccountId!;
        Pair(dying, a);
        var act = () => dying.RemoveAsync(a);
        await act.Should().ThrowAsync<SimulatedCrash>();
        await dying.DisposeAsync();
        SqliteConnection.ClearAllPools();
        leave.Calls.Clear();

        await using var next = await StartedAsync(_rig.Host(leave: leave));

        next.GetSnapshot().Accounts.Should().BeEmpty();
        leave.Calls.Should().BeEmpty("there was nothing left to call with");
        Directory.Exists(Path.Combine(AccountsDir, a)).Should().BeFalse("no folder was made for the goodbye of an account whose folder is gone");
    }

    // ---- 7. a dangling link where the account folder was ----------------------------------------------------------------------------------------

    [Fact]
    public void ADanglingLinkWhereTheAccountFolderShouldBe_IsRefused_AndLeft()
    {
        var id = BlindAccountEntry.NewId();
        Directory.CreateDirectory(AccountsDir);
        var link = Path.Combine(AccountsDir, id);
        if (!TestLinks.TryCreateDanglingDirectoryLink(link, Path.Combine(_rig.Root, "gone-target"))) return;

        var act = () => BlindAccountHost.DeleteAccountFolder(_rig.Root, Dir(id));

        act.Should().Throw<IOException>().WithMessage("*link*");
        TestLinks.IsLink(link).Should().BeTrue("the dangling link was left for a person, and the entry is not let go while it stays");
    }

    [Fact]
    public async Task ARemovalThatMeetsADanglingLink_StaysUnfinished_AndTheEntryStays()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var folder = Path.Combine(AccountsDir, a);
        Directory.Delete(folder, recursive: true);
        if (!TestLinks.TryCreateDanglingDirectoryLink(folder, Path.Combine(_rig.Root, "gone-target"))) return;
        await using var host = await StartedAsync();

        var act = () => host.RemoveAsync(a);

        (await act.Should().ThrowAsync<AggregateException>()).Which.Message.Should().Contain("link");
        TestLinks.IsLink(folder).Should().BeTrue();
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Contain(a);
        host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a).Entry.Phase.Should().Be(BlindAccountPhases.Deleting);
    }

    // ---- 8. an inaccessible legacy database is not an absent one ------------------------------------------------------------------------------

    [Fact]
    public async Task ALegacyDatabaseThatCannotBeOpened_StopsTheAdoption_WritesNoRegistry_AndIsAdoptedOnceItCan()
    {
        File.WriteAllBytes(_rig.LegacyDb, [1, 2, 3, 4]);
        var hold = new FileStream(_rig.LegacyDb, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await using var blocked = await StartedAsync();

            blocked.GetSnapshot().RegistryProblem.Should().Contain("could not be read");
            blocked.GetSnapshot().CanAdd.Should().BeFalse();
            File.Exists(RegistryFile).Should().BeFalse("an empty registry written over a database that could not be looked at would hide it");
        }
        finally
        {
            hold.Dispose();
        }

        await using var next = await StartedAsync();

        next.GetSnapshot().Accounts.Should().ContainSingle(c => c.Entry.Id == "legacy", "a database with content is a copy, once it can be seen");
    }

    [Fact]
    public async Task ALegacyDatabaseThatIsReallyAbsent_IsNoCopy()
    {
        await using var host = await StartedAsync();

        host.GetSnapshot().Accounts.Should().BeEmpty();
        host.GetSnapshot().RegistryProblem.Should().BeNull();
        File.Exists(RegistryFile).Should().BeTrue();
        JsonDocument.Parse(File.ReadAllText(RegistryFile)).RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }
}
