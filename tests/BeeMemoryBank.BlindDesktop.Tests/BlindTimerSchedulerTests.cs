using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>The scheduler loop with a fake app and clock (no timers or threads unless a test says so).</summary>
public sealed class BlindTimerSchedulerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private sealed class Rig
    {
        public FakeApp App { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public BlindTimerScheduler Scheduler { get; }
        public List<string> UserReports { get; } = [];

        /// <summary>What the scheduler wrote to the blind app's log about itself (and about the conditions it could not read).</summary>
        public List<string> Reports { get; } = [];

        public Rig(BlindSchedulerOptions? options = null)
        {
            Scheduler = new BlindTimerScheduler(App, Time, options ?? new BlindSchedulerOptions(),
                report: line => { lock (Reports) Reports.Add(line); });
            Scheduler.UserJobReported += UserReports.Add;
        }

        public async Task TickAfter(TimeSpan wait)
        {
            Time.Advance(wait);
            await Scheduler.TickAsync();
        }
    }

    [Fact]
    public async Task TheFirstPass_IsTheCheckAtStart_ItSyncsAndRunsTheLongJob()
    {
        var rig = new Rig();

        await rig.Scheduler.TickAsync();

        rig.App.SyncCalls.Should().Be(1);
        rig.App.HeavyForce.Should().Equal(false);
        rig.App.Calls.Should().Equal("sync", "heavy");
    }

    [Fact]
    public async Task ASyncRound_RunsEveryFifteenMinutes()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        rig.App.SyncCalls.Should().Be(1);

        await rig.TickAfter(TimeSpan.FromMinutes(14));
        rig.App.SyncCalls.Should().Be(1, "not due yet");

        await rig.TickAfter(TimeSpan.FromMinutes(1));
        rig.App.SyncCalls.Should().Be(2);

        await rig.TickAfter(TimeSpan.FromMinutes(15));
        rig.App.SyncCalls.Should().Be(3);
    }

    [Fact]
    public async Task TheLongJob_IsConsideredEveryHour()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        rig.App.HeavyCalls.Should().Be(1);

        await rig.TickAfter(TimeSpan.FromMinutes(59));
        rig.App.HeavyCalls.Should().Be(1);

        await rig.TickAfter(TimeSpan.FromMinutes(1));
        rig.App.HeavyCalls.Should().Be(2);
        rig.App.HeavyForce.Should().OnlyContain(force => !force, "the hourly run is not the Back-up-now button");
    }

    [Fact]
    public async Task NotPaired_NothingLongRuns_AndABackupRequestIsNotRemembered()
    {
        var rig = new Rig();
        rig.App.Status = FakeApp.StatusOf(paired: false, loaded: false);

        rig.Scheduler.RequestBackup();
        await rig.Scheduler.TickAsync();
        rig.App.HeavyCalls.Should().Be(0);

        // Paired later: the old request must not suddenly fire a forced backup.
        rig.App.Status = FakeApp.StatusOf(paired: true, loaded: false);
        await rig.TickAfter(Tick);
        rig.App.HeavyCalls.Should().Be(0, "nothing is due before the hour, and the earlier request was dropped");
    }

    [Fact]
    public async Task AfterPairing_RequestHeavy_RunsAtOnce_NotAtTheNextHour()
    {
        var rig = new Rig();
        rig.App.Status = FakeApp.StatusOf(paired: false, loaded: false);
        await rig.Scheduler.TickAsync();
        rig.App.HeavyCalls.Should().Be(0);

        rig.App.Status = FakeApp.StatusOf(paired: true, loaded: false);
        rig.Scheduler.RequestHeavy();
        await rig.TickAfter(TimeSpan.FromMinutes(2));

        rig.App.HeavyCalls.Should().Be(1, "the first load starts right after pairing");
    }

    [Fact]
    public async Task SyncNow_RunsAtOnce_AndTheUserHearsTheResult()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        rig.UserReports.Should().BeEmpty("the scheduled runs are quiet");

        rig.Scheduler.RequestSync();
        await rig.TickAfter(TimeSpan.FromSeconds(1));

        rig.App.SyncCalls.Should().Be(2);
        rig.UserReports.Should().Equal("Synced.");
    }

    [Fact]
    public async Task BackUpNow_ForcesABackup_AndReportsTheOutcome()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        rig.App.HeavyResult = "Backup made.";

        rig.Scheduler.RequestBackup();
        await rig.TickAfter(TimeSpan.FromSeconds(1));

        rig.App.HeavyForce.Should().Equal(false, true);
        rig.UserReports.Should().Equal("Backup made.");
    }

    [Fact]
    public async Task OneJobAtATime_AndRequestsMadeMeanwhileWaitForTheRunningJob()
    {
        var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.App.HeavyBody = async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return "Backup made.";
        };

        var running = rig.Scheduler.TickAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var syncsBefore = rig.App.SyncCalls;

        // A second pass, a sync request and a backup request while the long job runs: none starts anything.
        await rig.Scheduler.TickAsync();
        rig.Scheduler.RequestSync();
        rig.Scheduler.RequestBackup();
        await rig.Scheduler.TickAsync();
        rig.App.SyncCalls.Should().Be(syncsBefore);
        rig.App.HeavyCalls.Should().Be(1);

        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.TickAfter(TimeSpan.FromSeconds(1));

        rig.App.SyncCalls.Should().Be(syncsBefore + 1, "the request was kept and ran after the job");
        rig.App.HeavyForce.Should().Equal(false, true);
        rig.App.MaxConcurrentJobs.Should().Be(1);
    }

    [Fact]
    public async Task AFailedFirstLoad_IsRetriedSoon_WithAGrowingPause_NotOnlyAtTheNextHour()
    {
        // Right after pairing the listening node may not have heard of this device yet and answers 401: the first load fails with every
        // condition fine. The next try must not wait an hour.
        var rig = new Rig();
        rig.App.Status = FakeApp.StatusOf(loaded: false);
        rig.App.HeavyResult = "First load failed: Response status code does not indicate success: 401 (Unauthorized).";

        await rig.Scheduler.TickAsync();
        rig.App.HeavyCalls.Should().Be(1);

        var expectedPauses = new[] { 30, 60, 120, 240, 480, 900, 900 };
        var calls = 1;
        foreach (var pause in expectedPauses)
        {
            await rig.TickAfter(TimeSpan.FromSeconds(pause - 1));
            rig.App.HeavyCalls.Should().Be(calls, $"not yet: the pause is {pause} s");
            await rig.TickAfter(TimeSpan.FromSeconds(1));
            rig.App.HeavyCalls.Should().Be(++calls, $"after {pause} s");
        }
    }

    [Fact]
    public async Task OnceTheFirstLoadSucceeds_TheRetriesStop()
    {
        var rig = new Rig();
        rig.App.Status = FakeApp.StatusOf(loaded: false);
        var attempts = 0;
        rig.App.HeavyBody = _ =>
        {
            if (++attempts == 2) rig.App.Status = FakeApp.StatusOf(loaded: true);
            return Task.FromResult(attempts == 2 ? "First load finished." : "First load failed: 401");
        };

        await rig.Scheduler.TickAsync();
        await rig.TickAfter(TimeSpan.FromSeconds(30));
        attempts.Should().Be(2);

        await rig.TickAfter(TimeSpan.FromMinutes(10));
        attempts.Should().Be(2, "nothing is retried once the load is done; the hourly check is a long way off");
    }

    [Fact]
    public async Task AHandlerOfTheUserReportThatThrows_DoesNotBreakThePass_AndIsReportedOnceInTheQuietTime()
    {
        var rig = new Rig();
        rig.Scheduler.UserJobReported += _ => throw new ObjectDisposedException("the window");
        await rig.Scheduler.TickAsync();

        rig.Scheduler.RequestSync();
        var first = () => rig.TickAfter(TimeSpan.FromSeconds(1));
        await first.Should().NotThrowAsync();
        rig.Scheduler.RequestSync();
        await rig.TickAfter(TimeSpan.FromSeconds(1));

        rig.App.SyncCalls.Should().Be(3, "the job ran, and so did the next one");
        rig.Reports.Should().ContainSingle().Which.Should().Contain("Showing a job result failed").And.Contain("ObjectDisposedException");

        rig.Time.Advance(TimeSpan.FromMinutes(16));
        rig.Scheduler.RequestSync();
        await rig.Scheduler.TickAsync();
        rig.Reports.Should().HaveCount(2, "after the quiet time the same problem is said again");
    }

    [Fact]
    public async Task TheRetryOfAFailedFirstLoad_IsVisibleForTheScreen_WithItsPause_ItsAttempts_AndTheCeiling()
    {
        var rig = new Rig();
        rig.App.Status = FakeApp.StatusOf(loaded: false);
        rig.App.HeavyResult = "First load failed: The server answered 401.";
        rig.Scheduler.FirstLoadRetry.Should().BeNull("nothing failed yet");

        await rig.Scheduler.TickAsync();
        var info = rig.Scheduler.FirstLoadRetry!;
        info.Attempts.Should().Be(1);
        info.Every.Should().Be(TimeSpan.FromSeconds(30));
        info.NextAt.Should().Be(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(30));
        info.AtCeiling.Should().BeFalse();

        var expected = new (int Pause, int Next, bool Ceiling)[]
        {
            (30, 60, false), (60, 120, false), (120, 240, false), (240, 480, false), (480, 900, true), (900, 900, true), (900, 900, true),
        };
        var attempts = 1;
        foreach (var (pause, next, ceiling) in expected)
        {
            await rig.TickAfter(TimeSpan.FromSeconds(pause));
            attempts++;
            info = rig.Scheduler.FirstLoadRetry!;
            info.Attempts.Should().Be(attempts);
            info.Every.Should().Be(TimeSpan.FromSeconds(next));
            info.NextAt.Should().Be(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(next));
            info.AtCeiling.Should().Be(ceiling);
        }

        // It works at last.
        rig.App.HeavyBody = _ =>
        {
            rig.App.Status = FakeApp.StatusOf(loaded: true);
            return Task.FromResult("First load finished.");
        };
        await rig.TickAfter(TimeSpan.FromSeconds(900));
        rig.Scheduler.FirstLoadRetry.Should().BeNull("a done load plans no retry");
    }

    [Fact]
    public async Task AnUnexpectedErrorInAPass_DoesNotKillTheLoop()
    {
        var app = new FakeApp();
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions { Tick = TimeSpan.FromMilliseconds(20) });
        var calls = 0;
        app.SyncBody = _ => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Task.FromResult("Synced.");

        scheduler.EnsureScheduled();
        await WaitUntil(() => calls >= 1);
        scheduler.RequestSync();
        await WaitUntil(() => calls >= 2);

        scheduler.IsRunning.Should().BeTrue();
        scheduler.LastError.Should().Contain("InvalidOperationException").And.Contain("boom");
        await scheduler.StopAsync();
        scheduler.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task TheRealLoop_StartsWithACheck_WakesForARequest_AndStops()
    {
        var app = new FakeApp();
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions
        {
            Tick = TimeSpan.FromHours(1), // only a request can make the second sync happen within this test
            SyncEvery = TimeSpan.FromHours(1),
        });

        scheduler.EnsureScheduled();
        scheduler.EnsureScheduled(); // a second call does not start a second loop
        await WaitUntil(() => app.SyncCalls >= 1);
        app.Calls.Should().Contain("heavy", "the check at start also looks at the long job");

        scheduler.RequestSync();
        await WaitUntil(() => app.SyncCalls >= 2);

        await scheduler.StopAsync();
        scheduler.IsRunning.Should().BeFalse();
        app.MaxConcurrentJobs.Should().Be(1);
    }

    [Fact]
    public async Task Stop_PausesTheRunningJob_AndReturnsWhenItHasStopped()
    {
        var app = new FakeApp();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedByStop = false;
        app.HeavyBody = async ct =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { pausedByStop = true; throw; }
            return "unreachable";
        };
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions { Tick = TimeSpan.FromMilliseconds(20) });

        scheduler.EnsureScheduled();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        pausedByStop.Should().BeTrue("Stop sends the cancellation to the job, which pauses where it is");
        scheduler.IsRunning.Should().BeFalse();

        scheduler.EnsureScheduled(); // it can be started again
        scheduler.IsRunning.Should().BeTrue();
        app.HeavyBody = null;
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task AStopThatTimesOutOnASlowJob_DoesNotDisposeTheSourceTheLoopStillRunsOn()
    {
        // The job ignores the cancellation until it is released, so the stop must time out; the loop is then
        // left winding down on that very token, and the token has to keep working until the loop is done.
        var app = new FakeApp();
        var jobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenStillWorks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.HeavyBody = async ct =>
        {
            jobStarted.TrySetResult();
            await releaseJob.Task;
            // The stop has given up, but the source the loop still runs on must be alive: most uses of a
            // disposed source's token happen to work on the current runtime, the contract does not promise
            // it, and ct.WaitHandle is the member that demonstrably breaks (ObjectDisposedException).
            var sourceAlive = true;
            try { _ = ct.WaitHandle; }
            catch (ObjectDisposedException) { sourceAlive = false; }
            sourceAlive.Should().BeTrue("the loop and its job still run on this token after the stop timed out");
            using (ct.Register(() => tokenStillWorks.TrySetResult()))
                await Task.Delay(Timeout.Infinite, ct);
            return "unreachable";
        };
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions
        {
            Tick = TimeSpan.FromMilliseconds(20),
            StopTimeout = TimeSpan.FromMilliseconds(100),
        });

        scheduler.EnsureScheduled();
        await jobStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));   // gave up waiting after 100 ms

        releaseJob.SetResult();
        await tokenStillWorks.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => !scheduler.IsRunning);   // the loop left running finishes on the token the stop stopped waiting for

        scheduler.EnsureScheduled(); // a fresh loop can still be started afterwards
        scheduler.IsRunning.Should().BeTrue();
        app.HeavyBody = null;
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task ALongJob_RunsToItsEnd_NothingButTheLoopsOwnStopCancelsIt_WhateverTheTicksDo()
    {
        // The owner's rule: no charger, battery, Wi-Fi or network rule may interrupt a job that has started. The loop keeps ticking while
        // the job runs; the job's token must stay alive until the job itself ends.
        var app = new FakeApp();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        app.HeavyBody = async ct =>
        {
            started.TrySetResult();
            using var watch = ct.Register(() => cancelled = true);
            await release.Task;
            return "First load finished.";
        };
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions { Tick = TimeSpan.FromMilliseconds(10) });

        scheduler.EnsureScheduled();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300); // many ticks and many "condition" moments pass while the job runs

        cancelled.Should().BeFalse("nothing but the user's quit stops a started job");
        release.SetResult();
        await WaitUntil(() => scheduler.LastResult == "First load finished.");

        cancelled.Should().BeFalse();
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task ABlockingCancel_FromAnotherThread_ReturnsWhenTheLoopHasStopped()
    {
        var app = new FakeApp();
        var scheduler = new BlindTimerScheduler(app, TimeProvider.System, new BlindSchedulerOptions { Tick = TimeSpan.FromMilliseconds(20) });
        scheduler.EnsureScheduled();
        await WaitUntil(() => app.SyncCalls >= 1);

        await Task.Run(scheduler.Cancel).WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.IsRunning.Should().BeFalse();
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("the condition was not met in time");
            await Task.Delay(10);
        }
    }
}
