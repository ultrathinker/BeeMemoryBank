using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>Today's panel of one account (what the old single-account window showed): the view model over one controller and its work requests.</summary>
public sealed class AccountPanelViewModelTests
{
    private sealed class Rig
    {
        public FakeApp App { get; } = new();
        public FakeWorkRequests Work { get; } = new();
        public List<string> QrFor { get; } = [];
        public AccountPanelViewModel Vm { get; }

        public Rig()
        {
            Vm = new AccountPanelViewModel(App, Work, text =>
            {
                QrFor.Add(text);
                return [0x89, 0x50, 0x4E, 0x47];
            });
        }

        public async Task<AccountPanelViewModel> Shown(BlindAppStatus status)
        {
            App.Status = status;
            await Vm.RefreshAsync();
            return Vm;
        }
    }

    private static BlindPhoneLog.Entry Entry(string message, int minute = 0) =>
        new(new DateTimeOffset(2026, 10, 4, 12, minute, 0, TimeSpan.Zero), "k", message);

    // ---- what it shows ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BeforePairing_ItShowsTheIdentity_TheCodeWithItsQr_AndTheAnswerField()
    {
        var rig = new Rig();
        rig.App.PairingCodeValue = "bmb-blind-phone:?n=1";

        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false, awaiting: true));

        vm.IdentityText.Should().Be("Test PC");
        vm.NodeText.Should().StartWith("Node 11111111");
        vm.PairText.Should().Contain("Not paired yet");
        vm.ShowPairingCode.Should().BeTrue();
        vm.PairingCodeText.Should().Be("bmb-blind-phone:?n=1");
        vm.PairingQrPng.Should().NotBeNull();
        vm.CanConnect.Should().BeTrue();
        vm.CanRePair.Should().BeFalse();
        vm.CanSyncNow.Should().BeFalse();
        vm.CanBackupNow.Should().BeFalse();
        vm.LoadText.Should().Be("First load: starts after pairing.");
    }

    [Fact]
    public async Task TheQr_IsMadeOncePerCode_NotOnEveryRefresh()
    {
        var rig = new Rig();
        rig.App.PairingCodeValue = "code-A";
        await rig.Shown(FakeApp.StatusOf(paired: false, awaiting: true));
        await rig.Vm.RefreshAsync();
        await rig.Vm.RefreshAsync();
        rig.QrFor.Should().Equal("code-A");

        rig.App.PairingCodeValue = "code-B";
        await rig.Vm.RefreshAsync();
        rig.QrFor.Should().Equal("code-A", "code-B");

        rig.App.PairingCodeValue = null;
        await rig.Shown(FakeApp.StatusOf(awaiting: false));
        rig.Vm.PairingQrPng.Should().BeNull();
        rig.Vm.ShowPairingCode.Should().BeFalse();
    }

    [Fact]
    public async Task WhenPaired_ItShowsTheEndpoint_TheTimes_AndRePair()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(paired: true, loaded: true) with
        {
            LastSyncAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.Zero),
        });

        vm.PairText.Should().Be("Calls https://127.0.0.1:5610");
        vm.PairingMessage.Should().Contain("Paired. The code was used up");
        vm.ShowPairingCode.Should().BeFalse();
        vm.CanRePair.Should().BeTrue();
        vm.CanConnect.Should().BeFalse();
        vm.CanSyncNow.Should().BeTrue();
        vm.CanBackupNow.Should().BeTrue();
        vm.LoadText.Should().Be("First load: done.");
        vm.SyncText.Should().StartWith("Last sync: ").And.NotContain("never");
        vm.BackupText.Should().Be("Last backup: never");
    }

    // ---- the first-load line says what is really going on (fix round 1) ------------------------------------------------------------

    private static readonly DateTimeOffset FailedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static BlindAppFailure Failed(string message, int attempts = 1, string title = "First load") => new(title, message, FailedAt, attempts);

    [Fact]
    public async Task LoadText_BeforePairing_AndWhenDone_AndWhenAboutToStart()
    {
        var rig = new Rig();

        (await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false))).LoadText.Should().Be("First load: starts after pairing.");
        (await rig.Shown(FakeApp.StatusOf(paired: true, loaded: false))).LoadText.Should().Be("First load: starting.");
        (await rig.Shown(FakeApp.StatusOf(paired: true, loaded: true))).LoadText.Should().Be("First load: done.");
    }

    [Fact]
    public async Task LoadText_WhileRunning_ShowsTheProgress_OrJustThatItRuns()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, activeJob: "First load", progress: 0.4));
        vm.LoadText.Should().Be($"First load: running ({0.4:P0}).");

        await rig.Shown(FakeApp.StatusOf(loaded: false, activeJob: "First load"));
        vm.LoadText.Should().Be("First load: running.");

        // A sync or a backup running is not the first load running.
        await rig.Shown(FakeApp.StatusOf(loaded: false, activeJob: "Sync"));
        vm.LoadText.Should().Be("First load: starting.");
    }

    [Fact]
    public async Task LoadText_AFailedTry_ShowsTheReason_TheTries_AndTheNextTime_AndNeverBlamesAMissingCondition()
    {
        var rig = new Rig();
        var next = new DateTimeOffset(2026, 10, 4, 12, 1, 0, TimeSpan.Zero);
        rig.Work.FirstLoadRetry = new FirstLoadRetryInfo(next, TimeSpan.FromSeconds(60), 3, AtCeiling: false);

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false,
            lastFailure: Failed("Response status code does not indicate success: 401 (Unauthorized).", attempts: 3)));

        vm.LoadText.Should().StartWith("First load: the last try failed - Response status code does not indicate success: 401 (Unauthorized).");
        vm.LoadText.Should().Contain("probably not heard of this device", "a 401 right after pairing is the normal case and is said so");
        vm.LoadText.Should().Contain("(3 tries in a row)");
        vm.LoadText.Should().Contain($"Next try at {next.ToLocalTime():HH:mm:ss}.");
        vm.LoadText.Should().NotContain("waiting");
    }

    [Fact]
    public async Task LoadText_AFailedTry_ForAnotherReason_ShowsThatReason_NotTheHint()
    {
        var rig = new Rig();
        rig.Work.FirstLoadRetry = new FirstLoadRetryInfo(FailedAt.AddSeconds(30), TimeSpan.FromSeconds(30), 1, AtCeiling: false);

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, lastFailure: Failed("The SSL connection could not be established (pin mismatch).")));

        vm.LoadText.Should().Contain("pin mismatch").And.NotContain("probably not heard").And.NotContain("tries in a row");
    }

    [Fact]
    public async Task LoadText_AtTheCeiling_SaysItKeepsTryingEveryFifteenMinutes()
    {
        var rig = new Rig();
        rig.Work.FirstLoadRetry = new FirstLoadRetryInfo(FailedAt.AddMinutes(15), TimeSpan.FromMinutes(15), 9, AtCeiling: true);

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, lastFailure: Failed("The connection was refused.", 9)));

        vm.LoadText.Should().Contain("(9 tries in a row)").And.Contain("it keeps trying every 15 minutes until it works.");
        vm.LoadText.Should().Contain("Next try at ");
    }

    [Fact]
    public async Task LoadText_AFailedTry_WithNoRetryPlannedYet_SaysItTriesAgainSoon()
    {
        var rig = new Rig();
        rig.Work.FirstLoadRetry = null;

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, lastFailure: Failed("The connection was refused.")));

        vm.LoadText.Should().EndWith("It tries again soon.").And.NotContain("Next try at");
    }

    [Fact]
    public async Task LoadText_ALongOrMultiLineReason_IsCutToOneShortLine()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, lastFailure: Failed(new string('x', 400) + "\r\nsecond line")));

        vm.LoadText.Should().NotContain("\n").And.NotContain("second line").And.Contain("...");
        vm.LoadText.Length.Should().BeLessThan(300);
    }

    [Fact]
    public async Task LoadText_AFailureOfAnotherJob_IsNotShownAsAFirstLoadFailure()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(loaded: false, lastFailure: Failed("no room", 2, title: "Backup")));
        vm.LoadText.Should().Be("First load: starting.");

        // A done first load stays done whatever failed after it.
        await rig.Shown(FakeApp.StatusOf(loaded: true, lastFailure: Failed("no room", 2, title: "Backup")));
        vm.LoadText.Should().Be("First load: done.");
    }

    [Fact]
    public async Task TheLoadText_FollowsTheStateThroughItsWholeLife()
    {
        var rig = new Rig();
        rig.Work.FirstLoadRetry = new FirstLoadRetryInfo(FailedAt.AddSeconds(30), TimeSpan.FromSeconds(30), 1, AtCeiling: false);
        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false));
        var seen = new List<string> { vm.LoadText };

        foreach (var status in new[]
                 {
                     FakeApp.StatusOf(loaded: false, activeJob: "First load", progress: 0.1),
                     FakeApp.StatusOf(loaded: false, lastFailure: Failed("401")),
                     FakeApp.StatusOf(loaded: false, activeJob: "First load"),
                     FakeApp.StatusOf(loaded: true),
                 })
        {
            await rig.Shown(status);
            seen.Add(vm.LoadText);
        }

        seen.Should().HaveCount(5);
        seen[0].Should().Contain("starts after pairing");
        seen[1].Should().Contain("running (");
        seen[2].Should().Contain("the last try failed");
        seen[3].Should().Be("First load: running.");
        seen[4].Should().Be("First load: done.");
    }

    // ---- the key store (fix round 1) -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyStoreThatDoesNotAnswer_IsANotice_NotALostKey_AndNothingAsksForARemoval()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(keyStoreUnavailable: "UnauthorizedAccessException: The keychain is locked."));

        vm.HasKeyStoreNotice.Should().BeTrue();
        vm.KeyStoreNoticeText.Should().Contain("UnauthorizedAccessException: The keychain is locked.").And.Contain("no key is lost").And.Contain("Try again");
        vm.KeyStoreNoticeText.Should().NotContain("wipe", "a transient failure must not push the person to a wipe");
        vm.HasKeyLost.Should().BeFalse();
        vm.KeyLostText.Should().BeNull();
        vm.CanBackupNow.Should().BeTrue("the key is not known to be lost");
        vm.PairingMessage.Should().Contain("key store").And.NotContain("Disconnect and wipe");

        // It answers again: the notice goes.
        await rig.Shown(FakeApp.StatusOf());
        vm.HasKeyStoreNotice.Should().BeFalse();
        vm.KeyStoreNoticeText.Should().BeNull();
    }

    [Fact]
    public async Task AKeyStoreNotice_AndARealLostKey_AreNotTheSameThing()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(keyLost: true));

        vm.HasKeyLost.Should().BeTrue();
        vm.HasKeyStoreNotice.Should().BeFalse();
    }

    // ---- failures of the view model's own callbacks (fix round 1) ----------------------------------------------------------------

    [Fact]
    public async Task ACommandThatThrows_BecomesANotice_AndTheProblemIsLogged_NothingEscapes()
    {
        var rig = new Rig();
        var problems = new List<(string What, Exception Error)>();
        var vm = new AccountPanelViewModel(rig.App, rig.Work, _ => [], problem: (what, ex) => problems.Add((what, ex)));
        rig.App.Status = FakeApp.StatusOf();
        await vm.RefreshAsync();
        rig.Work.Failure = new InvalidOperationException("the scheduler is gone");

        var syncNow = () => vm.SyncNowCommand.Execute(null);
        var backupNow = () => vm.BackupNowCommand.Execute(null);

        syncNow.Should().NotThrow();
        backupNow.Should().NotThrow();
        vm.Notice.Should().Be("That did not work: an unexpected problem (InvalidOperationException).", "a plain exception is told by its kind: only a sentence this code threw on purpose is said as it is");
        vm.Notice.Should().NotContain("scheduler");
        problems.Should().HaveCount(2);
        problems[0].What.Should().Be("A command failed");
        problems[0].Error.Should().BeSameAs(rig.Work.Failure);
    }

    [Fact]
    public void ACommandWhoseCanExecuteThrows_IsJustDisabled_NotAnException()
    {
        var problems = new List<string>();
        var command = new RelayCommand(() => { }, () => throw new InvalidOperationException("state"), ex => problems.Add(ex.Message));

        var can = () => command.CanExecute(null);

        can.Should().NotThrow().Which.Should().BeFalse();
        problems.Should().Equal("state");
    }

    [Fact]
    public async Task AQrFunctionThatThrows_IsANotice_TheCodeStaysUsable_AndItIsNotRetriedOnEveryRefresh()
    {
        var rig = new Rig();
        var made = 0;
        var problems = new List<string>();
        var vm = new AccountPanelViewModel(rig.App, rig.Work, _ =>
        {
            made++;
            throw new InvalidOperationException("the QR library is missing");
        }, problem: (what, _) => problems.Add(what));
        rig.App.PairingCodeValue = "bmb-blind-phone:?n=1";
        rig.App.Status = FakeApp.StatusOf(paired: false, loaded: false, awaiting: true);

        var refresh = () => vm.RefreshAsync();
        await refresh.Should().NotThrowAsync();
        await vm.RefreshAsync();
        await vm.RefreshAsync();

        vm.PairingQrPng.Should().BeNull();
        vm.ShowPairingCode.Should().BeTrue("the text of the code is still there");
        vm.PairingCodeText.Should().Be("bmb-blind-phone:?n=1");
        vm.CanConnect.Should().BeTrue("the answer field stays usable");
        vm.Notice.Should().Contain("QR picture could not be made").And.NotContain("the QR library is missing").And.Contain("Copy the code below");
        made.Should().Be(1);
        problems.Should().Equal("Making the QR picture failed");
    }

    [Fact]
    public async Task AFailureWhileShowingTheStatus_IsANotice_AndTheNextRefreshShowsTheNextStatus()
    {
        var rig = new Rig();
        var problems = new List<string>();
        var vm = new AccountPanelViewModel(rig.App, rig.Work, _ => [], problem: (what, _) => problems.Add(what));
        rig.App.Status = FakeApp.StatusOf(name: "First");
        await vm.RefreshAsync();

        rig.App.Status = FakeApp.StatusOf(name: "Second") with { Backups = null! };
        var refresh = () => vm.RefreshAsync();
        await refresh.Should().NotThrowAsync();
        vm.Notice.Should().Contain("Could not show the status");
        problems.Should().Equal("Showing the status failed");

        rig.App.Status = FakeApp.StatusOf(name: "Third");
        await vm.RefreshAsync();
        vm.IdentityText.Should().Be("Third");
    }

    [Fact]
    public async Task ARunningJob_ShowsItsNameAndProgress()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(activeJob: "First load", progress: 0.4));
        vm.JobVisible.Should().BeTrue();
        vm.JobText.Should().Be($"First load: {0.4:P0}");
        vm.JobProgress.Should().Be(0.4);

        await rig.Shown(FakeApp.StatusOf(activeJob: "Sync"));
        vm.JobText.Should().Be("Sync");
        vm.JobProgress.Should().Be(0);

        await rig.Shown(FakeApp.StatusOf());
        vm.JobVisible.Should().BeFalse();
    }

    [Fact]
    public async Task TheLog_ShowsTheLatestLines()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(log: [Entry("newest", 5), Entry("older", 1)]));

        var lines = vm.LogText.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().EndWith("newest");
        lines[1].Should().EndWith("older");
    }

    [Fact]
    public async Task AStoredTextAndEveryUntrustedPairingLine_AreNotShownOnThePanel()
    {
        var rig = new Rig();
        var hostilePairing = "Parameter 'filePath' token=hunter2";
        var currentPairing = "Paired: calls https://127.0.0.1:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).";
        var log = new List<BlindPhoneLog.Entry>
        {
            new(DateTimeOffset.UtcNow, "sync", @"Sync failed: C:\Users\someone\canary-123\state.json is locked"),
            new(DateTimeOffset.UtcNow.AddMinutes(-1), "backup", "a clean line of the log"),
            new(DateTimeOffset.UtcNow.AddMinutes(-2), BlindPhoneLog.PairingKind, hostilePairing, Version: 0),
            new(DateTimeOffset.UtcNow.AddMinutes(-3), BlindPhoneLog.PairingKind, hostilePairing),
            new(DateTimeOffset.UtcNow.AddMinutes(-4), BlindPhoneLog.PairingKind, currentPairing),
        };

        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false, startError: "Could not open /Users/someone/canary-123/db",
            keyStoreUnavailable: "IOException: Access to the path '/Users/someone/canary-123' is denied", log: log));

        vm.StartErrorText.Should().Contain(BlindFailureText.StoredTextHidden).And.NotContain("canary-123").And.NotContain("someone");
        vm.KeyStoreNoticeText.Should().Contain(BlindFailureText.StoredTextHidden).And.NotContain("canary-123").And.NotContain("someone");
        vm.LogText.Should().NotContain("canary-123").And.NotContain("state.json").And.NotContain(hostilePairing).And.Contain("a clean line of the log").And.Contain(currentPairing);
        vm.LogText.Split('\n').Count(line => line.Contains(BlindFailureText.StoredTextHidden, StringComparison.Ordinal)).Should().Be(3);
        vm.LogText.Should().Contain(BlindFailureText.StoredTextHidden);
    }

    [Fact]
    public async Task ACleanKeyStoreReason_IsShownAsItIs()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(keyStoreUnavailable: "BlindSecretStoreException: The backup key could not be read from the Keychain (Keychain status -25308: User interaction is not allowed)"));

        vm.KeyStoreNoticeText.Should().Contain("-25308").And.Contain("User interaction is not allowed").And.NotContain(BlindFailureText.StoredTextHidden);
    }

    [Fact]
    public async Task AKeyLostCopy_AndAStartError_AreSaidPlainly()
    {
        var rig = new Rig();

        var vm = await rig.Shown(FakeApp.StatusOf(keyLost: true));
        vm.HasKeyLost.Should().BeTrue();
        vm.KeyLostText.Should().Contain("a new key is never made silently");
        vm.CanBackupNow.Should().BeFalse();

        await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false, startError: "Disconnect and wipe required."));
        vm.HasStartError.Should().BeTrue();
        vm.StartErrorText.Should().Contain("Disconnect and wipe required.");
        vm.PairingMessage.Should().Contain("could not set itself up");
    }

    [Fact]
    public async Task TheBackups_AreListed_AndSaveToSendsTheNameToTheApp()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(backups: [new BlindAppBackup("bmb-phone-2.bmbbackup", 5 * 1024 * 1024), new BlindAppBackup("bmb-phone-1.bmbbackup", 1024)]));

        vm.HasBackups.Should().BeTrue();
        vm.Backups.Select(b => b.Name).Should().Equal("bmb-phone-2.bmbbackup", "bmb-phone-1.bmbbackup");
        vm.Backups[0].SizeText.Should().Be("5120 KiB");

        vm.Backups[1].SaveCommand.Execute(null);
        await WaitFor(() => vm.Notice?.StartsWith("Saved") == true);

        rig.App.Exported.Should().Equal("bmb-phone-1.bmbbackup");
        vm.Notice.Should().Contain("bmb-phone-1.bmbbackup").And.Contain("chosen place");
    }

    [Fact]
    public async Task SaveTo_WhenThePickerIsCancelled_SaysNothingWasSaved_AndOnAFailure_SaysWhy()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(backups: [new BlindAppBackup("a.bmbbackup", 10)]));

        rig.App.ExportBody = _ => throw new OperationCanceledException();
        vm.Backups[0].SaveCommand.Execute(null);
        await WaitFor(() => vm.Notice == "Nothing was saved.");

        rig.App.ExportBody = _ => throw new IOException("disk full");
        vm.Backups[0].SaveCommand.Execute(null);
        await WaitFor(() => vm.Notice?.Contains("Could not save the backup") == true);
        vm.Notice.Should().NotContain("disk full", "a file failure is told by its kind, not by its message");
    }

    // ---- what it does -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Connect_WithAWrongCode_ShowsTheReason_AndStaysUnpaired()
    {
        var rig = new Rig();
        rig.App.PairingCodeValue = "code";
        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false, awaiting: true));
        vm.CallCodeInput = "bmb-blind-call:?junk";
        rig.App.AcceptError = "This is not a connection code from the computer. Copy it again.";

        vm.ConnectCommand.Execute(null);

        vm.ConnectError.Should().Be("This is not a connection code from the computer. Copy it again.");
        vm.HasConnectError.Should().BeTrue();
        vm.CallCodeInput.Should().Be("bmb-blind-call:?junk", "the user can correct it");
        rig.Work.Heavies.Should().Be(0);
        rig.App.Accepted.Should().Equal("bmb-blind-call:?junk");

        vm.CallCodeInput = "changed";
        vm.HasConnectError.Should().BeFalse("typing again clears the old error");
    }

    [Fact]
    public async Task Connect_WithTheRightCode_ClearsTheField_AndAsksForTheFirstLoadAtOnce()
    {
        var rig = new Rig();
        rig.App.PairingCodeValue = "code";
        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false, awaiting: true));
        vm.CallCodeInput = "bmb-blind-call:?ok";
        rig.App.AcceptError = null;

        vm.ConnectCommand.Execute(null);

        vm.CallCodeInput.Should().BeEmpty();
        vm.ConnectError.Should().BeNull();
        vm.Notice.Should().Contain("Paired").And.Contain("first load");
        rig.Work.Heavies.Should().Be(1);
    }

    [Fact]
    public async Task SyncNow_AndBackUpNow_AskTheScheduler_AndOnlyWhenTheyMake_Sense()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(paired: false, loaded: false));
        vm.SyncNowCommand.CanExecute(null).Should().BeFalse();
        vm.BackupNowCommand.CanExecute(null).Should().BeFalse();

        await rig.Shown(FakeApp.StatusOf(paired: true, loaded: false));
        vm.SyncNowCommand.CanExecute(null).Should().BeFalse("a sync needs the first load to be done");
        vm.BackupNowCommand.CanExecute(null).Should().BeTrue();

        await rig.Shown(FakeApp.StatusOf(paired: true, loaded: true));
        vm.SyncNowCommand.Execute(null);
        rig.Work.Syncs.Should().Be(1);
        // BMB-311: a long operation is running, so no second one can be started until the first has ended.
        vm.BackupNowCommand.CanExecute(null).Should().BeFalse();
        rig.Work.Report("Sync finished.");
        vm.BackupNowCommand.Execute(null);
        rig.Work.Backups.Should().Be(1);
        vm.Notice.Should().Be("Backup asked for.");
    }

    [Fact]
    public async Task RePair_StartsANewCode()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf());
        vm.RePairCommand.Execute(null);
        rig.App.RePairCalls.Should().Be(1);
        vm.Notice.Should().Contain("The current connection stays");
    }

    [Fact]
    public async Task TheSchedulePicker_ShowsTheStatus_AndChangesGoToTheApp_ButRefreshesDoNot()
    {
        var rig = new Rig();
        var vm = await rig.Shown(FakeApp.StatusOf(schedule: BlindBackupSchedule.Daily));
        vm.ScheduleIndex.Should().Be(1);
        vm.ScheduleOptions.Should().Equal("Off", "Daily", "Weekly");
        rig.App.LastSchedule.Should().BeNull("showing the stored schedule does not write it back");

        vm.ScheduleIndex = 0;
        rig.App.LastSchedule.Should().Be(BlindBackupSchedule.Off);
        vm.ScheduleIndex = 2;
        rig.App.LastSchedule.Should().Be(BlindBackupSchedule.Weekly);
    }

    // ---- events -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ControllerChangedEvents_AreCoalescedIntoOneRefresh_OnTheUiThread()
    {
        var app = new FakeApp();
        var posted = new List<Action>();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => [], posted.Add);
        app.Status = FakeApp.StatusOf(name: "First");
        app.RaiseChanged();
        app.RaiseChanged();
        app.RaiseChanged();

        posted.Should().HaveCount(1, "three events before the UI thread got to it are one refresh");
        posted[0]();
        await WaitFor(() => vm.IdentityText == "First");

        app.RaiseChanged();
        posted.Should().HaveCount(2);
        vm.Dispose();
        app.RaiseChanged();
        posted.Should().HaveCount(2, "a disposed view model does not listen");
    }

    [Fact]
    public async Task AUserJobReport_BecomesTheNotice()
    {
        var rig = new Rig();
        await rig.Shown(FakeApp.StatusOf());

        rig.Work.Report("Backup made.");

        rig.Vm.Notice.Should().Be("Backup made.");
    }

    [Fact]
    public async Task AFailingStatusRead_IsShownAsANotice_NotThrown()
    {
        var rig = new Rig();
        var vm = new AccountPanelViewModel(new ThrowingApp(), rig.Work, _ => []);

        await vm.RefreshAsync();

        vm.Notice.Should().Contain("Could not read the status").And.NotContain("db is gone");
    }

    private sealed class ThrowingApp : IBlindAppController
    {
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public BlindAppStatus GetStatus() => throw new InvalidOperationException("db is gone");
        public string? PairingCode() => null;
        public string? AcceptCallCode(string text) => null;
        public void StartRePair() { }
        public void SetSchedule(BlindBackupSchedule schedule) { }
        public Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> RequestSyncAsync(CancellationToken ct = default) => Task.FromResult("");
        public Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default) => Task.FromResult(0L);
        public Task DisconnectAndWipeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task WaitFor(Func<bool> condition, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("the condition was not met in time");
            await Task.Delay(10);
        }
    }
}
