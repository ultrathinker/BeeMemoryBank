using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Hardening of the accounts, part 3, the blocker: the host gives the data root back only when nothing can still use it. Every test here has an operation that does NOT stop when it is asked to
/// (a thread cannot be killed) and that outlives the host's patience: no provider is disposed under it, no second host can take the root, and the shutdown finishes by itself when the
/// operation ends.
/// </summary>
public sealed class BlindHostShutdownFix3Tests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(300);
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>A first load that does not look at its token: it ends when the test says.</summary>
    private sealed class DeafReplica : IBlindReplicaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    /// <summary>The courtesy call of a removal, answered by a hook that never looks at its token.</summary>
    private sealed class DeafLeave : IBlindAccountLeaveHook
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        condition().Should().BeTrue(what);
    }

    private async Task SecondHostIsRefusedAsync()
    {
        await using var second = _rig.Host(time: new FakeClock(Noon));
        await second.StartAsync();
        second.GetSnapshot().RegistryProblem.Should().NotBeNull("the root is held: a second host is refused with a sentence");
        (await second.AddAsync("X")).Ok.Should().BeFalse();
        second.GetSnapshot().Accounts.Should().BeEmpty("the second host composed nothing");
    }

    [Fact]
    public async Task ALongJobThatDoesNotStop_IsNotDisposedUnder_AndNoSecondHostGetsTheRoot_UntilItEnds()
    {
        var replica = new DeafReplica();
        var host = _rig.Host(drain: Patience, time: new FakeClock(Noon), overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(replica));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"), loaded: false);
        host.For(a).SetSchedule(BlindBackupSchedule.Off);
        var services = host.ServicesFor(a);
        var activity = services.GetRequiredService<BlindActivity>();
        var load = host.For(a).RunHeavyAsync(false);
        await replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); // returns after its patience, with the job still running

        host.ShutdownIncomplete.Should().BeTrue();
        host.ShutdownFault.Should().Contain("still running");
        activity.ActiveCount.Should().Be(1, "the job is still running");
        var stillAlive = () => services.GetRequiredService<BlindActivity>();
        stillAlive.Should().NotThrow("the services the job is using were not disposed beneath it");
        await SecondHostIsRefusedAsync();

        replica.Release.SetResult(); // the job ends
        await load.WaitAsync(TimeSpan.FromSeconds(15));
        await EventuallyAsync(() => !host.ShutdownIncomplete, "the host finished its shutdown by itself once the work had ended");
        SqliteConnection.ClearAllPools();

        await using var third = _rig.Host(time: new FakeClock(Noon));
        await third.StartAsync();
        third.GetSnapshot().RegistryProblem.Should().BeNull("the root was given back");
        third.GetSnapshot().Accounts.Should().ContainSingle();
    }

    [Fact]
    public async Task ARemovalWaitingOnAHookThatNeverAnswers_KeepsTheRootAndItsRuntime_UntilItEnds()
    {
        var hook = new DeafLeave();
        var host = _rig.Host(drain: Patience, leave: hook, time: new FakeClock(Noon));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"), loaded: false);
        var services = host.ServicesFor(a);
        var removal = host.RemoveAsync(a);
        await hook.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        host.ShutdownIncomplete.Should().BeTrue("the removal is still under way");
        var stillAlive = () => services.GetRequiredService<BlindActivity>();
        stillAlive.Should().NotThrow("its runtime is not disposed under the removal that still needs it");
        await SecondHostIsRefusedAsync();

        hook.Release.SetResult();
        await removal.WaitAsync(TimeSpan.FromSeconds(15));
        await EventuallyAsync(() => !host.ShutdownIncomplete, "the shutdown finished once the removal had");
        SqliteConnection.ClearAllPools();

        await using var third = _rig.Host(time: new FakeClock(Noon));
        await third.StartAsync();
        third.GetSnapshot().RegistryProblem.Should().BeNull();
        third.GetSnapshot().Accounts.Should().BeEmpty("the removal that was left running completed: the entry, the keys and the files are gone");
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeFalse();
    }

    [Fact]
    public async Task AHostWhoseWorkAllStops_GivesTheRootBackAtOnce_AndRefusesEveryOperationAfterwards()
    {
        var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();
        await host.AddAsync("A");

        await host.DisposeAsync();

        host.ShutdownIncomplete.Should().BeFalse();
        SqliteConnection.ClearAllPools();
        await using (var next = _rig.Host(time: new FakeClock(Noon)))
        {
            await next.StartAsync();
            next.GetSnapshot().RegistryProblem.Should().BeNull("the root is free");
        }
        var add = () => host.AddAsync("B");
        await add.Should().ThrowAsync<ObjectDisposedException>("a host that is closed starts nothing");
        var start = () => host.StartAsync();
        await start.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task ARebuildAfterAWipe_IsNotComposedByAHostThatIsClosed()
    {
        var composed = 0;
        var host = _rig.Host(time: new FakeClock(Noon), overrides: (_, _) => Interlocked.Increment(ref composed));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var lifecycle = host.ServicesFor(a).GetRequiredService<IBlindLifecycle>();
        composed.Should().Be(1);
        await host.DisposeAsync();

        lifecycle.RestartAfterWipe(); // what a wipe that finishes a moment too late does
        await Task.Delay(500);

        composed.Should().Be(1, "nothing is composed once the host is closed");
        SqliteConnection.ClearAllPools();
    }
}
