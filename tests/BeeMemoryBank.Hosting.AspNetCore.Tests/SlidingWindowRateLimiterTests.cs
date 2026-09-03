using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Hosting.AspNetCore.Tests;

public class SlidingWindowRateLimiterTests
{
    [Fact]
    public void AllowsUpToTheBudgetThenBlocks()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(5));

        limiter.TryAcquire("ip").Should().BeTrue();
        limiter.TryAcquire("ip").Should().BeTrue();
        limiter.TryAcquire("ip").Should().BeTrue();
        limiter.TryAcquire("ip").Should().BeFalse("the budget is 3");
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(5));

        limiter.TryAcquire("a").Should().BeTrue();
        limiter.TryAcquire("a").Should().BeFalse();
        limiter.TryAcquire("b").Should().BeTrue("one caller's budget must not consume another's");
    }

    [Fact]
    public void AttemptsFallOutOfTheWindow()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        limiter.TryAcquire("ip", t0).Should().BeTrue();
        limiter.TryAcquire("ip", t0).Should().BeTrue();
        limiter.TryAcquire("ip", t0).Should().BeFalse();

        limiter.TryAcquire("ip", t0.AddMinutes(6)).Should().BeTrue("the window has passed");
    }

    /// <summary>
    /// A blocked attempt must not extend the block. Recording rejected attempts would let anyone
    /// hammering a shared egress IP hold it in permanent lockout — the office behind one NAT
    /// address could never get back in while an attacker kept firing.
    /// </summary>
    [Fact]
    public void RejectedAttemptsDoNotPushTheWindowForward()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        limiter.TryAcquire("ip", t0).Should().BeTrue();

        // Hammer it for four of the five minutes.
        for (int i = 1; i <= 4; i++)
            limiter.TryAcquire("ip", t0.AddMinutes(i)).Should().BeFalse();

        // The single recorded attempt is now older than the window, so the key is free again —
        // it would not be if the rejections had been recorded too.
        limiter.TryAcquire("ip", t0.AddMinutes(6)).Should().BeTrue();
    }

    [Fact]
    public void ResetClearsTheKey()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(5));

        limiter.TryAcquire("ip").Should().BeTrue();
        limiter.TryAcquire("ip").Should().BeTrue();
        limiter.TryAcquire("ip").Should().BeFalse();

        limiter.Reset("ip");

        limiter.CountFor("ip").Should().Be(0);
        limiter.TryAcquire("ip").Should().BeTrue("a successful attempt forgives the window");
    }

    [Fact]
    public void ConcurrentAcquiresNeverExceedTheBudget()
    {
        var limiter = new SlidingWindowRateLimiter(50, TimeSpan.FromMinutes(5));
        int granted = 0;

        Parallel.For(0, 500, _ =>
        {
            if (limiter.TryAcquire("ip")) Interlocked.Increment(ref granted);
        });

        granted.Should().Be(50, "the window must hold under concurrent callers, not merely on average");
    }
}
