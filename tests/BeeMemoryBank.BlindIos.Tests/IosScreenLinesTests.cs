using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// What the iPhone screen adds to the shared view (last contact, the newest problem, the last background round) and the "silent node"
/// notification's plan: worked out from the controller's status alone.
/// </summary>
public class IosScreenLinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LastContact_SaysWhenAndHowLongAgo_OrThatThereWasNone()
    {
        IosHomeLines.LastContact(Status(paired: false), Now).Should().Contain("never").And.Contain("not paired");
        IosHomeLines.LastContact(Status(), Now).Should().Contain("not yet");
        IosHomeLines.LastContact(Status(lastSync: Now.AddMinutes(-5)), Now).Should().Contain("5 minutes ago");
        IosHomeLines.LastContact(Status(lastSync: Now.AddHours(-30)), Now).Should().Contain("1 day ago");
        IosHomeLines.LastContact(Status(lastSync: Now.AddSeconds(5)), Now).Should().Contain("just now", "a clock a little ahead is not a negative time");
    }

    [Fact]
    public void TheNewestProblem_IsAContactFailureAfterTheLastSync_OrTheLongJobsFailure()
    {
        IosHomeLines.LastProblem(Status(lastSync: Now)).Should().BeNull();

        var old = Status(lastSync: Now, log: [Entry(Now.AddMinutes(-10), "sync", "Sync failed: timeout")]);
        IosHomeLines.LastProblem(old).Should().BeNull("a failure before the last good sync is over");

        var refused = Status(lastSync: Now.AddHours(-1), log:
        [
            Entry(Now.AddMinutes(-1), "pin", "Refused 192.0.2.10:5610: it answered with another key than the one pinned when this phone was paired. Nothing was sent."),
            Entry(Now.AddMinutes(-2), "sync", "Sync failed: connection refused"),
            Entry(Now, "pairing", "something else"),
        ]);
        IosHomeLines.LastProblem(refused).Should().Contain("another key than the one pinned");

        // The refused key is logged a moment BEFORE the sync failure it causes: the cause is shown, not the generic TLS sentence.
        var sameRound = Status(lastSync: Now.AddHours(-1), log:
        [
            Entry(Now.AddMilliseconds(18), "sync", "Sync failed: The SSL connection could not be established, see inner exception."),
            Entry(Now, "pin", "Refused 192.0.2.10:5610: it answered with another key than the one pinned when this phone was paired. Nothing was sent."),
        ]);
        IosHomeLines.LastProblem(sameRound).Should().Contain("another key than the one pinned");
        var later = Status(lastSync: Now.AddHours(-1), log:
        [
            Entry(Now.AddMinutes(5), "sync", "Sync failed: connection refused"),
            Entry(Now, "pin", "Refused 192.0.2.10:5610: another key"),
        ]);
        IosHomeLines.LastProblem(later).Should().Contain("connection refused", "a refusal minutes earlier is another round");

        var load = Status(failure: new BlindAppFailure("First load", "401 Unauthorized", Now, 3));
        IosHomeLines.LastProblem(load).Should().Contain("First load failed").And.Contain("401").And.Contain("3 tries");
    }

    [Fact]
    public void TheSilenceWarningLine_SaysWhen_OrThatNotificationsAreOff_AndNothingBeforePairing()
    {
        IosHomeLines.SilenceWarning(Status(paired: false), Now, true).Should().BeNull();
        IosHomeLines.SilenceWarning(Status(), Now.AddDays(3), true).Should().Contain("warns you on");
        IosHomeLines.SilenceWarning(Status(), Now.AddDays(3), false).Should().Contain("Notifications are off");
        IosHomeLines.SilenceWarning(Status(), null, null).Should().BeNull("nothing is pending yet");
    }

    [Fact]
    public void TheLastBackgroundRound_RoundTrips_AndAnythingElseIsNoneYet()
    {
        IosLastRound.Describe(IosLastRound.Format(Now, "app refresh", "Synced.")).Should().Contain("(app refresh): Synced.");
        IosLastRound.Describe(null).Should().Contain("none yet");
        IosLastRound.Describe("garbage").Should().Contain("none yet");
    }

    [Fact]
    public void TheSilenceAlarm_IsPendingOnlyForAPairedCopy_ThreeDaysAfterTheLastContact()
    {
        BlindSilenceAlarm.Plan(Status(paired: false), Now).Should().BeNull("an unpaired copy has no node to miss");

        var notice = BlindSilenceAlarm.Plan(Status(lastSync: Now.AddHours(-1)), Now)!;
        notice.FireAt.Should().Be(Now.AddHours(-1) + TimeSpan.FromDays(3));
        notice.Body.Should().Contain("has not synced");

        BlindSilenceAlarm.Plan(Status(), Now)!.FireAt.Should().Be(Now + TimeSpan.FromDays(3), "never synced: counted from now");
    }

    [Fact]
    public void ASilenceThatAlreadyLasted_IsSaidAgainADayLater_NotAtOnce()
    {
        var notice = BlindSilenceAlarm.Plan(Status(lastSync: Now.AddDays(-5)), Now)!;
        notice.FireAt.Should().Be(Now + BlindSilenceAlarm.RemindAfter);
    }

    private static BlindPhoneLog.Entry Entry(DateTimeOffset at, string kind, string message) => new(at, kind, message);

    private static BlindAppStatus Status(bool paired = true, DateTimeOffset? lastSync = null, IReadOnlyList<BlindPhoneLog.Entry>? log = null,
        BlindAppFailure? failure = null) =>
        new(Guid.NewGuid(), "iPhone", paired, InitialLoadDone: true, lastSync, null, BlindBackupSchedule.Off, false, null, null, [], log ?? [],
            Endpoint: paired ? "https://192.0.2.10:5610" : null, LastFailure: failure);
}
