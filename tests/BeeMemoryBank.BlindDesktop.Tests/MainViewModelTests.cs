using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The window of a device that holds several accounts: the list on the left, the selected account on the right, the Add page, the repair states, the device limits, autostart and
/// quit. The view model is a thin adapter: these tests check that what it shows IS what the view records say (<see cref="BlindAccountsView"/>, <see cref="BlindAccountCard"/>), and that
/// every action reaches <see cref="IBlindAccountsController"/>. Today's panel of one account is <see cref="AccountPanelViewModelTests"/>.
/// </summary>
public sealed class MainViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private sealed class Rig
    {
        public FakeAccounts Accounts { get; } = new();
        public FakeAutostart Autostart { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public Dictionary<string, FakeWorkRequests> Work { get; } = [];
        public List<(string What, Exception Error)> Problems { get; } = [];
        public MainViewModel Vm { get; }

        public Rig(Action<Action>? post = null, string statusArea = "tray", BlindInvitationWatch? watch = null)
        {
            Vm = new MainViewModel(Accounts, id => Work.TryGetValue(id, out var w) ? w : Work[id] = new FakeWorkRequests(), Autostart, _ => [0x89, 0x50, 0x4E, 0x47], post,
                (what, ex) => Problems.Add((what, ex)), statusArea, time: Time, zone: Zone, watch: watch);
        }

        public BlindAccountsView View() => BlindAccountsView.Build(Accounts.GetSnapshot(), Zone, Time.GetUtcNow());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    // ---- what it shows -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeviceWithNoAccount_OpensOnTheAddPage_WithNothingSelected()
    {
        var rig = new Rig();

        await rig.Vm.RefreshAsync();

        rig.Vm.Rows.Should().BeEmpty();
        rig.Vm.Detail.Should().BeNull();
        rig.Vm.ShowAdd.Should().BeTrue("there is nothing else to show");
        rig.Vm.ShowDetail.Should().BeFalse();
        rig.Vm.CanCloseAdd.Should().BeFalse("there is no account to go back to");
        rig.Vm.DeviceLine.Should().Be(rig.View().DeviceLine).And.Be("No accounts");
        rig.Vm.CanAdd.Should().BeTrue();
    }

    [Fact]
    public async Task TheList_IsTheCardsOfTheViewRecord_WordForWord()
    {
        var rig = new Rig();
        var (berlin, _) = rig.Accounts.AddAccount("Berlin office", status: FakeApp.StatusOf(lastSyncAt: rig.Time.GetUtcNow() - TimeSpan.FromMinutes(4)));
        var (paris, _) = rig.Accounts.AddAccount("Paris");
        rig.Accounts.Pauses[paris.Id] = new BlindPause(BlindPauseReasons.Traffic, Limit: 200L << 20);
        rig.Accounts.Usage[berlin.Id] = new BlindAccountUsageView(1_288_490_189, 312, 30L << 20, 8L << 20, 40L << 20);

        await rig.Vm.RefreshAsync();

        var expected = rig.View();
        rig.Vm.Rows.Select(r => (r.Id, r.Name, r.SourceHost, r.State, r.Kind, r.Stored))
            .Should().Equal(expected.Cards.Select(c => (c.Id, c.Name, c.SourceHost, c.State, c.Kind, c.Stored)));
        rig.Vm.Rows[0].State.Should().Be("Synced 4 min ago");
        rig.Vm.Rows[1].State.Should().StartWith("Paused: daily traffic limit reached (200 MB)");
        rig.Vm.DeviceLine.Should().Be(expected.DeviceLine).And.StartWith("2 accounts");
    }

    [Fact]
    public async Task TheFirstAccountIsSelected_AndItsDetailShowsItsCardAndItsPanel()
    {
        var rig = new Rig();
        var (first, _) = rig.Accounts.AddAccount("Berlin office");
        rig.Accounts.AddAccount("Paris");

        await rig.Vm.RefreshAsync();

        rig.Vm.Detail!.Id.Should().Be(first.Id);
        rig.Vm.SelectedRow!.Id.Should().Be(first.Id);
        rig.Vm.ShowDetail.Should().BeTrue();
        rig.Vm.ShowAdd.Should().BeFalse();
        rig.Vm.Detail.StateLine.Should().Be(rig.View().Cards[0].State);
        rig.Vm.Detail.HasPanel.Should().BeTrue("an active account that is up shows today's panel");
    }

    [Fact]
    public async Task SelectingAnotherRow_ShowsThatAccount()
    {
        var rig = new Rig();
        rig.Accounts.AddAccount("Berlin office");
        var (second, _) = rig.Accounts.AddAccount("Paris");
        await rig.Vm.RefreshAsync();

        rig.Vm.SelectedRow = rig.Vm.Rows[1];

        await WaitFor(() => rig.Vm.Detail?.Id == second.Id);
        rig.Vm.Detail!.Name.Should().Be("Paris");
        rig.Vm.SelectedRow!.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task TheSelection_SurvivesARefresh_AndMovesOnWhenItsAccountIsGone()
    {
        var rig = new Rig();
        var (first, _) = rig.Accounts.AddAccount("Berlin office");
        var (second, _) = rig.Accounts.AddAccount("Paris");
        await rig.Vm.RefreshAsync();
        rig.Vm.SelectedRow = rig.Vm.Rows[1];
        await WaitFor(() => rig.Vm.Detail?.Id == second.Id);
        var row = rig.Vm.Rows[1];

        await rig.Vm.RefreshAsync();
        rig.Vm.Rows[1].Should().BeSameAs(row, "the same row object for the same account: a selection must survive a refresh");
        rig.Vm.Detail!.Id.Should().Be(second.Id);

        rig.Accounts.Entries.RemoveAll(e => e.Id == second.Id);
        await rig.Vm.RefreshAsync();
        rig.Vm.Detail!.Id.Should().Be(first.Id, "the account that was shown is gone: the first one is shown");
    }

    [Fact]
    public async Task AnAccountThatIsNotActive_ShowsItsStateLine_AndNoPanel()
    {
        var rig = new Rig();
        var (entry, _) = rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.AwaitingConfirmation);
        rig.Accounts.Entries[0] = entry with { MatchCode = "K7F-2QX", InviteId = "i1" };

        await rig.Vm.RefreshAsync();

        rig.Vm.Detail!.HasPanel.Should().BeFalse("an invited account has nothing to pair yet");
        rig.Vm.Detail.AwaitingOwner.Should().BeTrue();
        rig.Vm.Detail.StateLine.Should().Be("Waiting for Alex to confirm. Read this code out to them: K7F-2QX");
        rig.Vm.Rows[0].State.Should().Be(rig.Vm.Detail.StateLine);
    }

    // ---- the add page, the cap, a damaged list ------------------------------------------------------------------------------------

    [Fact]
    public async Task AddAccount_OpensTheAddPage_AndBackReturnsToTheAccount()
    {
        var rig = new Rig();
        rig.Accounts.AddAccount("Berlin office");
        await rig.Vm.RefreshAsync();
        rig.Vm.ShowAdd.Should().BeFalse();

        rig.Vm.AddAccountCommand.Execute(null);
        rig.Vm.ShowAdd.Should().BeTrue();
        rig.Vm.ShowDetail.Should().BeFalse();
        rig.Vm.CanCloseAdd.Should().BeTrue();

        rig.Vm.CloseAddCommand.Execute(null);
        rig.Vm.ShowAdd.Should().BeFalse();
        rig.Vm.ShowDetail.Should().BeTrue();
    }

    [Fact]
    public async Task AtTheCap_AddIsOff_AndTheHintIsTheViewRecordsSentence()
    {
        var rig = new Rig();
        rig.Accounts.Max = 1;
        rig.Accounts.AddAccount("Berlin office");

        await rig.Vm.RefreshAsync();

        rig.Vm.CanAdd.Should().BeFalse();
        rig.Vm.AddAccountCommand.CanExecute(null).Should().BeFalse();
        rig.Vm.AddHint.Should().Be(rig.View().AddHint).And.Contain("at most 1 accounts");
        rig.Vm.HasAddHint.Should().BeTrue();
    }

    [Fact]
    public async Task ADamagedList_ShowsOnlyItsSentence_NoCardsAndNoPage()
    {
        var rig = new Rig();
        rig.Accounts.RegistryProblem = "accounts.json is damaged and cannot be read. Nothing was started or changed, so that nothing is lost.";
        rig.Accounts.AddAccount("Berlin office");

        await rig.Vm.RefreshAsync();

        rig.Vm.HasRegistryProblem.Should().BeTrue();
        rig.Vm.RegistryProblem.Should().Be(rig.Accounts.RegistryProblem);
        rig.Vm.ShowAdd.Should().BeFalse();
        rig.Vm.ShowDetail.Should().BeFalse();
        rig.Vm.CanAdd.Should().BeFalse();
        rig.Vm.AddAccountCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- repair states -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachRepairState_HasItsOwnExplicitAction_NothingIsDoneOnItsOwn()
    {
        var rig = new Rig();
        rig.Accounts.Repairs.Add(new BlindRepairIssue(BlindRepairKinds.LegacyRootNotListed, null, "A 2.6 copy is in the root and the list omits it."));
        rig.Accounts.Repairs.Add(new BlindRepairIssue(BlindRepairKinds.FolderNotListed, "aaaa", "A folder is not listed."));
        rig.Accounts.Repairs.Add(new BlindRepairIssue(BlindRepairKinds.FolderMissing, "bbbb", "A listed account lost its folder."));
        rig.Accounts.Repairs.Add(new BlindRepairIssue(BlindRepairKinds.UnsafePath, "cccc", "A link is where a folder belongs."));

        await rig.Vm.RefreshAsync();

        rig.Vm.HasRepairs.Should().BeTrue();
        rig.Vm.Repairs.Select(r => r.Description).Should().Equal(rig.View().Repairs!.Select(r => r.Description));
        rig.Accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("Adopt") || c.StartsWith("Forget"), "a repair is never taken by the app on its own");
        rig.Vm.Repairs.Select(r => r.ActionText).Should().Equal("Add it to the list", "Add it to the list", "Forget it", "");
        rig.Vm.Repairs[3].Command.CanExecute(null).Should().BeFalse("a link needs a person");

        rig.Vm.Repairs[0].Command.Execute(null);
        rig.Vm.Repairs[1].Command.Execute(null);
        rig.Vm.Repairs[2].Command.Execute(null);
        await WaitFor(() => rig.Accounts.CallsSoFar.Contains("ForgetMissing:bbbb"));
        rig.Accounts.CallsSoFar.Should().Contain("AdoptLegacyRoot").And.Contain("AdoptFolder:aaaa");
    }

    [Fact]
    public async Task ARefusedRepair_ShowsTheControllersSentence()
    {
        var rig = new Rig();
        rig.Accounts.Repairs.Add(new BlindRepairIssue(BlindRepairKinds.FolderMissing, "bbbb", "A listed account lost its folder."));
        rig.Accounts.Refusals["ForgetMissing"] = "The folder is back; nothing was forgotten.";
        await rig.Vm.RefreshAsync();

        rig.Vm.Repairs[0].Command.Execute(null);

        await WaitFor(() => rig.Vm.Notice == "The folder is back; nothing was forgotten.");
    }

    [Fact]
    public async Task ADeviceUsageProblem_OffersStartNewCounts_AndTheRepairClearsIt()
    {
        var rig = new Rig();
        rig.Accounts.DeviceUsageProblem = "The device's usage counts cannot be read.";
        await rig.Vm.RefreshAsync();
        rig.Vm.HasDeviceUsageProblem.Should().BeTrue();
        rig.Vm.DeviceUsageProblem.Should().Be(rig.View().DeviceUsageProblem);

        rig.Vm.RepairDeviceUsageCommand.Execute(null);

        rig.Accounts.CallsSoFar.Should().Contain("StartNewDeviceUsageLedger");
        await WaitFor(() => !rig.Vm.HasDeviceUsageProblem);
    }

    // ---- the device's own limits --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDeviceLimitBoxes_AreFilledFromTheDevice_AndSavedThroughTheForm()
    {
        var rig = new Rig();
        rig.Accounts.Device = new BlindDeviceLimits(5L << 30, 500L << 20);
        await rig.Vm.RefreshAsync();
        rig.Vm.DeviceStoredText.Should().Be("5 GB");
        rig.Vm.DeviceTrafficText.Should().Be("500 MB");

        rig.Vm.DeviceStoredText = "8 GB";
        rig.Vm.DeviceTrafficText = "";
        rig.Vm.ApplyDeviceLimitsCommand.Execute(null);

        rig.Accounts.Device.Should().Be(new BlindDeviceLimits(8L << 30, null));
        rig.Vm.HasDeviceLimitsProblems.Should().BeFalse();
        rig.Vm.Notice.Should().Be("Device limits saved.");
    }

    [Fact]
    public async Task ADeviceLimitTheFormRefuses_IsShownAsTheFormsSentence_AndNothingIsSaved()
    {
        var rig = new Rig();
        await rig.Vm.RefreshAsync();

        rig.Vm.DeviceTrafficText = "1 MB";
        rig.Vm.ApplyDeviceLimitsCommand.Execute(null);

        rig.Vm.HasDeviceLimitsProblems.Should().BeTrue();
        rig.Vm.DeviceLimitsProblems.Should().Be(new BlindDeviceLimitsForm("", "1 MB").Validate().Problems.Single());
        rig.Accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("SetDeviceLimits"));
    }

    [Fact]
    public async Task WhatThePersonIsTypingInTheDeviceBoxes_IsNotTakenAwayByARefresh()
    {
        var rig = new Rig();
        rig.Accounts.Device = new BlindDeviceLimits(5L << 30, null);
        await rig.Vm.RefreshAsync();

        rig.Vm.DeviceStoredText = "7 G";
        await rig.Vm.RefreshAsync();

        rig.Vm.DeviceStoredText.Should().Be("7 G");
    }

    [Fact]
    public async Task ARefusalOfTheController_ForDeviceLimits_IsShown()
    {
        var rig = new Rig();
        rig.Accounts.Refusals["SetDeviceLimits"] = "The list of accounts is damaged.";
        await rig.Vm.RefreshAsync();

        rig.Vm.DeviceStoredText = "5 GB";
        rig.Vm.ApplyDeviceLimitsCommand.Execute(null);

        rig.Vm.DeviceLimitsProblems.Should().Be("The list of accounts is damaged.");
    }

    // ---- autostart, quit, words ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Autostart_ShowsTheOsState_WritesItOnToggle_AndRevertsWithANoticeOnFailure()
    {
        var rig = new Rig();
        rig.Autostart.Enabled = true;
        await rig.Vm.RefreshAsync();
        rig.Vm.AutostartOn.Should().BeTrue();
        rig.Vm.AutostartKnown.Should().BeTrue();

        rig.Vm.AutostartOn = false;
        rig.Autostart.Enabled.Should().BeFalse();

        rig.Autostart.Failure = new UnauthorizedAccessException("denied");
        rig.Vm.AutostartOn = true;
        rig.Vm.AutostartOn.Should().BeFalse("the box goes back to what the system has");
        rig.Vm.Notice.Should().Contain("denied");

        rig.Autostart.Failure = null;
        rig.Autostart.Enabled = null;
        await rig.Vm.RefreshAsync();
        rig.Vm.AutostartKnown.Should().BeFalse("a setting that cannot be read is shown as unknown, not as off");
    }

    [Fact]
    public void Quit_IsAskedOfTheShell_NotDoneByTheViewModel()
    {
        var rig = new Rig();
        var asked = 0;
        rig.Vm.QuitRequested += () => asked++;

        rig.Vm.QuitCommand.Execute(null);

        asked.Should().Be(1);
    }

    [Theory]
    [InlineData("tray", "Start in the tray when I sign in", "Closing this window keeps the app running in the tray.")]
    [InlineData("menu bar", "Start in the menu bar when I sign in", "Closing this window keeps the app running in the menu bar.")]
    public void TheWordsFollowThePlatformsNameForItsStatusArea(string area, string autostart, string closing)
    {
        var rig = new Rig(statusArea: area);

        rig.Vm.StatusArea.Should().Be(area);
        rig.Vm.AutostartText.Should().Be(autostart);
        rig.Vm.CloseHintText.Should().Be(closing);
    }

    // ---- events -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangedEvents_AreCoalescedIntoOneRefresh_OnTheUiThread()
    {
        var posted = new List<Action>();
        var rig = new Rig(posted.Add);
        rig.Accounts.AddAccount("Berlin office");

        rig.Accounts.RaiseChanged();
        rig.Accounts.RaiseChanged();
        rig.Accounts.RaiseChanged();

        posted.Should().HaveCount(1, "three events before the UI thread got to it are one refresh");
        posted[0]();
        await WaitFor(() => rig.Vm.Rows.Count == 1);

        rig.Accounts.RaiseChanged();
        posted.Should().HaveCount(2);
        rig.Vm.Dispose();
        rig.Accounts.RaiseChanged();
        posted.Should().HaveCount(2, "a disposed view model does not listen");
    }

    [Fact]
    public async Task AFailingRead_IsAnotice_NotThrown_AndTheNextReadShowsTheNextState()
    {
        var rig = new Rig();
        var accounts = new ThrowingOnceAccounts();
        var vm = new MainViewModel(accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], problem: (what, _) => rig.Problems.Add((what, null!)), zone: Zone);

        var first = () => vm.RefreshAsync();
        await first.Should().NotThrowAsync();
        vm.Notice.Should().Contain("Could not read the accounts").And.NotContain("db is gone", "a file failure is told by its kind, not by its message");
        rig.Problems.Select(p => p.What).Should().Equal("Reading the accounts failed");

        accounts.AddAccount("Back again");
        await vm.RefreshAsync();
        vm.Rows.Should().ContainSingle().Which.Name.Should().Be("Back again");
    }

    private sealed class ThrowingOnceAccounts : IBlindAccountsController
    {
        private readonly FakeAccounts _inner = new();
        private bool _thrown;
        public event Action? Changed { add { } remove { } }
        public int MaxAccounts => 8;
        public void AddAccount(string name) => _inner.AddAccount(name);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public BlindAccountsSnapshot GetSnapshot()
        {
            if (_thrown) return _inner.GetSnapshot();
            _thrown = true;
            throw new IOException("db is gone");
        }
        public IBlindAppController For(string accountId) => _inner.For(accountId);
        public IServiceProvider ServicesFor(string accountId) => _inner.ServicesFor(accountId);
        public BlindPause? PauseOf(string accountId) => null;
        public Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default) => _inner.AddAsync(name, ct);
        public BlindInvitationPreview PreviewInvitation(string? text) => _inner.PreviewInvitation(text);
        public Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default) => _inner.AddFromInvitationAsync(text, settings, ct);
        public Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default) => Task.FromResult(0);
        public string? Reconnect(string accountId) => null;
        public string? AcceptCallCode(string text) => null;
        public string? Rename(string accountId, string name) => null;
        public string? SetLimits(string accountId, BlindAccountLimits limits) => null;
        public string? SetSwitches(string accountId, bool unmeteredOnly, bool pauseOnPowerSaving) => null;
        public string? SetPaused(string accountId, bool paused) => null;
        public string? SetDeviceLimits(BlindDeviceLimits limits) => null;
        public Task RemoveAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
        public string? StartNewUsageLedger(string accountId) => null;
        public string? StartNewDeviceUsageLedger() => null;
        public Task<string?> AdoptLegacyRootAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> AdoptFolderAsync(string accountId, string? name = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ForgetMissingAccountAsync(string accountId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task ThePairingCodeCopyOfTheSelectedPanel_ReachesTheWindow()
    {
        var rig = new Rig();
        var (entry, app) = rig.Accounts.AddAccount("Berlin office", status: FakeApp.StatusOf(paired: false, loaded: false, awaiting: true));
        app.PairingCodeValue = "bmb-blind-phone:?n=1";
        await rig.Vm.RefreshAsync();
        var copied = new List<string>();
        rig.Vm.CopyRequested += copied.Add;

        rig.Vm.Detail!.Panel!.CopyPairingCodeCommand.Execute(null);

        copied.Should().Equal("bmb-blind-phone:?n=1");
        entry.Id.Should().NotBeNull();
    }

    // ---- the invitation watch -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShowingTheWindow_StartsTheWatch_AndHidingItStopsIt()
    {
        var rig = new Rig();
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.AwaitingConfirmation);

        vm.OnWindowShown();
        await WaitFor(() => rig.Accounts.Polls > 0);
        watch.IsRunning.Should().BeTrue("an account waits for its owner and the window is open");

        vm.OnWindowHidden();
        await WaitFor(() => !watch.IsRunning);
        vm.Dispose();
    }

    [Fact]
    public async Task AReadThatFinishesAfterTheWindowWasHidden_DoesNotStartTheWatchAgain()
    {
        var rig = new Rig();
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.Pending);
        var onItsWay = new QueuedContext();
        var read = StartUnder(onItsWay, vm.RefreshAsync);
        await WaitFor(() => onItsWay.Pending == 1);

        vm.OnWindowHidden();
        onItsWay.RunAll();
        await read;

        watch.IsRunning.Should().BeFalse("the window is hidden: the device loop asks, the screen does not");
        rig.Accounts.Polls.Should().Be(0);
        vm.OnWindowShown();
        watch.IsRunning.Should().BeTrue("showing it again starts the watch");
        vm.Dispose();
        await watch.StopAsync();
    }

    [Fact]
    public async Task AppStartedInTheTray_WithAnAccountThatWaits_DoesNotStartTheWatch_UntilTheWindowIsShown()
    {
        var rig = new Rig();
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.Pending);

        // --minimized: the view model reads the accounts at start, and the window has never been shown.
        await vm.RefreshAsync();
        await vm.RefreshAsync();

        watch.IsRunning.Should().BeFalse("the window is not open: the device loop asks, the screen does not");
        rig.Accounts.Polls.Should().Be(0, "no ask of the hub was made on behalf of a window nobody sees");

        vm.OnWindowShown();
        await WaitFor(() => rig.Accounts.Polls > 0);
        watch.IsRunning.Should().BeTrue("the window is shown: the watch starts");
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        rig.Accounts.Polls.Should().Be(1, "it was started once: the reads that follow find it running and start no second one");
        vm.Dispose();
        await watch.StopAsync();
    }

    [Fact]
    public async Task AnAddThatCompletesAfterTheWindowWasHidden_DoesNotStartTheWatch()
    {
        var rig = new Rig();
        var time = new ManualTimeProvider();
        var invitation = "bmb-invite:?v=1&a=https%3A%2F%2Fmem.example.org";
        rig.Accounts.Previews[invitation] = new BlindInvitationPreview("Alex", "mem.example.org", BlindAccountDefaults.ForInvited(false), time.GetUtcNow().AddMinutes(28));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Accounts.OnAddFromInvitation = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            var (entry, _) = rig.Accounts.AddAccount("Invited", phase: BlindAccountPhases.AwaitingConfirmation);
            return new BlindAddResult(entry.Id);
        };
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        vm.OnWindowShown();
        await vm.RefreshAsync();
        await WaitFor(() => !watch.IsRunning);   // nothing waits yet: the watch the window started ends by itself
        var before = rig.Accounts.Polls;
        vm.AddAccountCommand.Execute(null);
        vm.Add.InviteText = invitation;
        vm.Add.AddInvitationCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        vm.OnWindowHidden();
        release.SetResult();
        await WaitFor(() => rig.Accounts.Entries.Count == 1 && !vm.Add.IsBusy);
        await vm.RefreshAsync();

        watch.IsRunning.Should().BeFalse("the account was made after the window was hidden: the screen does not ask for it");
        rig.Accounts.Polls.Should().Be(before);
        vm.OnWindowShown();
        await WaitFor(() => rig.Accounts.Polls > before);
        watch.IsRunning.Should().BeTrue("shown again, the window watches the account that waits");
        vm.Dispose();
        await watch.StopAsync();
    }

    [Fact]
    public async Task HidingAndShowingTheWindowAtOnce_WhileAnAskIsHeld_NeverMakesTwoAsksAtTheSameTime()
    {
        var rig = new Rig();
        var concurrent = 0;
        var most = 0;
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.Pending);
        // The first ask is held and takes no notice of the cancellation: the stop that hiding the window starts is in flight until the test lets it go.
        rig.Accounts.OnPoll = async (number, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref concurrent));
            entered.Release();
            try { if (number == 1) await release.Task; }
            finally { Interlocked.Decrement(ref concurrent); }
            return 1;
        };
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        vm.OnWindowShown();
        (await entered.WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue("the shown window asked");

        vm.OnWindowHidden();
        vm.OnWindowShown();
        await Task.Delay(200);

        rig.Accounts.Polls.Should().Be(1, "the new watch waits for the old ask: no second ask beside it");
        most.Should().Be(1);
        release.SetResult();
        (await entered.WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue("the window is shown: the watch asks again once the old ask has ended");
        most.Should().Be(1, "at no time were two asks in flight");
        watch.IsRunning.Should().BeTrue();
        vm.Dispose();
        await watch.StopAsync();
    }

    [Fact]
    public async Task AnAccountThatWaitsForItsOwner_StartsTheWatchOnTheNextRead_EvenIfTheWindowWasAlreadyOpen()
    {
        var rig = new Rig();
        var watch = new BlindInvitationWatch(rig.Accounts, rig.Time);
        var vm = new MainViewModel(rig.Accounts, _ => new FakeWorkRequests(), rig.Autostart, _ => [], zone: Zone, time: rig.Time, watch: watch);
        vm.OnWindowShown();
        await vm.RefreshAsync();
        await WaitFor(() => !watch.IsRunning);   // nothing waits: the watch the window started ends by itself
        var before = rig.Accounts.Polls;

        rig.Accounts.AddAccount("Alex", phase: BlindAccountPhases.Pending);
        await vm.RefreshAsync();

        await WaitFor(() => rig.Accounts.Polls > before);
        vm.Dispose();
        await watch.StopAsync();
    }

    // ---- the pop-up ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePopUp_FollowsTheSelectedAccountsPanel_AndTheAddPage()
    {
        var rig = new Rig();
        var (entry, app) = rig.Accounts.AddAccount("Berlin office");
        await rig.Vm.RefreshAsync();
        rig.Vm.IsBusy.Should().BeFalse();

        rig.Vm.Detail!.Panel!.SyncNowCommand.Execute(null);

        rig.Vm.IsBusy.Should().BeTrue("the panel put up its pop-up for the job it asked for");
        rig.Vm.BusyTitle.Should().Be("Syncing");
        rig.Work[entry.Id].Syncs.Should().Be(1, "the request went to the work requests of THIS account");
        rig.Work[entry.Id].Report("Synced.");
        rig.Vm.IsBusy.Should().BeFalse();
        app.Should().NotBeNull();
    }

    // ---- two reads of the accounts that finish in the wrong order ------------------------------------------------------------------------

    /// <summary>The UI thread, by hand: what an <c>await</c> started under it continues with is queued, and the test runs it when it says.</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Pending
        {
            get { lock (_queue) return _queue.Count; }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue) _queue.Enqueue((d, state));
        }

        public void RunAll()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    next = _queue.Dequeue();
                }
                next.Callback(next.State);
            }
        }
    }

    private static Task StartUnder(SynchronizationContext context, Func<Task> start)
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }
    }

    [Fact]
    public async Task ARefreshThatReadTheAccountsEarlier_IsNotShownOverOneThatReadThemLater()
    {
        var rig = new Rig();
        var (gone, _) = rig.Accounts.AddAccount("Berlin office");
        rig.Accounts.AddAccount("Paris");
        await rig.Vm.RefreshAsync();
        rig.Vm.Rows.Should().HaveCount(2);

        // The first read sees both accounts; its result waits to be shown (the UI thread is busy).
        var early = new QueuedContext();
        var first = StartUnder(early, rig.Vm.RefreshAsync);
        await WaitFor(() => early.Pending == 1);
        // The account is removed, and a second read - made later - is shown first.
        await rig.Accounts.RemoveAsync(gone.Id);
        var late = new QueuedContext();
        var second = StartUnder(late, rig.Vm.RefreshAsync);
        await WaitFor(() => late.Pending == 1);
        late.RunAll();
        await second;
        rig.Vm.Rows.Select(r => r.Id).Should().NotContain(gone.Id);

        // The first one is shown now: it must not bring the removed account back.
        early.RunAll();
        await first;

        rig.Vm.Rows.Select(r => r.Id).Should().NotContain(gone.Id, "an older read is not shown over a newer one");
        rig.Vm.Rows.Should().HaveCount(1);
    }
}
