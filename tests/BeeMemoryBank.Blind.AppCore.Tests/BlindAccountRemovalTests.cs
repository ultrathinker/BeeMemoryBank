using System.Diagnostics;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>Removing an account: the courtesy call to the source, the drain of its work, what a failed step leaves, and the path checks before a folder is deleted.</summary>
public sealed class BlindAccountRemovalTests : IDisposable
{
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<(BlindAccountHost Host, string A, string B)> TwoAsync(BlindAccountHost? host = null)
    {
        host ??= _rig.Host();
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        return (host, a, b);
    }

    private BlindAccountEntry Entry(BlindAccountHost host, string id) => host.GetSnapshot().Accounts.Single(c => c.Entry.Id == id).Entry;

    private void Pair(BlindAccountHost host, string id, FakeBlindHub? hub = null)
    {
        hub ??= new FakeBlindHub("hub-" + id[..6] + ".test");
        host.AcceptCallCode(hub.CallCodeFor(_rig.Secrets.For(Entry(host, id)).LoadPairingSecret()!)).Should().BeNull();
    }

    // ---- the courtesy call -------------------------------------------------------------------------------------------------------------------

    private sealed class RecordingLeave : IBlindAccountLeaveHook
    {
        public List<(BlindAccountEntry Account, Guid? NodeId, bool KeysStillThere, bool Paired)> Calls { get; } = [];
        public Func<CancellationToken, Task>? Behaviour { get; set; }
        public InMemoryBlindSecretStoreFactory? Secrets { get; set; }

        public async Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
        {
            var state = context.Services.GetRequiredService<BlindPhoneState>();
            Calls.Add((context.Account, state.NodeId, Secrets?.For(context.Account).LoadIdentitySeed() is not null, state.CallCode is not null));
            if (Behaviour is { } behaviour) await behaviour(ct);
        }
    }

    [Fact]
    public async Task ThePairedAccountsHook_RunsBeforeAnythingIsDeleted_WithTheAccountsOwnServices()
    {
        var leave = new RecordingLeave { Secrets = _rig.Secrets };
        var (host, a, b) = await TwoAsync(_rig.Host(leave: leave));
        await using var _ = host;
        Pair(host, a);
        var node = host.Get<BlindPhoneState>(a).NodeId;

        await host.RemoveAsync(a);

        var call = leave.Calls.Should().ContainSingle().Subject;
        call.Account.Id.Should().Be(a);
        call.NodeId.Should().Be(node);
        call.KeysStillThere.Should().BeTrue("the hook may need the key to sign its call: keys are cleared after it");
        call.Paired.Should().BeTrue();
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
    }

    [Fact]
    public async Task AnAccountThatNeverPaired_HasNobodyToTell()
    {
        var leave = new RecordingLeave();
        var (host, a, _) = await TwoAsync(_rig.Host(leave: leave));
        await using var _ = host;

        await host.RemoveAsync(a);

        leave.Calls.Should().BeEmpty();
        host.GetSnapshot().Accounts.Should().ContainSingle();
    }

    [Fact]
    public async Task AHookThatFails_DoesNotHoldUpTheRemoval()
    {
        var leave = new RecordingLeave { Behaviour = _ => throw new HttpRequestException("the source is down") };
        var (host, a, b) = await TwoAsync(_rig.Host(leave: leave));
        await using var _ = host;
        Pair(host, a);

        await host.RemoveAsync(a);

        leave.Calls.Should().HaveCount(1);
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeFalse();
    }

    [Fact]
    public async Task AHookThatNeverReturns_IsGivenUpOn_AfterTheTimeout()
    {
        var leave = new RecordingLeave { Behaviour = ct => Task.Delay(Timeout.Infinite, ct) };
        var (host, a, _) = await TwoAsync(_rig.Host(leave: leave));
        await using var _ = host;
        Pair(host, a);
        var clock = Stopwatch.StartNew();

        await host.RemoveAsync(a).WaitAsync(TimeSpan.FromSeconds(30));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the rig's courtesy timeout is 300 ms");
        host.GetSnapshot().Accounts.Should().ContainSingle();
    }

    // ---- stopping the work -------------------------------------------------------------------------------------------------------------------

