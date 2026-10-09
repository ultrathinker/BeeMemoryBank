using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>The desktop lifecycle around "Disconnect and wipe": what stopping does, and what a refused wipe gives back.</summary>
public sealed class HostLifecycleTests
{
    private static BlindTimerScheduler NewScheduler() =>
        new(new FakeApp(), TimeProvider.System, new BlindSchedulerOptions { Tick = TimeSpan.FromMilliseconds(50) });

    [Fact]
    public async Task ResumeBackgroundWork_AfterARefusedWipe_BringsTheSchedulerBack()
    {
        var scheduler = NewScheduler();
        scheduler.EnsureScheduled();
        IBlindLifecycle lifecycle = new HostLifecycle(scheduler, () => { });

        lifecycle.StopBackgroundWork();   // what the wipe does first
        scheduler.IsRunning.Should().BeFalse();
        lifecycle.ResumeBackgroundWork(); // what it does when the work did not pause in time and nothing was deleted

        scheduler.IsRunning.Should().BeTrue("the copy must keep syncing while the person tries again");
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task ResumeBackgroundWork_WhenTheSchedulerRuns_DoesNotStartASecondLoop()
    {
        var scheduler = NewScheduler();
        scheduler.EnsureScheduled();
        IBlindLifecycle lifecycle = new HostLifecycle(scheduler, () => { });

        lifecycle.ResumeBackgroundWork();

        scheduler.IsRunning.Should().BeTrue();
        await scheduler.StopAsync();
        scheduler.IsRunning.Should().BeFalse("one StopAsync ends the one loop");
    }

    [Fact]
    public void StopBackgroundWork_AndStopBackupService_StopTheScheduler_RestartAfterWipe_ReturnsToFirstRun()
    {
        var scheduler = NewScheduler();
        var returned = 0;
        IBlindLifecycle lifecycle = new HostLifecycle(scheduler, () => returned++);
        scheduler.EnsureScheduled();

        lifecycle.StopBackupService();
        scheduler.IsRunning.Should().BeFalse();
        lifecycle.RestartAfterWipe();

        returned.Should().Be(1);
    }
}
