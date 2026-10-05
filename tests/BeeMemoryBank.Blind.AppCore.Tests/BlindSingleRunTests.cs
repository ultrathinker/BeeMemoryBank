using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// F-10: repeated "Back up now" taps. The service used to replace its cancellation source and launch a new untracked task on every start; the
/// second task's end stopped the service (and removed the notification) under the first, still running, backup.
/// </summary>
public sealed class BlindSingleRunTests
{
    [Fact]
    public async Task ASecondStartWhileTheTaskRuns_IsCoalesced_NothingIsReplaced_AndTheServiceDoesNotEnd()
    {
        var run = new BlindSingleRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new List<int>();
        var tokens = new List<CancellationToken>();

        Task Work(CancellationToken token)
        {
            lock (tokens) tokens.Add(token);
            started.TrySetResult();
            return release.Task;
        }

        run.TryStart(1, Work, ended.Add).Should().BeTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        run.TryStart(2, Work, ended.Add).Should().BeFalse("a backup is running: the tap is the same request");
        run.TryStart(3, Work, ended.Add).Should().BeFalse();

        lock (tokens) tokens.Should().HaveCount(1, "no second task was launched");
        lock (ended) ended.Should().BeEmpty("the service stops only when its own task ends, not when a later start is ignored");
        run.IsRunning.Should().BeTrue();

        // The one cancellation handle reaches the one task.
        run.Cancel();
        tokens[0].IsCancellationRequested.Should().BeTrue();

        release.SetResult();
        await WaitUntilAsync(() => !run.IsRunning);
        ended.Should().Equal(3);
    }

    [Fact]
    public async Task TheEndStep_RunsAfterTheWorkHasEnded_NotBefore()
    {
        var run = new BlindSingleRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        run.TryStart(7, _ => { started.SetResult(); return release.Task; }, ended.SetResult);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        ended.Task.IsCompleted.Should().BeFalse();

        release.SetResult();
        (await ended.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(7);
    }

    [Fact]
    public async Task AStartAfterTheTaskEnded_StartsANewOne_AndAWorkThatThrowsStillEndsTheService()
    {
        var run = new BlindSingleRun();
        var ended = new List<int>();

        run.TryStart(1, _ => throw new InvalidOperationException("boom"), ended.Add).Should().BeTrue();
        await WaitUntilAsync(() => !run.IsRunning);
        ended.Should().Equal(1);

        var again = new TaskCompletionSource();
        run.TryStart(2, _ => { again.SetResult(); return Task.CompletedTask; }, ended.Add).Should().BeTrue();
        await again.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !run.IsRunning);
        ended.Should().Equal(1, 2);
    }

    [Fact]
    public async Task TheInLockStep_RunsForEveryStart_EvenACoalescedOne()
    {
        var run = new BlindSingleRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = 0;

        run.TryStart(1, _ => release.Task, _ => { }, () => foreground++);
        run.TryStart(2, _ => release.Task, _ => { }, () => foreground++);

        foreground.Should().Be(2, "every startForegroundService must be answered by StartForeground");
        release.SetResult();
        await WaitUntilAsync(() => !run.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
