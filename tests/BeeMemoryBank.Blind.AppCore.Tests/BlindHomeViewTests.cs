using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// F-04: what the Android screen shows is worked out from the controller's UI-safe status. The states a phone's key storage can produce
/// (it did not answer; the key is lost; the set-up failed) each have a sentence, and none of them needs the page to read a key.
/// </summary>
public sealed class BlindHomeViewTests
{
    [Fact]
    public void AKeyStoreThatDoesNotAnswer_IsSaid_WithTheRecoveryChoice_AndIsNotALostKey()
    {
        var view = BlindHomeView.Build(Status(keyStoreUnavailable: "KeyStoreException: key permanently invalidated"), pairingCode: null, backupRunning: false);

        view.KeyStoreText.Should().Contain("did not answer").And.Contain("key permanently invalidated");
        view.KeyStoreText.Should().Contain("remove this account from the accounts list and add it again", "the way out is named, and it is one a head has").And.NotContain("Disconnect and wipe");
        view.KeyStoreText.Should().Contain("no key is lost");
        view.KeyLostText.Should().BeNull("a store that does not answer is not a lost key");
        view.CanBackupNow.Should().BeFalse("a backup needs the key");
        view.CanRePair.Should().BeFalse();
        view.PairingMessage.Should().Contain("key storage");
        view.ShowPairingCode.Should().BeFalse();
    }

    [Fact]
    public void ALostBackupKey_DisablesBackups_AndSaysToWipeAndPairAgain()
    {
        var view = BlindHomeView.Build(Status(backupKeyLost: true), pairingCode: null, backupRunning: false);

        view.KeyLostText.Should().Be(BlindHomeView.KeyLostMessage).And.Contain("Remove this account from the accounts list").And.NotContain("Disconnect and wipe");
        view.CanBackupNow.Should().BeFalse();
        view.KeyStoreText.Should().BeNull();
    }

    [Fact]
    public void AStartError_IsShown_NotHiddenBehindThePairingCode()
    {
        var view = BlindHomeView.Build(Status(isPaired: false, startError: "database is locked"), pairingCode: null, backupRunning: false);

        view.StartErrorText.Should().Contain("database is locked").And.Contain("remove this account from the accounts list").And.NotContain("Disconnect and wipe");
        view.PairingMessage.Should().Contain("could not be set up");
    }

    [Fact]
    public void AwaitingAnswer_ShowsTheCode_AndAllowsConnecting()
    {
        var view = BlindHomeView.Build(Status(isPaired: false), pairingCode: "bmb-blind-phone:abc", backupRunning: false);

        view.ShowPairingCode.Should().BeTrue();
        view.PairingCode.Should().Be("bmb-blind-phone:abc");
        view.CanConnect.Should().BeTrue();
        view.CanRePair.Should().BeFalse();
        view.PairingMessage.Should().BeEmpty();
    }

    [Fact]
    public void APairedPhoneWhoseCodeWasUsedUp_OffersRePair_NotConnect()
    {
        var view = BlindHomeView.Build(Status(), pairingCode: null, backupRunning: false);

        view.CanConnect.Should().BeFalse();
        view.CanRePair.Should().BeTrue();
        view.PairingMessage.Should().Contain("Re-pair");
        view.Pair.Should().Be("Calls https://pc.example:5300");
    }

    [Fact]
    public void WhileABackupRuns_BackUpNowIsOff_AndSaysSo_SoASecondTapCannotStartASecondJob()
    {
        var idle = BlindHomeView.Build(Status(), null, backupRunning: false);
        var busy = BlindHomeView.Build(Status(), null, backupRunning: true);

        idle.CanBackupNow.Should().BeTrue();
        idle.BackupNowText.Should().Be("Back up now");
        busy.CanBackupNow.Should().BeFalse();
        busy.BackupNowText.Should().Contain("running");
        busy.Job.Should().NotBeNull();

        var controllerJob = BlindHomeView.Build(Status(activeJob: "Backup", progress: 0.5), null, backupRunning: false);
        controllerJob.CanBackupNow.Should().BeFalse("a job the controller runs also blocks it");
        controllerJob.Job.Should().StartWith("Backup");
    }

    [Fact]
    public void ANotPairedPhone_CannotBackUpOrSync()
    {
        var view = BlindHomeView.Build(Status(isPaired: false), "code", backupRunning: false);

        view.CanBackupNow.Should().BeFalse();
        view.CanSyncNow.Should().BeFalse();
        view.Load.Should().Be("First load: done.", "the sample status has the first load done");
    }

    [Fact]
    public void WhileAJobRuns_SyncNowIsOff_AndComesBackWhenItEnds()
    {
        BlindHomeView.Build(Status(activeJob: "Sync"), null, backupRunning: false).CanSyncNow.Should().BeFalse("a sync is running: a second tap would ask for another");
        BlindHomeView.Build(Status(activeJob: "Backup", progress: 0.2), null, backupRunning: false).CanSyncNow.Should().BeFalse();
        BlindHomeView.Build(Status(), null, backupRunning: false).CanSyncNow.Should().BeTrue();
    }

    [Fact]
    public void ABackupListAndTheScheduleAreShown()
    {
        var status = Status(schedule: BlindBackupSchedule.Weekly, backups: [new BlindAppBackup("a.bmbbackup", 4096)]);

        var view = BlindHomeView.Build(status, null, false);

        view.ScheduleIndex.Should().Be(2);
        view.Backups.Should().Contain("a.bmbbackup").And.Contain("4 KiB");
        view.CanSaveTo.Should().BeTrue();
        BlindHomeView.Build(Status(), null, false).CanSaveTo.Should().BeFalse();
    }

