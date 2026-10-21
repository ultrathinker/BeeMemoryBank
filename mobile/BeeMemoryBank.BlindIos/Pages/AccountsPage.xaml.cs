using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Busy;
using UIKit;

namespace BeeMemoryBank.BlindIos.Pages;

/// <summary>
/// The start page of the iPhone app: one calm card per account (the shared <see cref="BlindAccountsView"/> and <see cref="BlindAccountCard"/>: one state line, rounded numbers, no charts),
/// the line about the whole phone, "Add account", the limits of the phone at the bottom, and - only when the list of accounts and the disk disagree - the repair states with their explicit
/// actions. The page holds no blind logic and builds no sentence of its own: every line comes from AppCore and is only assigned to a control. While it is open it asks about an invitation
/// that waits for its owner every 15 seconds (<see cref="BlindInvitationWatch"/>); the device scheduler does it at its own passes otherwise. Every callback is guarded: a failure becomes a
/// sentence on the page, never an unhandled exception on the UI thread.
/// </summary>
public partial class AccountsPage : ContentPage
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly IosBlindRuntime _runtime;
    private readonly IosSilenceNotifier _silence;
    private IDispatcherTimer? _timer;
    private BlindInvitationWatch? _watch;
    private string? _cardsSignature;
    private string? _repairsSignature;
    private bool _limitsLoaded;
    private bool _refreshPending;
#if DEBUG || BMB_E2E
    private bool _e2eOpened;
#endif

    public AccountsPage(IosBlindRuntime runtime, IosSilenceNotifier silence)
    {
        InitializeComponent();
        _runtime = runtime;
        _silence = silence;
        DeviceLimitsHintLabel.Text = BlindDeviceLimitsForm.Explanation;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _runtime.Accounts.Changed += OnAccountsChanged;
            _runtime.Scheduler.UserJobReported += OnUserJobReported;
            // Idempotent: the app's life cycle has asked already, and a link may have opened the app before anyone did.
            await _runtime.StartAsync();
            Refresh();

            _watch ??= _runtime.CreateInvitationWatch();
            _watch.Start();
            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = RefreshEvery;
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();

            // A link that opened the app before this page existed: an invitation goes to the Add page, the computer's answer to the account that waits for it.
            if (App.TakePendingInvitation() is { } invitation)
                await Navigation.PushAsync(new AddAccountPage(_runtime, _silence, invitation));
            else if (App.HasPendingCallCode && FirstAccountWaitingForAnswer() is { } waiting)
                await Navigation.PushAsync(new BlindHomePage(_runtime, _silence, waiting));
#if DEBUG || BMB_E2E
            await OpenFirstAccountForE2eAsync();
#endif
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    protected override void OnDisappearing()
    {
        _runtime.Accounts.Changed -= OnAccountsChanged;
        _runtime.Scheduler.UserJobReported -= OnUserJobReported;
        _timer?.Stop();
        if (_watch is { } watch) _ = Task.Run(watch.StopAsync);
        base.OnDisappearing();
    }

    private string? FirstAccountWaitingForAnswer() =>
        _runtime.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Status is { AwaitingAnswer: true })?.Entry.Id;

#if DEBUG || BMB_E2E
    /// <summary>
    /// End-to-end checks on a simulator or a development iPhone (tools/ios-e2e) cannot tap "Add account": with BMB_BLIND_E2E_FIRST_ACCOUNT=1 the app opens the page of the account it has, making a
    /// first one the pairing-code way when there is none. Only in a Debug build or one made with -p:BmbE2E=true; a Release build for people does not contain it (IosBoundaryTests).
    /// </summary>
    private async Task OpenFirstAccountForE2eAsync()
    {
        if (_e2eOpened || Environment.GetEnvironmentVariable("BMB_BLIND_E2E_FIRST_ACCOUNT") != "1") return;
        _e2eOpened = true;
        var accounts = _runtime.Accounts;
        var id = accounts.GetSnapshot().Accounts.FirstOrDefault()?.Entry.Id ?? (await accounts.AddAsync("E2E")).AccountId;
        if (id is not null) await Navigation.PushAsync(new BlindHomePage(_runtime, _silence, id));
    }
