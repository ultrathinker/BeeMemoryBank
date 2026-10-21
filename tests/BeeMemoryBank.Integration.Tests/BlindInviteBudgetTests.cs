using System.Net;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The process-wide ceiling of the public invitation routes and the key a caller is counted under (release 2.7, fix round 1): many callers, each of them within
/// their own budget, still add up to a bounded amount of work, and the table of callers stays bounded.
/// </summary>
public sealed class BlindInviteBudgetTests
{
    /// <summary>A clock a test moves by hand: the token bucket measures elapsed time with <see cref="TimeProvider.GetTimestamp"/>.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp() => _now;

        public void Advance(TimeSpan by) => _now += (long)(by.TotalSeconds * 1_000_000_000);
    }

    [Fact]
    public void ABurstIsLetIn_ThenTheRateDecides_AndARefusalSaysWhenToComeBack()
    {
        var clock = new ManualClock();
        var budget = new PublicRouteBudget(burst: 5, refillPerSecond: 2, maxConcurrent: 100, clock);

        for (var i = 0; i < 5; i++) { using var entry = budget.TryEnter(); entry.Allowed.Should().BeTrue(); }
        using var refused = budget.TryEnter();

        refused.Allowed.Should().BeFalse();
        refused.Status.Should().Be(429);
        refused.RetryAfterSeconds.Should().Be(1, "one token takes half a second to come back, and the answer is whole seconds");

        clock.Advance(TimeSpan.FromSeconds(1));   // two tokens
        using (var a = budget.TryEnter()) a.Allowed.Should().BeTrue();
        using (var b = budget.TryEnter()) b.Allowed.Should().BeTrue();
        using var c = budget.TryEnter();
        c.Allowed.Should().BeFalse("only two tokens came back");
    }

    [Fact]
    public void TheBucketNeverHoldsMoreThanTheBurst_HoweverLongItRests()
    {
        var clock = new ManualClock();
        var budget = new PublicRouteBudget(burst: 3, refillPerSecond: 10, maxConcurrent: 100, clock);
        clock.Advance(TimeSpan.FromHours(5));

        var allowed = 0;
        for (var i = 0; i < 20; i++) { using var entry = budget.TryEnter(); if (entry.Allowed) allowed++; }

        allowed.Should().Be(3);
    }

    [Fact]
    public void RequestsBeingServed_AreCapped_With503_AndASlotIsFreedWhenTheRequestEnds()
    {
        var budget = new PublicRouteBudget(burst: 100, refillPerSecond: 100, maxConcurrent: 2, new ManualClock());
        var first = budget.TryEnter();
        var second = budget.TryEnter();

        using (var third = budget.TryEnter())
        {
            third.Allowed.Should().BeFalse();
            third.Status.Should().Be(503, "the node is busy, which is not the same as the caller being too fast");
            third.RetryAfterSeconds.Should().BeGreaterThan(0);
        }
        budget.InFlight.Should().Be(2, "a refused request holds no slot");

        first.Dispose();
        first.Dispose();   // a second Dispose must not free a slot that is not its own
        budget.InFlight.Should().Be(1);
        using (var again = budget.TryEnter()) again.Allowed.Should().BeTrue();
        second.Dispose();
        budget.InFlight.Should().Be(0);
    }

    [Fact]
    public void ARefusalForTheRate_DoesNotKeepItsSlot()
    {
        var budget = new PublicRouteBudget(burst: 1, refillPerSecond: 0.001, maxConcurrent: 1, new ManualClock());
        using (budget.TryEnter()) { }

        using var refused = budget.TryEnter();

        refused.Status.Should().Be(429);
        budget.InFlight.Should().Be(0);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]                   // a dual-stack listener reports an IPv4 caller this way
    [InlineData("2001:db8:1234:5678:aaaa:bbbb:cccc:dddd", "2001:db8:1234:5678::/64")]
    [InlineData("2001:db8:1234:5678:1::1", "2001:db8:1234:5678::/64")]  // the same subscriber, another address of its /64
    public void TheCallersKey_IsTheIPv4Address_OrTheIPv6Prefix(string address, string key) =>
        BlindInviteCaller.KeyOf(IPAddress.Parse(address)).Should().Be(key);

    [Fact]
    public void TwoPrefixes_AreTwoCallers_AndNoAddressIsOne()
    {
        BlindInviteCaller.KeyOf(IPAddress.Parse("2001:db8:1234:5678::1")).Should().NotBe(BlindInviteCaller.KeyOf(IPAddress.Parse("2001:db8:1234:5679::1")));
        BlindInviteCaller.KeyOf(null).Should().Be("unknown");
    }

    [Fact]
    public void ManyAddressesOfOneIPv6Prefix_ShareOnePerCallerBudget()
    {
        var limits = new BlindInviteRateLimits();
        limits.RedeemBudget = new PublicRouteBudget(1_000_000, 1_000_000, 1_000_000);
        var prefix = IPAddress.Parse("2001:db8:aaaa:bbbb::").GetAddressBytes();

        var admitted = 0;
        for (var i = 0; i < 1_000; i++)
        {
            var bytes = (byte[])prefix.Clone();
            bytes[15] = (byte)i;
            bytes[14] = (byte)(i >> 8);
            using var admission = limits.Admit(BlindInviteRoute.Redeem, new IPAddress(bytes));
            if (admission.Allowed) admitted++;
        }

        admitted.Should().Be(10, "a thousand addresses of one /64 are one caller, with the one budget of ten redemptions");
    }

    /// <summary>
    /// The attack of the review: a bot with very many source addresses, each of them within its own budget. Whatever it does, the work let through is bounded by the
    /// process-wide budget, and the table of callers by its cap.
    /// </summary>
    [Fact]
    public void ManyDistinctIPv4AndIPv6Callers_StayWithinTheProcessWideCeiling_AndTheTableStaysBounded()
    {
        var limits = new BlindInviteRateLimits();
        var admittedRedeem = 0;
        var admittedStatus = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        for (var i = 0; i < 60_000; i++)
        {
            var caller = i % 2 == 0
                ? new IPAddress(new byte[] { (byte)(i >> 16), (byte)(i >> 8), (byte)i, (byte)(i >> 24 | 1) })
                : new IPAddress(new byte[] { 0x20, 0x01, 0x0d, 0xb8, (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i, 0, 0, 0, 0, 0, 0, 0, 1 });
            using (var redeem = limits.Admit(BlindInviteRoute.Redeem, caller)) if (redeem.Allowed) admittedRedeem++;
            using (var status = limits.Admit(BlindInviteRoute.Status, caller)) if (status.Allowed) admittedStatus++;
        }
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;

        // The budgets: a burst of 30 (redeem) and 100 (status), plus what the steady rate adds in the seconds this loop took.
        admittedRedeem.Should().BeLessThanOrEqualTo(30 + (int)Math.Ceiling(3 * seconds) + 1);
        admittedStatus.Should().BeLessThanOrEqualTo(100 + (int)Math.Ceiling(20 * seconds) + 1);
        limits.Redeem.KeyCount.Should().BeLessThanOrEqualTo(BlindInviteRateLimits.MaxCallers);
        limits.Status.KeyCount.Should().BeLessThanOrEqualTo(BlindInviteRateLimits.MaxCallers);
    }

    [Fact]
    public void ABannedCaller_CostsTheSharedBudgetNothing()
    {
        var limits = new BlindInviteRateLimits();
        var banned = IPAddress.Parse("198.51.100.9");
        for (var i = 0; i < 10; i++) limits.Admit(BlindInviteRoute.Redeem, banned).Dispose();

        using var refused = limits.Admit(BlindInviteRoute.Redeem, banned);
        using var other = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("198.51.100.10"));

        refused.Allowed.Should().BeFalse();
        refused.Status.Should().Be(429);
        refused.RetryAfterSeconds.Should().BeGreaterThan(0);
        other.Allowed.Should().BeTrue("one noisy address does not use up what everybody else may do");
    }

    // ── fix round 2: refused traffic does not fill the table; the reserved lane ──

    [Fact]
    public void ARequestTheSharedBudgetTurnsAway_DoesNotUseUpTheCallersBudget_NorFillTheTableOfCallers()
    {
        var limits = new BlindInviteRateLimits();
        limits.RedeemBudget = new PublicRouteBudget(burst: 2, refillPerSecond: 0.0001, maxConcurrent: 100);
        var caller = IPAddress.Parse("198.51.100.20");
        limits.Admit(BlindInviteRoute.Redeem, caller).Dispose();
        limits.Admit(BlindInviteRoute.Redeem, caller).Dispose();

        for (var i = 0; i < 50; i++)
        {
            using var refused = limits.Admit(BlindInviteRoute.Redeem, caller);
            refused.Allowed.Should().BeFalse();
            refused.Status.Should().Be(429);
        }
        limits.Redeem.CountFor("198.51.100.20").Should().Be(2, "only the two requests that were let in are counted against the caller");

        for (var i = 0; i < 5_000; i++) limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse($"203.0.{i / 250}.{i % 250 + 1}")).Dispose();
        limits.Redeem.KeyCount.Should().Be(1, "rotating callers that the shared budget turned away left nothing in the table, so they cannot fill it with bans");
    }

    private const string FriendsInvitation = "AAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void AnOpenInvitation_IsServedFromItsOwnLane_WhileTheGeneralBudgetIsEmpty_AndAnUnknownIdIsNot()
    {
        var limits = new BlindInviteRateLimits();
        limits.RedeemBudget = new PublicRouteBudget(burst: 1, refillPerSecond: 0.0001, maxConcurrent: 100);
        limits.OpenInvites.Replace([(FriendsInvitation, DateTime.UtcNow.AddMinutes(30))]);
        limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.1")).Dispose();   // the general budget is spent

        using var stranger = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.2"), "BBBBBBBBBBBBBBBBBBBBBB");
        using var friend = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.3"), FriendsInvitation);

        stranger.Allowed.Should().BeFalse("an id the node does not know uses the general budget, which is empty");
        friend.Allowed.Should().BeTrue("an open invitation draws from its own lane");
    }

    [Fact]
    public void TheReservedLane_DoesNotUseThePerCallerTable_SoAFloodOfCallersCannotRefuseTheFriend()
    {
        var limits = new BlindInviteRateLimits();
        limits.OpenInvites.Replace([(FriendsInvitation, DateTime.UtcNow.AddMinutes(30))]);
        limits.Redeem = new SlidingWindowRateLimiter(10, TimeSpan.FromMinutes(10), maxKeys: 3);
        for (var i = 0; i < 3; i++) limits.Redeem.TryAcquire($"203.0.113.{i + 10}");   // the table is full of other callers still counting

        using var friend = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("198.51.100.77"), FriendsInvitation);
        using var stranger = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("198.51.100.78"));

        friend.Allowed.Should().BeTrue();
        stranger.Allowed.Should().BeFalse("a newcomer without an invitation waits for room");
    }

    [Fact]
    public void AnInvitationThatRanOut_OrWasDecided_HasNoReservedLane()
    {
        var limits = new BlindInviteRateLimits();
        limits.RedeemBudget = new PublicRouteBudget(burst: 1, refillPerSecond: 0.0001, maxConcurrent: 100);
        limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.1")).Dispose();
        limits.OpenInvites.Replace([(FriendsInvitation, DateTime.UtcNow.AddMinutes(-1))]);

        using (var ranOut = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.5"), FriendsInvitation))
            ranOut.Allowed.Should().BeFalse("the open set remembers when each invitation runs out");

        limits.OpenInvites.Replace([]);
        using var decided = limits.Admit(BlindInviteRoute.Redeem, IPAddress.Parse("203.0.113.6"), FriendsInvitation);
        decided.Allowed.Should().BeFalse();
    }

    [Fact]
    public void TheReservedLane_IsASmallBucketPerInvitation_AndPerRoute()
    {
        var limits = new BlindInviteRateLimits();
        limits.OpenInvites.Replace([(FriendsInvitation, DateTime.UtcNow.AddMinutes(30)), ("CCCCCCCCCCCCCCCCCCCCCC", DateTime.UtcNow.AddMinutes(30))]);
        var caller = IPAddress.Parse("203.0.113.9");

        var redeems = 0;
        for (var i = 0; i < 100; i++) { using var a = limits.Admit(BlindInviteRoute.Redeem, caller, FriendsInvitation); if (a.Allowed) redeems++; }

        redeems.Should().BeInRange(10, 12, "a burst of ten and a slow refill: the lane of one invitation is not a way around every limit");
        using var other = limits.Admit(BlindInviteRoute.Redeem, caller, "CCCCCCCCCCCCCCCCCCCCCC");
        using var status = limits.Admit(BlindInviteRoute.Status, caller, FriendsInvitation);
        other.Allowed.Should().BeTrue("another invitation has its own lane");
        status.Allowed.Should().BeTrue("and the status route has its own");
    }

    [Theory]
    [InlineData("{\"inviteId\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"secret\":\"x\"}", "AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("{ \"secret\": \"x\", \"inviteId\" :	\"a-_B0123456789abcdefgh\" }", "a-_B0123456789abcdefgh")]
    [InlineData("{\"inviteId\":\"short\"}", null)]
    [InlineData("{\"inviteId\":\"AAAAAAAAAAAAAAAAAAAAAAA\"}", null)]          // 23 characters
    [InlineData("{\"inviteId\":\"AAAAAAAAAAAAAAAAAAAA!A\"}", null)]           // not base64url
    [InlineData("{\"inviteId\":12345678901234567890123}", null)]
    [InlineData("{\"inviteid\":\"AAAAAAAAAAAAAAAAAAAAAA\"}", null)]           // the member is spelled exactly
    [InlineData("", null)]
    [InlineData("not json at all", null)]
    [InlineData("{\"inviteId\":\"AAAAAAAAAAAAAAAAAAAAAA", null)]               // cut off inside the id
    public void TheInviteIdIsFoundInTheFirstBytesOfTheBody_WithoutParsingJson(string body, string? expected) =>
        BlindInviteBodyPeek.InviteId(System.Text.Encoding.UTF8.GetBytes(body)).Should().Be(expected);
}
