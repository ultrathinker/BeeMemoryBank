using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// BMB-311, the blind desktop copy: a long operation (connect with its first load, sync, backup, save, wipe) puts up a "please wait" pop-up
/// over the whole window, no command can be started a second time while it runs, and the pop-up ends with the job. The window binds the
/// pop-up to <see cref="MainViewModel.IsBusy"/>; the commands bind to the view model, so a disabled command is a disabled button.
/// </summary>
public sealed class MainViewModelBusyTests
{
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    private static async Task<(MainViewModel Vm, FakeApp App, FakeWorkRequests Work)> PairedAsync(Action<FakeApp>? configure = null)
    {
        var app = new FakeApp();
        var work = new FakeWorkRequests();
        configure?.Invoke(app);
        var vm = new MainViewModel(app, work, new FakeAutostart(), _ => []);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);
        await vm.RefreshAsync();
        return (vm, app, work);
    }

    [Fact]
    public async Task SyncNow_PutsUpThePopUp_AndNoSecondCommandCanStart()
    {
        var (vm, _, work) = await PairedAsync();
        vm.IsBusy.Should().BeFalse();

        vm.SyncNowCommand.Execute(null);

        vm.IsBusy.Should().BeTrue();
        vm.BusyTitle.Should().Be("Syncing");
        vm.BusyText.Should().Contain("Please wait");
        vm.SyncNowCommand.CanExecute(null).Should().BeFalse("the same button cannot be pressed again");
        vm.BackupNowCommand.CanExecute(null).Should().BeFalse("nor can another long operation be started");
        vm.RePairCommand.CanExecute(null).Should().BeFalse();
        vm.SyncNowCommand.Execute(null);
        work.Syncs.Should().Be(1, "a second press asks for nothing");
    }

    [Fact]
    public async Task ThePopUp_EndsWhenTheJobTheUserAskedForReportsItsEnd()
    {
        var (vm, _, work) = await PairedAsync();
        vm.BackupNowCommand.Execute(null);
        vm.IsBusy.Should().BeTrue();
        vm.BusyTitle.Should().Be("Backing up");

        work.Report("Backup finished.");

        vm.IsBusy.Should().BeFalse();
        vm.Notice.Should().Be("Backup finished.");
        vm.BackupNowCommand.CanExecute(null).Should().BeTrue();
        vm.SyncNowCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ThePopUp_FollowsTheRunningJob_AndEndsWhenTheStatusShowsItIsOver()
    {
        var (vm, app, _) = await PairedAsync();
        vm.SyncNowCommand.Execute(null);

        app.Status = FakeApp.StatusOf(activeJob: "Sync", progress: 0.4);
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue();
        vm.BusyText.Should().Contain("Sync").And.Contain("40");

        app.Status = FakeApp.StatusOf(lastSyncAt: DateTimeOffset.UtcNow);   // the job is over: the time of the last sync moved
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse("the job left its mark: it is over");
    }

    [Fact]
    public async Task ThePopUp_WaitsForTheJobToShowUp_ItDoesNotEndBecauseTheStatusIsStillIdle()
    {
        var (vm, app, _) = await PairedAsync();
        vm.SyncNowCommand.Execute(null);

        // The job has been asked for but the status still shows nothing running (the loop has not picked it up yet): the pop-up stays.
        app.Status.ActiveJob.Should().BeNull();
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue();
    }

    [Fact]
    public async Task WhenAskingFails_ThePopUpDoesNotStayUp_AndTheCommandIsUsableAgain()
    {
        var (vm, _, work) = await PairedAsync();
        work.Failure = new InvalidOperationException("the scheduler is gone");

        vm.SyncNowCommand.Execute(null);

        vm.IsBusy.Should().BeFalse();
        vm.Notice.Should().Be("That did not work: the scheduler is gone");
        vm.SyncNowCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task AJobThatKeepsRunning_KeepsThePopUpUp_HoweverLongItTakes_AndSaysToNotStartItAgain()
    {
        // BMB-311 fix round: the pop-up used to end after an hour whatever the status said, giving the commands back with the job still going.
        var time = new ManualTimeProvider();
        var app = new FakeApp();
        var work = new FakeWorkRequests();
        var vm = new MainViewModel(app, work, new FakeAutostart(), _ => [], time: time);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);
        await vm.RefreshAsync();
        vm.BackupNowCommand.Execute(null);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, activeJob: "Backup", progress: 0.4);
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue();
        vm.BusyText.Should().StartWith("Backup");

        time.Advance(TimeSpan.FromMinutes(31));
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue("the job still runs");
        vm.BusyText.Should().Contain("do not start it again");

        time.Advance(TimeSpan.FromHours(3));
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue("no clock ends the wait while the status says the job runs");
        vm.SyncNowCommand.CanExecute(null).Should().BeFalse("and no second job can be asked for meanwhile");

        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastBackupAt: time.GetUtcNow());   // the job is over: its mark moved
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse();
    }

    // ---- BMB-311 fix2 (R2): the model of the phones: a job is open until the state says otherwise ----

    [Fact]
    public async Task AJobAskedForThatHasNotShownUpAfterThirtyOneSeconds_KeepsThePopUpAndTheLock_AndEndsOnlyAsUnreportedAfterTheGrace()
    {
        var time = new ManualTimeProvider();
        var app = new FakeApp();
        var work = new FakeWorkRequests();
        var vm = new MainViewModel(app, work, new FakeAutostart(), _ => [], time: time);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);
        await vm.RefreshAsync();
        vm.SyncNowCommand.Execute(null);

        time.Advance(TimeSpan.FromSeconds(31));   // the 30-second limit of the old model: the system may simply not have started it yet
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue("a queued job is still on its way");
        vm.SyncNowCommand.CanExecute(null).Should().BeFalse("and no second job can be asked for");
        vm.BackupNowCommand.CanExecute(null).Should().BeFalse();

        app.Status = FakeApp.StatusOf(paired: true, loaded: true, activeJob: "Sync", progress: 0.1);   // it starts after 31 seconds
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue();
        vm.BusyText.Should().StartWith("Sync");

        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastSyncAt: time.GetUtcNow());
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse();

        // A job that is asked for and never starts leaves no mark: after the grace it is said not to have been reported - not to have finished.
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastSyncAt: time.GetUtcNow());
        await vm.RefreshAsync();
        vm.BackupNowCommand.Execute(null);
        time.Advance(BlindJobWait.QueuedGrace + TimeSpan.FromSeconds(1));
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse();
        vm.Notice.Should().Be(BlindJobWait.UnreportedText(BlindJobWait.Backup));
    }

    [Fact]
    public async Task AScheduledJobThatRunsBeforeTheClick_HoldsTheCommands_AndOneThatStartsAfterItIsNotTakenForTheUsersJob()
    {
        var time = new ManualTimeProvider();
        var app = new FakeApp();
        var vm = new MainViewModel(app, new FakeWorkRequests(), new FakeAutostart(), _ => [], time: time);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, activeJob: "Backup", progress: 0.3);   // a scheduled backup is running
        await vm.RefreshAsync();

        vm.SyncNowCommand.CanExecute(null).Should().BeFalse("a job runs: the same rule as the phones' buttons");
        vm.BackupNowCommand.CanExecute(null).Should().BeFalse();
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastBackupAt: time.GetUtcNow());
        await vm.RefreshAsync();
        vm.SyncNowCommand.CanExecute(null).Should().BeTrue("the scheduled job is over");

        // The user asks for a sync; before it starts the schedule starts a backup: the pop-up is still waiting for the SYNC.
        vm.SyncNowCommand.Execute(null);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, activeJob: "Backup", progress: 0.1, lastBackupAt: time.GetUtcNow());
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue("the backup that runs is not the sync that was asked for");
        vm.BusyText.Should().Contain("Backup is running first");
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastBackupAt: time.GetUtcNow().AddSeconds(5));   // the backup ends; the sync has not started
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue("the end of the backup is not the end of the sync");

        app.Status = FakeApp.StatusOf(paired: true, loaded: true, activeJob: "Sync", progress: 0.5, lastBackupAt: time.GetUtcNow().AddSeconds(5));
        await vm.RefreshAsync();
        vm.BusyText.Should().StartWith("Sync");
        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastSyncAt: time.GetUtcNow().AddSeconds(9), lastBackupAt: time.GetUtcNow().AddSeconds(5));
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task AJobThatFails_EndsThePopUpWithItsOwnMessage()
    {
        var time = new ManualTimeProvider();
        var app = new FakeApp();
        var vm = new MainViewModel(app, new FakeWorkRequests(), new FakeAutostart(), _ => [], time: time);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);
        await vm.RefreshAsync();
        vm.BackupNowCommand.Execute(null);
        time.Advance(TimeSpan.FromSeconds(5));

        app.Status = FakeApp.StatusOf(paired: true, loaded: true, lastFailure: new BlindAppFailure("Backup", "The repository is not writable.", time.GetUtcNow(), 1));
        await vm.RefreshAsync();

        vm.IsBusy.Should().BeFalse();
        vm.Notice.Should().Be("The repository is not writable.");
    }

    [Fact]
    public async Task Wipe_ShowsThePopUpUntilTheWipeEnds_AndOnAFailureItGoesAway()
    {
        var gate = new TaskCompletionSource();
        var (vm, app, _) = await PairedAsync(a => a.WipeBody = () => gate.Task);
        vm.OpenWipeCommand.Execute(null);
        vm.WipeTyped = "Test PC";

        vm.ConfirmWipeCommand.Execute(null);

        await WaitFor(() => app.WipeCalls == 1);
        vm.IsBusy.Should().BeTrue();
        vm.BusyTitle.Should().Contain("wiping");
        vm.ConfirmWipeCommand.CanExecute(null).Should().BeFalse();
        gate.SetException(new IOException("in use"));
        await WaitFor(() => !vm.IsBusy);
        vm.Notice.Should().Contain("in use");
    }

    [Fact]
    public async Task SaveTo_ShowsThePopUpWhileTheFileIsCopied()
    {
        var gate = new TaskCompletionSource<long>();
        var app = new FakeApp { ExportBody = _ => gate.Task };
        var vm = new MainViewModel(app, new FakeWorkRequests(), new FakeAutostart(), _ => []);
        app.Status = FakeApp.StatusOf(backups: [new BlindAppBackup("a.bmbbackup", 10)]);
        await vm.RefreshAsync();

        vm.Backups[0].SaveCommand.Execute(null);

        await WaitFor(() => vm.IsBusy);
        vm.BusyTitle.Should().Be("Saving the backup");
        vm.Backups[0].SaveCommand.CanExecute(null).Should().BeFalse();
        gate.SetResult(4096);
        await WaitFor(() => !vm.IsBusy);
        vm.Notice.Should().StartWith("Saved a.bmbbackup");
        vm.Backups[0].SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Connect_WithTheRightCode_KeepsThePopUpUpUntilTheFirstLoadIsOver()
    {
        var app = new FakeApp { AcceptError = null, PairingCodeValue = "code" };
        var work = new FakeWorkRequests();
        var vm = new MainViewModel(app, work, new FakeAutostart(), _ => []);
        app.Status = FakeApp.StatusOf(paired: false, loaded: false, awaiting: true);
        await vm.RefreshAsync();
        vm.CallCodeInput = "bmb-blind-call:?ok";

        vm.ConnectCommand.Execute(null);

        work.Heavies.Should().Be(1);
        vm.IsBusy.Should().BeTrue();
        vm.BusyTitle.Should().Be("Connecting");
        work.Report("The first load is done.");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_WithAWrongCode_PutsUpNoPopUp()
    {
        var (vm, app, _) = await PairedAsync(a => { a.AcceptError = "wrong code"; a.PairingCodeValue = "code"; });
        app.Status = FakeApp.StatusOf(paired: false, loaded: false, awaiting: true);
        await vm.RefreshAsync();
        vm.CallCodeInput = "junk";

        vm.ConnectCommand.Execute(null);

        vm.IsBusy.Should().BeFalse();
        vm.ConnectError.Should().Be("wrong code");
    }
}