#endif

    private void OnAccountsChanged()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _refreshPending = false;
            Refresh();
        });
    }

    private void OnUserJobReported(string accountId, string sentence) => MainThread.BeginInvokeOnMainThread(() => Notice(sentence));

    /// <summary>Reads the accounts again and shows them. Never throws: the 5-second timer and every handler call it.</summary>
    private void Refresh()
    {
        try
        {
            var snapshot = _runtime.Accounts.GetSnapshot();
            var view = BlindAccountsView.Build(snapshot, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
            ProblemLabel.IsVisible = view.Problem is not null;
            ProblemLabel.Text = view.Problem ?? "";
            DeviceLineLabel.Text = view.DeviceLine;
            ShowRepairs(view);
            ShowCards(view, snapshot);
            AddButton.IsEnabled = view.CanAdd;
            AddHintLabel.Text = view.AddHint;
            AddHintLabel.IsVisible = view.AddHint.Length > 0;
            EmptyLabel.IsVisible = view.Cards.Count == 0 && view.Problem is null;
            ShowDeviceLimits(snapshot, view);
            ShowBackground(snapshot);
            _silence.Update(snapshot);
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    // ---- the cards ------------------------------------------------------------------------------------------------------------------------

    private void ShowCards(BlindAccountsView view, BlindAccountsSnapshot snapshot)
    {
        // Rebuilt only when something a card says changed: a list that is rebuilt every few seconds jumps under the person's finger.
        var signature = string.Join("\n", view.Cards.Select(c =>
            string.Join("|", c.Id, c.Name, c.SourceHost, c.State, c.Kind, c.Stored, c.Today, c.Average, c.LastSync, c.IsPaused, c.CanSyncNow, c.CanEdit, c.CanRepairUsage,
                snapshot.Accounts.First(a => a.Entry.Id == c.Id).Entry.Phase)));
        if (signature == _cardsSignature) return;
        _cardsSignature = signature;

        CardsHost.Children.Clear();
        foreach (var card in view.Cards)
            CardsHost.Children.Add(BuildCard(card, snapshot.Accounts.First(a => a.Entry.Id == card.Id).Entry.Phase));
    }

    private View BuildCard(BlindAccountCard card, string phase)
    {
        var body = new VerticalStackLayout { Spacing = 4 };
        body.Children.Add(Line(card.Name, 16, bold: true));
        if (card.SourceHost.Length > 0) body.Children.Add(Line(card.SourceHost, 12, colorKey: "SecondaryTextColor"));
        body.Children.Add(Line(card.State, 14, colorKey: ColorOf(card.Kind)));
        if (card.Stored.Length > 0) body.Children.Add(Line("Stored: " + card.Stored, 12));
        if (card.Today.Length > 0) body.Children.Add(Line("Today: " + card.Today, 12));
        if (phase == BlindAccountPhases.Active)
        {
            body.Children.Add(Line(card.Average, 12, colorKey: "SecondaryTextColor"));
            body.Children.Add(Line(card.LastSync, 12, colorKey: "SecondaryTextColor"));
        }

        var buttons = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var id = card.Id;
        if (card.CanEdit) buttons.Children.Add(Small("Open", (_, _) => Open(id), primary: true));
        if (card.CanSyncNow) buttons.Children.Add(Small("Sync now", (_, _) => SyncNow(id)));
        if (phase == BlindAccountPhases.RemovedByOwner) buttons.Children.Add(Small("Try again", (_, _) => Reconnect(id)));
        if (card.CanRepairUsage) buttons.Children.Add(Small("Start new counts", (_, _) => StartNewCounts(id)));
        if (card.CanEdit && phase != BlindAccountPhases.Active) buttons.Children.Add(Small("Remove", (sender, args) => _ = RemoveAsync(id, sender as Button)));
        if (buttons.Children.Count > 0) body.Children.Add(buttons);

        var frame = new Frame
        {
            Style = (Style)Application.Current!.Resources["CardFrame"],
            AutomationId = "BlindAccountCard",
            Content = body,
        };
        if (card.CanEdit)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Open(id);
            frame.GestureRecognizers.Add(tap);
        }
        return frame;
    }

    private static string ColorOf(BlindCardKind kind) => kind switch
    {
        BlindCardKind.Ok => "SuccessColor",
        BlindCardKind.Waiting => "WarningColor",
        BlindCardKind.Problem => "ErrorColor",
        BlindCardKind.Paused or BlindCardKind.Removing => "SecondaryTextColor",
        _ => "TextColor",
    };

    // busy-exempt: opens the account's page, nothing is sent and nothing waits
    private async void Open(string accountId)
    {
        try { await Navigation.PushAsync(new BlindHomePage(_runtime, _silence, accountId)); }
        catch (Exception ex) { ShowProblem(BusyAction.Describe(ex)); }
    }

    private void SyncNow(string accountId)
    {
        try
        {
            _runtime.Scheduler.EnsureScheduled();
            _runtime.Scheduler.RequestSync(accountId);
            Notice("Sync asked for.");
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    private void Reconnect(string accountId)
    {
        var error = _runtime.Accounts.Reconnect(accountId);
        Notice(error ?? "It will try the source again at the next round.");
        Refresh();
    }

    private void StartNewCounts(string accountId)
    {
        var error = _runtime.Accounts.StartNewUsageLedger(accountId);
        Notice(error ?? "New usage counts were started. The damaged file is kept aside.");
        Refresh();
    }

    /// <summary>"Remove": what goes and who is told, in the words of <see cref="BlindRemoveConfirmation"/>; then the removal, which waits for the account's running work to stop first.</summary>
    private async Task RemoveAsync(string accountId, Button? sender)
    {
        try
        {
            var card = _runtime.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == accountId);
            if (card is null) return;
            var ask = BlindRemoveConfirmation.Build(card);
            if (!await DisplayAlertAsync(ask.Title, ask.Message, ask.ConfirmText, ask.CancelText)) return;
            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up until the account is gone; a failure is said in full.
            await BusyAction.RunAsync(sender, () => _runtime.Accounts.RemoveAsync(accountId), new BusyOptions
            {
                Long = true,
                Title = "Removing the account",
                Message = "Please wait. A running sync or backup is stopped first, then the account, its backups and its keys are deleted from this phone.",
                FailureTitle = "Remove",
            });
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    // ---- repair states --------------------------------------------------------------------------------------------------------------------

    private void ShowRepairs(BlindAccountsView view)
    {
        var repairs = view.Repairs ?? [];
        var signature = string.Join("\n", repairs.Select(r => r.Kind + "|" + r.AccountId + "|" + r.Description));
        if (signature == _repairsSignature) return;
        _repairsSignature = signature;

        RepairsHost.Children.Clear();
        foreach (var issue in repairs)
        {
            var body = new VerticalStackLayout { Spacing = 6 };
            body.Children.Add(Line("Needs a decision", 14, bold: true, colorKey: "WarningColor"));
            body.Children.Add(Line(issue.Description, 13));
            var buttons = new HorizontalStackLayout { Spacing = 8 };
            var accountId = issue.AccountId;
            if (issue.CanAdopt)
                buttons.Children.Add(Small("Adopt it as an account", (sender, args) => _ = RepairAsync(sender as Button, "Adopt it", () =>
                    issue.Kind == BlindRepairKinds.LegacyRootNotListed ? _runtime.Accounts.AdoptLegacyRootAsync() : _runtime.Accounts.AdoptFolderAsync(accountId!))));
            if (issue.CanForget)
                buttons.Children.Add(Small("Forget this account", (sender, args) => _ = RepairAsync(sender as Button, "Forget it", () => _runtime.Accounts.ForgetMissingAccountAsync(accountId!))));
            if (buttons.Children.Count > 0) body.Children.Add(buttons);
            RepairsHost.Children.Add(new Frame { Style = (Style)Application.Current!.Resources["CardFrame"], Content = body });
        }
    }

    /// <summary>One explicit repair action, behind the busy helper; its answer is null on success, otherwise the sentence that says why not.</summary>
    private async Task RepairAsync(Button? sender, string title, Func<Task<string?>> action)
    {
        try
        {
            string? answer = null;
            await BusyAction.RunAsync(sender, async () => answer = await action(), new BusyOptions { Long = true, Title = title, Message = "Please wait.", FailureTitle = title });
            Notice(answer ?? "Done.");
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    // ---- this phone: limits, usage, background --------------------------------------------------------------------------------------------

    private void ShowDeviceLimits(BlindAccountsSnapshot snapshot, BlindAccountsView view)
    {
        if (!_limitsLoaded && snapshot.RegistryProblem is null)
        {
            // Filled once and after a save, never over what the person is typing.
            var form = BlindDeviceLimitsForm.From(snapshot.Device);
            DeviceStoredEntry.Text = form.Stored;
            DeviceTrafficEntry.Text = form.TrafficPerDay;
            _limitsLoaded = true;
        }
        SaveDeviceLimitsButton.IsEnabled = snapshot.RegistryProblem is null;
        DeviceUsageProblemLabel.IsVisible = RepairDeviceUsageButton.IsVisible = view.CanRepairDeviceUsage;
        DeviceUsageProblemLabel.Text = view.DeviceUsageProblem ?? "";
    }

    private void OnSaveDeviceLimitsClicked(object? sender, EventArgs e)
    {
        try
        {
            var (limits, problems) = new BlindDeviceLimitsForm(DeviceStoredEntry.Text ?? "", DeviceTrafficEntry.Text ?? "").Validate();
            var error = limits is null ? string.Join(Environment.NewLine, problems) : _runtime.Accounts.SetDeviceLimits(limits);
            DeviceLimitsErrorLabel.Text = error;
            DeviceLimitsErrorLabel.IsVisible = error is not null;
            if (error is not null) return;
            _limitsLoaded = false;
            Notice("The limits of this phone are saved.");
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(BusyAction.Describe(ex));
        }
    }

    private void OnRepairDeviceUsageClicked(object? sender, EventArgs e)
    {
        var error = _runtime.Accounts.StartNewDeviceUsageLedger();
        Notice(error ?? "New usage counts were started for this phone. The damaged file is kept aside.");
        Refresh();
    }

    private void ShowBackground(BlindAccountsSnapshot snapshot)
    {
        var refresh = UIApplication.SharedApplication.BackgroundRefreshStatus;
        BackgroundLabel.Text = IosAccountsLines.BackgroundNote(snapshot.Accounts.Count, refresh == UIBackgroundRefreshStatus.Available, refresh.ToString(),
            Foundation.NSProcessInfo.ProcessInfo.LowPowerModeEnabled) ?? "";
        BackgroundLabel.IsVisible = BackgroundLabel.Text.Length > 0;

        var alerts = IosAccountsLines.AlertsNote(snapshot.Accounts.Any(a => a.Status is { IsPaired: true }), _silence.Allowed);
        AlertsLabel.Text = alerts ?? "";
        AlertsLabel.IsVisible = alerts is not null;
    }

    // ---- add ------------------------------------------------------------------------------------------------------------------------------

    // busy-exempt: opens the Add page, nothing is sent and nothing waits
    private async void OnAddClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(new AddAccountPage(_runtime, _silence)); }
        catch (Exception ex) { ShowProblem(BusyAction.Describe(ex)); }
    }

    // ---- small helpers --------------------------------------------------------------------------------------------------------------------

    private void Notice(string text)
    {
        NoticeLabel.Text = text;
        NoticeLabel.IsVisible = true;
    }

    private void ShowProblem(string text)
    {
        try
        {
            ProblemLabel.Text = text;
            ProblemLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the controls are gone (the page is closing); nothing is left to tell
        }
    }

    private static Label Line(string text, double size, bool bold = false, string colorKey = "TextColor") => new()
    {
        Text = text,
        FontSize = size,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = (Color)Application.Current!.Resources[colorKey],
    };

    private static Button Small(string text, EventHandler handler, bool primary = false)
    {
        var button = new Button { Text = text, Style = (Style)Application.Current!.Resources[primary ? "PrimaryButton" : "SecondaryButton"] };
        button.Clicked += handler;
        return button;
    }
}
