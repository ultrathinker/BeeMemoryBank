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
        view.KeyStoreText.Should().Contain("Disconnect and wipe", "the way out is named");
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

        view.KeyLostText.Should().Be(BlindHomeView.KeyLostMessage).And.Contain("Disconnect and wipe");
        view.CanBackupNow.Should().BeFalse();
        view.KeyStoreText.Should().BeNull();
    }

    [Fact]
    public void AStartError_IsShown_NotHiddenBehindThePairingCode()
    {
        var view = BlindHomeView.Build(Status(isPaired: false, startError: "database is locked"), pairingCode: null, backupRunning: false);

        view.StartErrorText.Should().Contain("database is locked").And.Contain("Disconnect and wipe");
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
    public void AStatusThatCouldNotBeRead_HasASentenceWithTheWayOut()
    {
        BlindHomeView.StatusFailedText(new IOException("disk gone")).Should().Contain("disk gone").And.Contain("Disconnect and wipe");
    }

    private static BlindAppStatus Status(bool isPaired = true, bool backupKeyLost = false, string? keyStoreUnavailable = null,
        string? startError = null, string? activeJob = null, double? progress = null,
        BlindBackupSchedule schedule = BlindBackupSchedule.Off, IReadOnlyList<BlindAppBackup>? backups = null) =>
        new(Guid.NewGuid(), "Phone", isPaired, InitialLoadDone: true, null, null, schedule, backupKeyLost, activeJob, progress,
            backups ?? [], [], Endpoint: isPaired ? "https://pc.example:5300" : null, AwaitingAnswer: false, StartError: startError,
            KeyStoreUnavailable: keyStoreUnavailable);
}
