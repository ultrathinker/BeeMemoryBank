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

/// <summary>
/// A limiter that matches routes by string equality has to normalize exactly as ASP.NET Core
/// routing does — the difference between the two IS the bypass. "/api/init/reset/" reaches the
/// node-wiping endpoint just as "/api/init/reset" does, and used to skip the limiter entirely.
/// </summary>
public class RateLimitPathTests
{
    [Theory]
    [InlineData("/api/init/reset", "/api/init/reset")]
    [InlineData("/api/init/reset/", "/api/init/reset")]
    [InlineData("/API/Init/Reset", "/api/init/reset")]
    [InlineData("//api/init/reset", "/api/init/reset")]
    [InlineData("/api//init///reset//", "/api/init/reset")]
    [InlineData("/Login", "/login")]
    [InlineData("//Login/", "/login")]
    public void NormalizesToTheRoutedForm(string raw, string expected)
        => RateLimitPath.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    public void HandlesDegenerateInput(string? raw, string expected)
        => RateLimitPath.Normalize(raw).Should().Be(expected);

    [Fact]
    public void DoesNotMergeUnrelatedPaths()
    {
        RateLimitPath.Normalize("/api/init/resetx").Should().NotBe("/api/init/reset");
        RateLimitPath.Normalize("/loginx").Should().NotBe("/login");
    }
}

/// <summary>
/// Route classification decides which budget a request is charged to. Both possible mistakes are
/// silent: send the node wipe to the permissive sign-in budget, or give the two vectors onto that
/// same wipe a separate budget each.
/// </summary>
public class RateLimitRouteClassificationTests
{
    [Fact]
    public void PlainLoginIsTheLoginBudget()
        => RateLimitPath.Classify("/login", null).Should().Be(RateLimitedRoute.Login);

    [Theory]
    [InlineData("ResetNode")]
    [InlineData("resetnode")]
    [InlineData("RESETNODE")]
    public void TheAdminResetHandlerIsTheResetBudget(string handler)
        => RateLimitPath.Classify("/admin", [handler]).Should().Be(RateLimitedRoute.NodeReset);

    /// <summary>
    /// The bypass: Razor dispatches on the FIRST handler value, so a request carrying the reset
    /// handler among several really does run the node wipe. Reading the joined "ResetNode,x"
    /// instead of the individual values compares unequal and would drop the request out of the
    /// reset budget entirely.
    /// </summary>
    [Fact]
    public void ARepeatedHandlerParameterCannotEscapeTheResetBudget()
    {
        RateLimitPath.Classify("/admin", ["ResetNode", "anything"]).Should().Be(RateLimitedRoute.NodeReset);
        RateLimitPath.Classify("/admin", ["anything", "ResetNode"]).Should().Be(RateLimitedRoute.NodeReset);
    }

    [Fact]
    public void OtherLoginHandlersStayOnTheLoginBudget()
        => RateLimitPath.Classify("/login", ["ContinueWithoutBackup"]).Should().Be(RateLimitedRoute.Login);

    /// <summary>
    /// The Admin page carries many handlers and only one of them is destructive; throttling the
    /// rest would rate-limit ordinary administration.
    /// </summary>
    [Fact]
    public void OtherAdminHandlersAreNotThrottled()
    {
        RateLimitPath.Classify("/admin", ["CreateSnapshot"]).Should().Be(RateLimitedRoute.None);
        RateLimitPath.Classify("/admin", null).Should().Be(RateLimitedRoute.None);
    }

    /// <summary>
    /// The two anonymous doors onto the node wipe — a form on the locked Login page and an
    /// unauthenticated Web proxy route — are gone; the operation lives on the superadmin-only Admin
    /// page. Pinned here because "it is still reachable, just unclassified" is the silent failure
    /// this whole class exists to prevent.
    /// </summary>
    [Fact]
    public void TheFormerAnonymousResetVectors_AreNoLongerRoutes()
    {
        RateLimitPath.Classify("/login", ["Reset"]).Should().Be(RateLimitedRoute.Login);
        RateLimitPath.Classify("/api-proxy/init/reset", null).Should().Be(RateLimitedRoute.None);
    }

    [Fact]
    public void UnrelatedPathsAreNotThrottled()
    {
        RateLimitPath.Classify("/tree", null).Should().Be(RateLimitedRoute.None);
        RateLimitPath.Classify("/adminx", ["ResetNode"]).Should().Be(RateLimitedRoute.None);
    }
}
