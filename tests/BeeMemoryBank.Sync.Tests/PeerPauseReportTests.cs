using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.7: a blind copy that its owner's limits stop says so in its <c>report-position</c> (<c>paused</c>, <c>pausedUntil</c>), and the hub shows "Paused by
/// the device's owner" instead of "silent". Nothing a peer writes reaches a screen as it came: the reason is a closed token, the end a bounded instant, the
/// sentence the hub's own.
/// </summary>
public class PeerPauseReportTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("traffic", "traffic")]
    [InlineData(" requests ", "requests")]
    [InlineData("disk", "disk")]
    [InlineData("device-traffic", "device-traffic")]
    [InlineData("device-disk", "device-disk")]
    [InlineData("paused-by-you", "paused-by-you")]
    [InlineData("some new reason", "other")]
    [InlineData("<script>alert(1)</script>", "other")]
    [InlineData("TRAFFIC", "other")]
    [InlineData("metered-network", "other")]   // never sent: the person said not to use that network, which is no pause to report
    public void TheReasonIsAClosedSet(string declared, string expected) => PeerPauseReport.Reason(declared).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReasonIsNoPause(string? declared) => PeerPauseReport.Reason(declared).Should().BeNull();

    [Fact]
    public void AReasonLongerThanAnyWeKnowIsOther() => PeerPauseReport.Reason(new string('a', 5000)).Should().Be("other");

    [Fact]
    public void TheEndOfAPauseInUtc_IsAnInstant_WithNoOffset()
    {
        var (until, offset) = PeerPauseReport.Until("2026-10-22T00:00:00Z", Now);

        until.Should().Be(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc));
        offset.Should().BeNull("the hub does not know the device's zone");
    }

    [Fact]
    public void TheEndOfAPauseInTheDevicesOwnClock_KeepsItsOffset()
    {
        var (until, offset) = PeerPauseReport.Until("2026-10-22T00:00:00+03:00", Now);

        until.Should().Be(new DateTime(2026, 10, 21, 21, 0, 0, DateTimeKind.Utc));
        offset.Should().Be(180);
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2020-01-01T00:00:00Z")]             // long past
    [InlineData("2027-01-01T00:00:00Z")]             // more than a week ahead
    [InlineData("9999-12-31T23:59:59Z")]
    public void AnEndThatIsNotAnInstantOrNotCredible_IsLeftOut_AndThePauseStays(string? declared) =>
        PeerPauseReport.Until(declared, Now).UntilUtc.Should().BeNull();

    [Fact]
    public void AnEndWithoutAZone_IsTakenAsUtc() =>
        PeerPauseReport.Until("2026-10-22T00:00:00", Now).Should().Be((new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc), (int?)null));

    [Fact]
    public void AnEndOfFortyOneCharactersOrMore_IsLeftOut() =>
        PeerPauseReport.Until("2026-10-22T00:00:00+00:00" + new string(' ', 20) + "x", Now).UntilUtc.Should().BeNull();

    [Fact]
    public void TheSentence_IsTheOneTheOwnerReads()
    {
        var until = new DateTime(2026, 10, 21, 21, 0, 0, DateTimeKind.Utc);

        PeerPauseReport.Sentence("traffic", until, 180, Now).Should().Be("Paused by the device's owner: daily traffic limit (until 00:00 device time)");
        PeerPauseReport.Sentence("traffic", until, null, Now).Should().Be("Paused by the device's owner: daily traffic limit (until 21:00 UTC)");
        PeerPauseReport.Sentence("requests", null, null, Now).Should().Be("Paused by the device's owner: daily request limit");
        PeerPauseReport.Sentence("disk", until, null, Now).Should().Contain("storage limit");
        PeerPauseReport.Sentence("paused-by-you", null, null, Now).Should().Be("Paused by the device's owner: paused by hand");
        PeerPauseReport.Sentence("other", null, null, Now).Should().Be("Paused by the device's owner: a limit set on the device");
    }

    [Fact]
    public void AnEndMoreThanADayAway_ShowsTheDate_AndAnEndThatHasPassed_IsLeftOut()
    {
        PeerPauseReport.Sentence("device-traffic", Now.AddDays(3), null, Now).Should().Be("Paused by the device's owner: daily traffic limit of the whole device (until 2026-10-24 12:00 UTC)");
        PeerPauseReport.Sentence("traffic", Now.AddMinutes(-5), null, Now).Should().Be("Paused by the device's owner: daily traffic limit");
    }

    [Fact]
    public void AReasonThatIsNotInTheSet_NeverBecomesWords()
    {
        PeerPauseReport.Sentence("<b>x</b>", null, null, Now).Should().Be("Paused by the device's owner: a limit set on the device");
    }
}

