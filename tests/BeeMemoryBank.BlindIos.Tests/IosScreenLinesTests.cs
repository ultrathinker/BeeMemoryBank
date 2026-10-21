using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// What the iPhone screen adds to the shared view - only what is iOS's own: the line about the "silent node" warning and the last background round
/// iOS granted. (The last contact and the newest problem are the shared view's; tested in Blind.AppCore.Tests.)
/// </summary>
public class IosScreenLinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

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

    // The silence notification (one per account) is tested in IosSilenceTests.

    private static BlindPhoneLog.Entry Entry(DateTimeOffset at, string kind, string message) => new(at, kind, message);

    private static BlindAppStatus Status(bool paired = true, DateTimeOffset? lastSync = null, IReadOnlyList<BlindPhoneLog.Entry>? log = null,
        BlindAppFailure? failure = null) =>
        new(Guid.NewGuid(), "iPhone", paired, InitialLoadDone: true, lastSync, null, BlindBackupSchedule.Off, false, null, null, [], log ?? [],
            Endpoint: paired ? "https://192.0.2.10:5610" : null, LastFailure: failure);
}