    private sealed class StubbornReplica : IBlindReplicaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Started.TrySetResult();
            await Finish.Task; // ignores the cancellation: a job slow to stop
        }
    }

    private sealed class Lifecycle : IBlindAccountLifecycle
    {
        public List<string> Calls { get; } = [];
        public void StopBackgroundWork(BlindAccountEntry account) => Calls.Add("stop:" + account.Id);
        public void StopBackupService(BlindAccountEntry account) => Calls.Add("stop-backup:" + account.Id);
        public void ResumeBackgroundWork(BlindAccountEntry account) => Calls.Add("resume:" + account.Id);
    }

    [Fact]
    public async Task AJobThatDoesNotStopInTime_StopsTheRemoval_NothingIsDeleted_AndTheAccountIsWhatItWas()
    {
        var replica = new StubbornReplica();
        var lifecycle = new Lifecycle();
        await using var host = _rig.Host(lifecycle: lifecycle, drain: TimeSpan.FromMilliseconds(250),
            overrides: (services, entry) => { if (entry.Name == "A") services.AddSingleton<IBlindReplicaSource>(replica); });
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        Pair(host, a);
        host.For(a).SetSchedule(BlindBackupSchedule.Off);
        var seed = _rig.Secrets.For(Entry(host, a)).LoadIdentitySeed();
        var load = host.For(a).RunHeavyAsync(false);
        await replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = () => host.RemoveAsync(a);

        (await act.Should().ThrowAsync<AggregateException>()).Which.Message.Should().Contain("did not stop in time").And.Contain("Nothing was deleted");
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeTrue();
        _rig.Secrets.For(Entry(host, a)).LoadIdentitySeed().Should().Equal(seed, "the keys are kept");
        Entry(host, a).Phase.Should().Be(BlindAccountPhases.Active, "the mark was put back: the account is what it was");
        lifecycle.Calls.Should().Contain(["stop:" + a, "stop-backup:" + a, "resume:" + a]);
        lifecycle.Calls.Should().NotContain("stop:" + b, "another account's work is not stopped");
        host.For(a).GetStatus().ActiveJob.Should().Be("First load", "the job goes on");
        _ = host.For(a);

        replica.Finish.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        await host.RemoveAsync(a);
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
    }

    // ---- a removal that cannot finish --------------------------------------------------------------------------------------------------------

    private sealed class FlakyClear(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public bool Fail { get; set; }

        public IBlindSecretStore For(BlindAccountEntry account) => new Store(this, inner.For(account));

        private sealed class Store(FlakyClear owner, IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed) => real.SaveIdentitySeed(seed);
            public byte[]? LoadIdentitySeed() => real.LoadIdentitySeed();
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => real.LoadBackupKey();
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => real.LoadPairingSecret();
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear()
            {
                if (owner.Fail) throw new UnauthorizedAccessException("the keystore is locked");
                real.Clear();
            }
        }
    }

    [Fact]
    public async Task KeysThatCannotBeCleared_KeepTheAccountListedAsDeleting_AndAnotherTryFinishesIt()
    {
        var flaky = new FlakyClear(_rig.Secrets);
        var (host, a, b) = await TwoAsync(_rig.Host(secrets: flaky));
        await using var _ = host;
        var entryA = Entry(host, a);
        flaky.Fail = true;

        var act = () => host.RemoveAsync(a);

        var failure = (await act.Should().ThrowAsync<AggregateException>()).Which;
        failure.Message.Should().Contain("forgetting the keys").And.Contain("not complete");
        failure.Message.Should().NotContain("seed", "no secret, only the step and the store's own words");
        var card = host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a);
        card.Entry.Phase.Should().Be(BlindAccountPhases.Deleting, "the entry stays until the keys are really gone, so that nothing is orphaned");
        card.Problem.Should().Contain("not complete");
        _rig.Secrets.BlobCount(entryA).Should().BeGreaterThan(0);

        flaky.Fail = false;
        await host.RemoveAsync(a);

        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        _rig.Secrets.BlobCount(entryA).Should().Be(0);
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeFalse();
    }

    [Fact]
    public async Task ADeletingAccount_IsFinishedByTheNextStart()
    {
        var flaky = new FlakyClear(_rig.Secrets);
        string a, b;
        BlindAccountEntry entryA;
        await using (var host = _rig.Host(secrets: flaky))
        {
            await host.StartAsync();
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
            entryA = Entry(host, a);
            flaky.Fail = true;
            var act = () => host.RemoveAsync(a);
            await act.Should().ThrowAsync<AggregateException>();
        }
        SqliteConnection.ClearAllPools();
        flaky.Fail = false;

        await using var restarted = _rig.Host(secrets: flaky);
        await restarted.StartAsync();

        restarted.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        _rig.Secrets.BlobCount(entryA).Should().Be(0);
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeFalse();
    }

    [Fact]
    public async Task ASecondRemoveOfTheSameAccount_JoinsTheFirst()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leave = new RecordingLeave { Behaviour = _ => gate.Task };
        var (host, a, _) = await TwoAsync(_rig.Host(leave: leave, leaveTimeout: TimeSpan.FromSeconds(30)));
        await using var _ = host;
        Pair(host, a);

        var first = host.RemoveAsync(a);
        var second = host.RemoveAsync(a);

        ReferenceEquals(first, second).Should().BeTrue();
        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(15));
        leave.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task RemovingAnAccountThatIsNotThere_IsAnError_AndAFreedSlotCanBeUsedAgain()
    {
        await using var host = _rig.Host(maxAccounts: 2);
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        (await host.AddAsync("B")).Ok.Should().BeTrue();
        (await host.AddAsync("C")).Ok.Should().BeFalse();

        var missing = () => host.RemoveAsync("0123456789abcdef0123456789abcdef");
        await missing.Should().ThrowAsync<KeyNotFoundException>();
        await host.RemoveAsync(a);

        (await host.AddAsync("C")).Ok.Should().BeTrue("the removed account's place is free again");
        host.GetSnapshot().Accounts.Select(c => c.Entry.Name).Should().Equal("B", "C");
    }

    [Fact]
    public async Task ARemovedAccount_IsGoneFromTheScreenAtOnce_AndTheScreenIsTold()
    {
        var (host, a, _) = await TwoAsync();
        await using var _ = host;
        var told = 0;
        host.Changed += () => Interlocked.Increment(ref told);

        await host.RemoveAsync(a);

        told.Should().BeGreaterThan(0);
        var act = () => host.For(a);
        act.Should().Throw<KeyNotFoundException>();
        var services = () => host.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>();
    }

    // ---- the checks before a folder is deleted -----------------------------------------------------------------------------------------------

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows without the symlink privilege: a junction is a link for these purposes and needs none.
            if (!OperatingSystem.IsWindows()) return false;
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            process!.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
    }

    [Fact]
    public void ALinkWhereTheAccountFolderShouldBe_IsRefused_NotFollowedAndNotRemoved()
    {
        var elsewhere = Path.Combine(_rig.Root, "somewhere-else");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "precious.txt"), "not an account's");
        var id = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts"));
        var link = Path.Combine(_rig.Root, "accounts", id);
        if (!TryLink(link, elsewhere)) return; // this machine cannot make a link; the check itself is exercised where it can (the Linux run)

        var act = () => BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x"));

        act.Should().Throw<IOException>().WithMessage("*link*");
        File.ReadAllText(Path.Combine(elsewhere, "precious.txt")).Should().Be("not an account's", "the delete never went through the link");
        Directory.Exists(link).Should().BeTrue("the link itself was left for a person");
    }

    [Fact]
    public void AnAccountsFolderThatIsItselfALink_IsRefused()
    {
        var elsewhere = Path.Combine(_rig.Root, "elsewhere");
        var id = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(elsewhere, id));
        File.WriteAllText(Path.Combine(elsewhere, id, "precious.txt"), "not an account's");
        var accounts = Path.Combine(_rig.Root, "accounts");
        if (!TryLink(accounts, elsewhere)) return;

        var act = () => BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x"));

        act.Should().Throw<IOException>().WithMessage("*link*");
        File.Exists(Path.Combine(elsewhere, id, "precious.txt")).Should().BeTrue();
    }

    [Fact]
    public void AFolderNameTakenByAFile_AndAnIdThatIsNotOurs_AreRefused()
    {
        var id = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts"));
        File.WriteAllText(Path.Combine(_rig.Root, "accounts", id), "a file");

        var occupied = () => BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x"));
        occupied.Should().Throw<IOException>();
        File.ReadAllText(Path.Combine(_rig.Root, "accounts", id)).Should().Be("a file");

        foreach (var bad in new[] { "..", "../x", "legacy", "ABC", "" })
        {
            var act = () => BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(bad, BlindAccountLayouts.Dir, "x"));
            act.Should().Throw<ArgumentException>(bad);
        }
    }

    [Fact]
    public void AMissingFolder_IsAlreadyRemoved_AndOnlyTheAccountsOwnFolderGoes()
    {
        var id = BlindAccountEntry.NewId();
        var neighbour = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts", id));
        Directory.CreateDirectory(Path.Combine(_rig.Root, "accounts", neighbour));
        File.WriteAllText(Path.Combine(_rig.Root, "accounts", neighbour, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(_rig.Root, "accounts", id, "gone.txt"), "x");

        BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x"));
        BlindAccountHost.DeleteAccountFolder(_rig.Root, new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x")); // again: nothing to do

        Directory.Exists(Path.Combine(_rig.Root, "accounts", id)).Should().BeFalse();
        File.Exists(Path.Combine(_rig.Root, "accounts", neighbour, "keep.txt")).Should().BeTrue();
    }
}
