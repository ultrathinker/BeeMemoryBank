using BeeMemoryBank.Sync.DekRotation;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// <see cref="DeferredRotationRetry"/> keeps exactly one retry outstanding — from scheduling until
/// the retry has finished — so retries never overlap and the attempt budget counts real attempts.
/// </summary>
public class DeferredRotationRetryTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ScheduleWhileARetryIsRunning_StartsNoSecondRetry_AndSpendsNoAttempt()
    {
        var retry = new DeferredRotationRetry { BaseDelay = TimeSpan.FromMilliseconds(10) };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxConcurrent = 0;
        var calls = 0;

        async Task SlowRetry()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxConcurrent, now);
            Interlocked.Increment(ref calls);
            await gate.Task;
            Interlocked.Decrement(ref running);
        }

        retry.Schedule(SlowRetry, null).Should().BeTrue();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1, "the first retry should start");

        // A deferral elsewhere while the retry is still running (its delay long gone).
        retry.Schedule(SlowRetry, null).Should().BeFalse("a retry is still outstanding");
        retry.Schedule(SlowRetry, null).Should().BeFalse();
        await Task.Delay(200);
        Volatile.Read(ref calls).Should().Be(1, "no second retry may start while the first is running");
        retry.Attempts.Should().Be(1, "a Schedule call during an outstanding retry must not spend an attempt");

        // Once the running retry finishes, the remembered request becomes the next real attempt.
        gate.SetResult();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2, "the remembered request should run afterwards");
        retry.Attempts.Should().Be(2);
        maxConcurrent.Should().Be(1, "retries never overlap");
    }

    [Fact]
    public async Task ARetryThatDefersAgain_KeepsTheChainGoing_UntilTheBudgetIsSpent()
    {
        var retry = new DeferredRotationRetry { BaseDelay = TimeSpan.FromMilliseconds(5), MaxAttempts = 3 };
        var calls = 0;

        Task DeferringRetry()
        {
            Interlocked.Increment(ref calls);
            // What a deferred auto-accept does: schedule the next retry from inside the running one.
            retry.Schedule(DeferringRetry, null);
            return Task.CompletedTask;
        }

        retry.Schedule(DeferringRetry, null).Should().BeTrue();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 3, "each deferral should schedule the next attempt");
        await Task.Delay(300);
        Volatile.Read(ref calls).Should().Be(3, "the budget of three attempts is a hard stop");
        retry.Attempts.Should().Be(3);

        retry.Reset();
        retry.Attempts.Should().Be(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