    [Fact]
    public void AStatusThatCouldNotBeRead_HasASentenceWithTheWayOut_ToldByTheKindOfFailure_NeverByItsMessage()
    {
        var text = BlindHomeView.StatusFailedText(new IOException("disk gone at /private/var/mobile/secret-path"));

        text.Should().Contain("Could not read the state of the phone").And.Contain("storage of this device").And.Contain("remove this account from the accounts list").And.NotContain("Disconnect and wipe");
        text.Should().NotContain("disk gone").And.NotContain("secret-path", "the message of a library exception is not the app's sentence");
    }

    [Fact]
    public void AStatusFailureThatCameThroughTheHttpStack_NeverShowsTheAddressOrTheRedirectTarget()
    {
        const string canary = "https://canary-host.invalid/path?token=CANARY-TOKEN-1234";
        foreach (var ex in new Exception[]
                 {
                     new HttpRequestException("Request to " + canary + " was redirected (302) to " + canary + "; redirects are not followed."),
                     new InvalidOperationException("outer " + canary, new HttpRequestException(canary)),
                     new AggregateException(new Exception("wrapper " + canary, new HttpRequestException(canary))),
                 })
        {
            var text = BlindHomeView.StatusFailedText(ex);
            text.Should().NotContain("CANARY").And.NotContain("canary-host").And.NotContain("token=", ex.GetType().Name);
            text.Should().Contain("Could not read the state of the phone");
        }
    }

    [Fact]
    public void TheNewestProblem_IsAContactFailureAfterTheLastSync_OrTheLongJobsFailure()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        BlindPhoneLog.Entry Entry(DateTimeOffset at, string kind, string message) => new(at, kind, message);

        BlindHomeView.ProblemText(Status() with { LastSyncAt = now }).Should().BeNull();
        BlindHomeView.Build(Status() with { LastSyncAt = now }, null, false).Problem.Should().BeNull();

        var old = Status() with { LastSyncAt = now, RecentLog = [Entry(now.AddMinutes(-10), "sync", "Sync failed: timeout")] };
        BlindHomeView.ProblemText(old).Should().BeNull("a failure before the last good sync is over");

        var refused = Status() with
        {
            LastSyncAt = now.AddHours(-1),
            RecentLog =
            [
                Entry(now.AddMinutes(-1), "pin", "Refused 192.0.2.10:5610: it answered with another key than the one pinned when this phone was paired. Nothing was sent."),
                Entry(now.AddMinutes(-2), "sync", "Sync failed: connection refused"),
                Entry(now, "pairing", "something else"),
            ],
        };
        BlindHomeView.ProblemText(refused).Should().Contain("another key than the one pinned");

        // The refused key is logged a moment BEFORE the sync failure it causes: the cause is shown, not the generic TLS sentence.
        var sameRound = Status() with
        {
            LastSyncAt = now.AddHours(-1),
            RecentLog =
            [
                Entry(now.AddMilliseconds(18), "sync", "Sync failed: The SSL connection could not be established, see inner exception."),
                Entry(now, "pin", "Refused 192.0.2.10:5610: it answered with another key than the one pinned when this phone was paired. Nothing was sent."),
            ],
        };
        BlindHomeView.ProblemText(sameRound).Should().Contain("another key than the one pinned");
        var later = Status() with
        {
            LastSyncAt = now.AddHours(-1),
            RecentLog = [Entry(now.AddMinutes(5), "sync", "Sync failed: connection refused"), Entry(now, "pin", "Refused 192.0.2.10:5610: another key")],
        };
        BlindHomeView.ProblemText(later).Should().Contain("connection refused", "a refusal minutes earlier is another round");

        var load = Status() with { LastFailure = new BlindAppFailure("First load", "401 Unauthorized", now, 3) };
        var problem = BlindHomeView.Build(load, null, false).Problem;
        problem.Should().Contain("First load failed").And.Contain("401").And.Contain("3 tries");
    }

    [Fact]
    public void TheLimitsExplanations_AreTheSharedRecordsOwn_AndNameTheNumbersTheFormEnforces()
    {
        BlindLimitsForm.Explanation.Should().Contain("Leave a box empty for no limit").And.Contain("0 pauses")
            .And.Contain($"{BlindLimitsForm.MinRequestsPerDay} requests").And.Contain(BlindSizeFormat.Format(BlindLimitsForm.MinTrafficPerDay)).And.Contain("15 minutes");
        BlindDeviceLimitsForm.Explanation.Should().Contain("0 pauses the whole device").And.Contain(BlindSizeFormat.Format(BlindLimitsForm.MinTrafficPerDay));
    }

    private static BlindAppStatus Status(bool isPaired = true, bool backupKeyLost = false, string? keyStoreUnavailable = null,
        string? startError = null, string? activeJob = null, double? progress = null,
        BlindBackupSchedule schedule = BlindBackupSchedule.Off, IReadOnlyList<BlindAppBackup>? backups = null) =>
        new(Guid.NewGuid(), "Phone", isPaired, InitialLoadDone: true, null, null, schedule, backupKeyLost, activeJob, progress,
            backups ?? [], [], Endpoint: isPaired ? "https://pc.example:5300" : null, AwaitingAnswer: false, StartError: startError,
            KeyStoreUnavailable: keyStoreUnavailable);
}
