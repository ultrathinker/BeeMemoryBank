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

    // ── a bound on the keys a stranger can create (the public invitation routes: one key per address) ──

    [Fact]
    public void ManyMoreDistinctKeysThanTheBound_NeverGrowTheTableBeyondIt()
    {
        var limiter = new SlidingWindowRateLimiter(10, TimeSpan.FromMinutes(10), maxKeys: 100);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5_000; i++) limiter.TryAcquire($"203.0.113.{i}", t0.AddMilliseconds(i));

        limiter.KeyCount.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void AtTheBound_AKeyThatIsStillCounting_IsNeverForgotten_AndTheNewcomerIsRefused()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(10), maxKeys: 10);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++) limiter.TryAcquire($"k{i}", t0.AddSeconds(i));   // every key has used one of its three attempts: partial, not banned

        limiter.TryAcquire("newcomer", t0.AddSeconds(20)).Should().BeFalse("no key can be dropped without resetting somebody's budget, so the newcomer waits");

        for (var i = 0; i < 10; i++) limiter.CountFor($"k{i}", t0.AddSeconds(20)).Should().Be(1, "nobody's partial budget was reset");
        limiter.KeyCount.Should().Be(10);
    }

    [Fact]
    public void AKeyWhoseWindowRanOut_IsTheOnlyKindThatMakesRoom()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(10), maxKeys: 4);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        limiter.TryAcquire("old1", t0);
        limiter.TryAcquire("old2", t0);
        limiter.TryAcquire("fresh1", t0.AddMinutes(9));
        limiter.TryAcquire("fresh2", t0.AddMinutes(9));

        limiter.TryAcquire("newcomer", t0.AddMinutes(11)).Should().BeTrue("old1 and old2 ran out their window, so they hold no room any more");

        limiter.CountFor("fresh1", t0.AddMinutes(11)).Should().Be(1, "the keys that were still counting are untouched");
        limiter.CountFor("fresh2", t0.AddMinutes(11)).Should().Be(1);
        limiter.KeyCount.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void ALegitimateCallersPartialBudget_SurvivesAFloodOfNewKeys()
    {
        var limiter = new SlidingWindowRateLimiter(10, TimeSpan.FromMinutes(10), maxKeys: 50);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++) limiter.TryAcquire("friend", t0.AddSeconds(i));

        for (var i = 0; i < 5_000; i++) limiter.TryAcquire($"flood{i}", t0.AddSeconds(10 + i % 100));

        limiter.CountFor("friend", t0.AddSeconds(200)).Should().Be(6, "the flood did not reset the friend's six attempts");
        for (var i = 0; i < 4; i++) limiter.TryAcquire("friend", t0.AddSeconds(200)).Should().BeTrue("and the friend still has four left");
        limiter.TryAcquire("friend", t0.AddSeconds(200)).Should().BeFalse("and no more than the budget");
        limiter.KeyCount.Should().BeLessThanOrEqualTo(50);
    }

    /// <summary>
    /// The table is exactly bounded under concurrency: the capacity decision and the insertion are one step. A table one short of its bound, and sixteen newcomers released
    /// at the same instant, many times over: if each could see the room and then insert, some round would end above the bound.
    /// </summary>
    [Fact]
    public void NewcomersReleasedTogether_NeverMakeTheTableLargerThanItsBound()
    {
        const int bound = 50;
        const int threads = 16;
        var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var largest = 0;

        for (var round = 0; round < 300; round++)
        {
            var limiter = new SlidingWindowRateLimiter(10, TimeSpan.FromMinutes(10), maxKeys: bound);
            for (var i = 0; i < bound - 1; i++) limiter.TryAcquire($"known-{i}", started);
            using var barrier = new Barrier(threads);

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                barrier.SignalAndWait();
                limiter.TryAcquire($"new-{round}-{i}", started);
            });

            largest = Math.Max(largest, limiter.KeyCount);
        }

        largest.Should().BeLessThanOrEqualTo(bound, "at no round did the burst of newcomers get past the bound");
    }

    [Fact]
    public void ManyThreadsAddingDistinctKeys_NeverMakeTheTableLargerThanItsBound()
    {
        const int bound = 200;
        var limiter = new SlidingWindowRateLimiter(10, TimeSpan.FromMinutes(10), maxKeys: bound);
        var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Parallel.For(0, 200_000, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => limiter.TryAcquire($"caller-{i}", started));

        limiter.KeyCount.Should().BeLessThanOrEqualTo(bound);
    }

    [Fact]
    public void Refund_TakesBackTheNewestAttempt_AndDropsAKeyThatHasNone()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(10), maxKeys: 10);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        limiter.TryAcquire("a", t0);
        limiter.TryAcquire("a", t0.AddSeconds(1));
        limiter.TryAcquire("b", t0);

        limiter.Refund("a");
        limiter.Refund("b");

        limiter.CountFor("a", t0.AddSeconds(2)).Should().Be(1);
        limiter.KeyCount.Should().Be(1, "b has no attempt left, so it holds no room");
        limiter.TryAcquire("a", t0.AddSeconds(3)).Should().BeTrue("the refunded attempt is available again");
        limiter.Refund("nobody");   // nothing to take back, nothing to fail
    }

    [Fact]
    public void ABannedKey_IsNeverForgottenToMakeRoom_AndANewKeyIsRefusedInstead()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(10), maxKeys: 5);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++) limiter.TryAcquire($"banned{i}", t0).Should().BeTrue();   // each used its one attempt: all five are at their limit

        limiter.TryAcquire("stranger", t0.AddSeconds(1)).Should().BeFalse("the table is full of banned keys: fail closed, do not free one of them");

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire($"banned{i}", t0.AddSeconds(2)).Should().BeFalse("an eviction must not reset a ban");
        limiter.KeyCount.Should().Be(5);
    }

    [Fact]
    public void AfterTheWindow_TheBannedKeysFreeTheirRoom()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(10), maxKeys: 3);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++) limiter.TryAcquire($"b{i}", t0);
        limiter.TryAcquire("stranger", t0.AddSeconds(1)).Should().BeFalse();

        limiter.TryAcquire("stranger", t0.AddMinutes(11)).Should().BeTrue("their window ran out, so they hold no room any more");
    }

    [Fact]
    public void AKeyThatIsAlreadyKnown_IsNotRefusedBecauseTheTableIsFull()
    {
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(10), maxKeys: 2);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        limiter.TryAcquire("a", t0);
        limiter.TryAcquire("b", t0);

        limiter.TryAcquire("a", t0.AddSeconds(1)).Should().BeTrue("a known caller keeps its own budget");
    }

    [Fact]
    public void WithoutABound_NothingChangesForTheOlderUsers()
    {
        var limiter = new SlidingWindowRateLimiter(1, TimeSpan.FromMinutes(10));

        for (var i = 0; i < 2_000; i++) limiter.TryAcquire($"k{i}").Should().BeTrue();

        limiter.KeyCount.Should().Be(2_000);
        limiter.MaxKeys.Should().Be(0);
    }

    [Fact]
    public void RetryAfter_SaysWhenTheOldestAttemptLeavesTheWindow()
    {
        var limiter = new SlidingWindowRateLimiter(2, TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        limiter.TryAcquire("ip", t0);
        limiter.TryAcquire("ip", t0.AddMinutes(1));
        limiter.TryAcquire("ip", t0.AddMinutes(2)).Should().BeFalse();

        limiter.RetryAfter("ip", t0.AddMinutes(2)).Should().Be(TimeSpan.FromMinutes(8));
        limiter.RetryAfter("nobody", t0).Should().BeNull();
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
    /// The Admin area has a page per block, and the wipe moved with its block to /Admin/ResetNode. Its budget moved with it: every POST to the page is the wipe,
    /// whatever handler it names, in any spelling of the path the router resolves to the page.
    /// </summary>
    [Theory]
    [InlineData("/Admin/ResetNode", "ResetNode")]
    [InlineData("/admin/resetnode/", "ResetNode")]
    [InlineData("//ADMIN//RESETNODE", "resetnode")]
    [InlineData("/Admin/ResetNode", "SomethingElse")]
    [InlineData("/Admin/ResetNode", null)]
    public void ThePageOfTheNodeWipe_IsTheResetBudget_WhateverTheHandler(string rawPath, string? handler)
        => RateLimitPath.Classify(RateLimitPath.Normalize(rawPath), handler is null ? null : [handler]).Should().Be(RateLimitedRoute.NodeReset);

    [Fact]
    public void ARepeatedHandlerParameterCannotEscapeTheResetBudgetOnItsOwnPage()
    {
        RateLimitPath.Classify("/admin/resetnode", ["anything", "ResetNode"]).Should().Be(RateLimitedRoute.NodeReset);
        RateLimitPath.Classify("/admin/resetnodex", ["ResetNode"]).Should().Be(RateLimitedRoute.None);
        RateLimitPath.Classify("/admin/resetnode/x", ["ResetNode"]).Should().Be(RateLimitedRoute.None);
    }

    /// <summary>The other pages of the Admin area post routine configuration and are not throttled.</summary>
    [Theory]
    [InlineData("/admin/snapshots", "DeleteSnapshot")]
    [InlineData("/admin/masterpassword", "ChangeMasterPassword")]
    [InlineData("/admin/dekrotation", "ProposeDekRotation")]
    [InlineData("/admin/trustednodes", "RevokeNode")]
    public void OtherPagesOfTheAdminAreaAreNotThrottled(string path, string handler)
        => RateLimitPath.Classify(path, [handler]).Should().Be(RateLimitedRoute.None);

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

    /// <summary>
    /// "Forgot your password?" checks a recovery key (an Argon2id derivation per recovery slot) for a
    /// caller nobody has signed in, so it has a budget of its own: not the sign-in one (20 tries would be
    /// far too many guesses at a key), and not none. Every spelling the router resolves to the page must land
    /// in it — the path is normalized first, exactly as for the other classes.
    /// </summary>
    [Theory]
    [InlineData("/RecoverAccess")]
    [InlineData("/recoveraccess/")]
    [InlineData("//RECOVERACCESS")]
    public void TheRecoveryPageHasABudgetOfItsOwn(string rawPath)
    {
        var route = RateLimitPath.Classify(RateLimitPath.Normalize(rawPath), null);

        route.Should().Be(RateLimitedRoute.RecoverAccess);
        route.Should().NotBe(RateLimitedRoute.Login);
    }

    [Fact]
    public void ARecoveryHandlerNameCannotMoveItToAnotherBudget()
    {
        RateLimitPath.Classify("/recoveraccess", ["ResetNode"]).Should().Be(RateLimitedRoute.RecoverAccess);
        RateLimitPath.Classify("/recoveraccessx", null).Should().Be(RateLimitedRoute.None);
        RateLimitPath.Classify("/recoveraccess/done", null).Should().Be(RateLimitedRoute.None);
    }

    [Fact]
    public void UnrelatedPathsAreNotThrottled()
    {
        RateLimitPath.Classify("/tree", null).Should().Be(RateLimitedRoute.None);
        RateLimitPath.Classify("/adminx", ["ResetNode"]).Should().Be(RateLimitedRoute.None);
    }
}
