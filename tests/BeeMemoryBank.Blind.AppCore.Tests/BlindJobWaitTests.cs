using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// BMB-311 fix round (F3): the screens of the blind apps keep a button locked for as long as the job it started is open, judged from the state of the phone
/// and not from a timer. The old screens waited 20 seconds for the job to be seen and an hour for it to end (Android) or 30 minutes (iPhone) and then let
/// the button go while the job might still run; these tests are the rules that replace the clocks.
/// </summary>
public sealed class BlindJobWaitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static BlindAppStatus Status(
        bool initialLoadDone = true, DateTimeOffset? lastSync = null, DateTimeOffset? lastBackup = null, string? activeJob = null, BlindAppFailure? failure = null) =>
        new(Guid.NewGuid(), "Phone", IsPaired: true, initialLoadDone, lastSync, lastBackup, BlindBackupSchedule.Off, BackupKeyLost: false, activeJob, null,
            [], [], Endpoint: "https://pc.example:5300", LastFailure: failure);

    [Fact]
    public void AJobThatRuns_IsOpen_HoweverLongItHasBeen()
    {
        var ask = BlindJobWait.Ask(BlindJobWait.Sync, Status(), T0);

        foreach (var later in new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(31), TimeSpan.FromHours(3), TimeSpan.FromDays(2) })
        {
            var outcome = BlindJobWait.Judge(ask, Status(activeJob: "Sync"), running: false, T0 + later);
            outcome.Phase.Should().Be(BlindJobPhase.Running, $"after {later} the job is still running: the lock does not expire");
            outcome.IsOpen.Should().BeTrue();
        }
        BlindJobWait.Judge(ask, Status(), running: true, T0 + TimeSpan.FromHours(3)).Phase.Should().Be(BlindJobPhase.Running, "the activity registry says so too");
    }

    [Fact]
    public void AJobThatWasAskedForAndHasNotStarted_IsQueued_UntilTheGraceEnds_ThenUnreported()
    {
        var ask = BlindJobWait.Ask(BlindJobWait.Backup, Status(), T0);

        BlindJobWait.Judge(ask, Status(), false, T0 + TimeSpan.FromSeconds(30)).Phase.Should().Be(BlindJobPhase.Queued, "WorkManager may take a moment: it is not over");
        BlindJobWait.Judge(ask, Status(), false, T0 + TimeSpan.FromMinutes(1.9)).IsOpen.Should().BeTrue();

        var lost = BlindJobWait.Judge(ask, Status(), false, T0 + BlindJobWait.QueuedGrace + TimeSpan.FromSeconds(1));
        lost.Phase.Should().Be(BlindJobPhase.Unreported);
        lost.IsOpen.Should().BeFalse();
        lost.Message.Should().Contain("did not report").And.Contain("backup").And.NotContain("finished", "a job that left no mark is not said to have finished");
    }

    [Fact]
    public void TheFirstLoad_EndsWhenItIsDone_OrWhenItsFailureIsRecordedAfterTheAsk()
    {
        var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, Status(initialLoadDone: false), T0);

        BlindJobWait.Judge(ask, Status(initialLoadDone: false), false, T0 + TimeSpan.FromSeconds(5)).Phase.Should().Be(BlindJobPhase.Queued);
        BlindJobWait.Judge(ask, Status(initialLoadDone: true), false, T0 + TimeSpan.FromMinutes(5)).Phase.Should().Be(BlindJobPhase.Finished);

        var failed = BlindJobWait.Judge(ask,
            Status(initialLoadDone: false, failure: new BlindAppFailure("First load", "The computer did not answer.", T0 + TimeSpan.FromSeconds(40), 1)), false, T0 + TimeSpan.FromMinutes(1));
        failed.Phase.Should().Be(BlindJobPhase.Failed);
        failed.Message.Should().Be("The computer did not answer.");
    }

    [Fact]
    public void AFailureFromBeforeTheAsk_IsNotThisJobsEnd()
    {
        var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, Status(initialLoadDone: false), T0);
        var old = new BlindAppFailure("First load", "An earlier try failed.", T0 - TimeSpan.FromMinutes(10), 3);

        BlindJobWait.Judge(ask, Status(initialLoadDone: false, failure: old), false, T0 + TimeSpan.FromSeconds(5)).Phase.Should().Be(BlindJobPhase.Queued);
    }

    [Fact]
    public void ASyncAndABackup_EndWhenTheirTimeMovedPastTheOneNotedAtTheAsk()
    {
        var syncAsk = BlindJobWait.Ask(BlindJobWait.Sync, Status(lastSync: T0 - TimeSpan.FromHours(1)), T0);
        BlindJobWait.Judge(syncAsk, Status(lastSync: T0 - TimeSpan.FromHours(1)), false, T0 + TimeSpan.FromSeconds(10)).Phase.Should().Be(BlindJobPhase.Queued, "the old time is not this sync");
        BlindJobWait.Judge(syncAsk, Status(lastSync: T0 + TimeSpan.FromSeconds(8)), false, T0 + TimeSpan.FromSeconds(10)).Phase.Should().Be(BlindJobPhase.Finished);

        var neverSynced = BlindJobWait.Ask(BlindJobWait.Sync, Status(lastSync: null), T0);
        BlindJobWait.Judge(neverSynced, Status(lastSync: T0 + TimeSpan.FromSeconds(3)), false, T0 + TimeSpan.FromSeconds(5)).Phase.Should().Be(BlindJobPhase.Finished);

        var backupAsk = BlindJobWait.Ask(BlindJobWait.Backup, Status(lastBackup: T0 - TimeSpan.FromDays(1)), T0);
        BlindJobWait.Judge(backupAsk, Status(lastBackup: T0 + TimeSpan.FromMinutes(1)), false, T0 + TimeSpan.FromMinutes(1)).Phase.Should().Be(BlindJobPhase.Finished);
    }

    [Fact]
    public void TheStillRunningNoticeIsAboutNotStartingItAgain_AndTheWaitHasNoLimitOfItsOwn()
    {
        BlindJobWait.StillRunningText.Should().Contain("do not start it again");
        BlindJobWait.StillRunningAfter.Should().Be(TimeSpan.FromMinutes(30));

        // There is no phase for "the clock ran out while the job still ran": a job that runs is Running, at any age (see AJobThatRuns_IsOpen_HoweverLongItHasBeen).
        Enum.GetNames<BlindJobPhase>().Should().BeEquivalentTo(["Running", "Queued", "Finished", "Failed", "Unreported"]);
    }
}