public class PeerContactLogPauseTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AReportThatNamesAReason_RecordsThePause_AndAPlainOneEndsIt()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();

        log.NotePauseReport(copy, T0, "traffic", "2026-10-22T00:00:00+03:00");
        var paused = log.Get(copy);
        paused.PausedReason.Should().Be("traffic");
        paused.PausedUntil.Should().Be(new DateTime(2026, 10, 21, 21, 0, 0, DateTimeKind.Utc));
        paused.PausedUntilOffsetMinutes.Should().Be(180);
        paused.PausedReportedAt.Should().Be(T0);

        log.NotePauseReport(copy, T0.AddHours(1), null, null);
        log.Get(copy).Should().Match<PeerContactLog.Contact>(c => c.PausedReason == null && c.PausedUntil == null && c.PausedReportedAt == null,
            "a report without the fields is a copy that runs (or an older build that never pauses)");
    }

    [Fact]
    public void APlainReportFromAPeerNobodyKnows_CreatesNothing()
    {
        var log = new PeerContactLog();

        log.NotePauseReport(Guid.NewGuid(), T0, null, null);

        log.Count.Should().Be(0);
    }

    [Fact]
    public void ThePauseDoesNotTouchTheContactTheOtherFactsKeep()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();
        log.NoteInbound(copy, T0, "203.0.113.9");
        log.NotePlatform(copy, "android", false);

        log.NotePauseReport(copy, T0.AddMinutes(1), "disk", null);

        var contact = log.Get(copy);
        (contact.InboundAt, contact.InboundFrom, contact.Platform).Should().Be((T0, "203.0.113.9", "android"));
        contact.PausedReason.Should().Be("disk");
    }

    [Fact]
    public void AnUnknownReason_IsStoredAsOther()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();

        log.NotePauseReport(copy, T0, "'; DROP TABLE x; --", "not a time");

        log.Get(copy).PausedReason.Should().Be("other");
        log.Get(copy).PausedUntil.Should().BeNull("a time that is not an instant is left out; the pause stays");
    }

    [Fact]
    public void WhenTheDeviceLeaves_ItIsNoted_AndAnotherSignInClearsIt()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();

        log.NoteLeft(copy, T0);
        log.Get(copy).LeftAt.Should().Be(T0);

        log.ClearLeft(copy);
        log.Get(copy).LeftAt.Should().BeNull();
        log.ClearLeft(Guid.NewGuid());   // a peer nobody knows: nothing happens
    }

    [Fact]
    public void ARevokedPeer_ForgetsItsPauseAndItsLeave()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();
        log.NotePauseReport(copy, T0, "traffic", null);
        log.NoteLeft(copy, T0);

        log.Forget(copy);

        log.Get(copy).Should().Be(new PeerContactLog.Contact());
    }

    [Fact]
    public void ThePauseAndTheLeave_CountForTheRetention_SoAReportingCopyIsNotDroppedAsIdle()
    {
        var log = new PeerContactLog();
        var copy = Guid.NewGuid();
        log.NotePauseReport(copy, T0, "traffic", null);

        log.Reconcile(new HashSet<Guid> { copy }, T0.AddDays(10));
        log.Get(copy).PausedReason.Should().Be("traffic");

        log.Reconcile(new HashSet<Guid> { copy }, T0 + PeerContactLog.Retention + TimeSpan.FromDays(1));
        log.Get(copy).Should().Be(new PeerContactLog.Contact(), "nothing was heard for the retention time, a pause report included");
    }
}
