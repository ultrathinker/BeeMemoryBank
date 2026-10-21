using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;
using UIKit;

namespace BeeMemoryBank.BlindIos.Pages;

/// <summary>
/// The page of ONE account - what the iPhone blind copy's only screen was in 2.6, bound to that account's own controller: the account's state line and its numbers, state (with
/// the last contact and the newest problem), the two pairing codes (only for an account paired the old way), the Limits of the account, backups, the log, and "Remove this account".
/// There is no unlock and no password field anywhere: a blind copy has no master password.
/// <para>The page holds no blind logic and reads no key: it shows <see cref="BlindHomeView"/> and <see cref="BlindAccountCard"/>, built from the status of the account's shared
/// <see cref="IBlindAppController"/>, and asks the device's one loop for jobs of this account. What is iOS's own stays here: the share sheet, the clipboard, the background-refresh
/// state. A wipe of this account makes it a fresh account in place, so the controller is always asked for again, never kept. Every callback is guarded: a failure becomes a
/// sentence on the screen, never an unhandled exception on the UI thread.</para>
/// </summary>
public partial class BlindHomePage : ContentPage
{
    private static readonly BlindBackupSchedule[] Schedules = BlindHomeView.Schedules;
    private static readonly TimeSpan StoreEvery = TimeSpan.FromSeconds(15);

    private readonly IosBlindRuntime _runtime;
    private readonly IosSilenceNotifier _silence;
    private string _accountId;
    private bool _bound;
    private IDispatcherTimer? _timer;
    private bool _applying;
    private bool _refreshPending;
    private bool? _wasLoaded;
    private string? _qrFor;
    private bool _limitsLoaded;
    private DateTimeOffset _backupAskedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _storeAt = DateTimeOffset.MinValue;
    private BlindStoreInfo? _store;
    private bool _readingStore;
#if DEBUG || BMB_E2E
    private bool _e2eCodeUsed;
#endif

    public BlindHomePage(IosBlindRuntime runtime, IosSilenceNotifier silence, string accountId)
    {
        InitializeComponent();
        _runtime = runtime;
        _silence = silence;
        _accountId = accountId;
        SchedulePicker.ItemsSource = new[] { "Off", "Daily", "Weekly" };
    }

    /// <summary>The account this page is about.</summary>
    internal string AccountId => _accountId;

    /// <summary>The account's own controller, asked for each time (a wipe of the account builds a new one in its place). Throws <see cref="KeyNotFoundException"/> once the account is gone.</summary>
    private IBlindAppController Controller => _runtime.Accounts.For(_accountId);

    /// <summary>The account's own services (its activity, its store figures, its paths).</summary>
    private IServiceProvider AccountServices => _runtime.Accounts.ServicesFor(_accountId);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await BindAsync();

            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromSeconds(5);
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();

            if (App.TakePendingLink() is { } link) await AcceptLinkAsync(link);
#if DEBUG || BMB_E2E
            // End-to-end checks on a simulator or a development iPhone (tools/ios-e2e): the computer's answer is handed over at launch,
            // because a script cannot tap the "Open in ...?" prompt iOS shows for a link. Accepted exactly like a pasted code. Only in a
            // Debug build or one made with -p:BmbE2E=true; a Release build for people does not contain it (IosBoundaryTests).
            if (!_e2eCodeUsed && Environment.GetEnvironmentVariable("BMB_BLIND_E2E_CALL_CODE") is { Length: > 0 } e2eCode)
            {
                _e2eCodeUsed = true;
                await AcceptLinkAsync(e2eCode);
            }
            // On a real iPhone there is no screenshot to read the QR code from: the check starts the app with its console attached
            // (xcrun devicectl ... --console) and reads the phone code there. The code is on the screen anyway; this only repeats it to
            // the developer tool that asked for it.
            if (Environment.GetEnvironmentVariable("BMB_BLIND_E2E_PRINT_PHONE_CODE") == "1" && Controller.PairingCode() is { } shown)
                Console.WriteLine("BMB_E2E_PHONE_CODE " + shown);
#endif
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    protected override void OnDisappearing()
    {
        Unbind();
        _timer?.Stop();
        base.OnDisappearing();
    }

    /// <summary>Listens to the accounts of the phone (this account's changes are among them), opens the account's database and makes its identity if there is none (a failure is in the status).</summary>
    private async Task BindAsync()
    {
        Unbind();
        _bound = true;
        _wasLoaded = null;
        _storeAt = DateTimeOffset.MinValue;
        _runtime.Accounts.Changed += OnAppChanged;
        _runtime.Scheduler.UserJobReported += OnUserJobReported;
        await _runtime.StartAsync();
        try
        {
            await Controller.InitializeAsync();
        }
        catch (KeyNotFoundException)
        {
            // The account has no running copy (it could not start, or it is being removed): its state line says why, and the way out is Remove.
        }
        Refresh();
    }

    private void Unbind()
    {
        if (!_bound) return;
        _runtime.Accounts.Changed -= OnAppChanged;
        _runtime.Scheduler.UserJobReported -= OnUserJobReported;
        _bound = false;
    }

    private void OnAppChanged()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _refreshPending = false;
            Refresh();
        });
    }

    private void OnUserJobReported(string accountId, string sentence)
    {
        if (accountId == _accountId) MainThread.BeginInvokeOnMainThread(() => Notice(sentence));
    }

    /// <summary>Reads the status again and shows it. Never throws: the 5-second timer and every handler call it.</summary>
    private void Refresh()
    {
        try
        {
            var snapshot = _runtime.Accounts.GetSnapshot();
            var account = snapshot.Accounts.FirstOrDefault(a => a.Entry.Id == _accountId);
            if (account is null)
            {
                // The account was removed (here, or by its owner's decision on the Accounts page): there is nothing left to show.
                LeavePage();
                return;
            }

            ShowAccount(account, BlindAccountCard.Build(account, TimeZoneInfo.Local, DateTimeOffset.UtcNow));
            if (account.Entry.Phase == BlindAccountPhases.Active && account.Status is not null)
            {
                var controller = Controller;
                var status = controller.GetStatus();
                var code = status.AwaitingAnswer ? controller.PairingCode() : null;
                var backupRunning = AccountServices.GetRequiredService<BlindActivity>().IsActive(BlindActivity.Heavy) || DateTimeOffset.UtcNow - _backupAskedAt < TimeSpan.FromSeconds(10);
                Show(BlindHomeView.Build(status, code, backupRunning, _store), status, account);
                FollowFirstLoad(status);
                ReadStoreSoon(status);
            }
            _silence.Update(snapshot);
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    /// <summary>Back to the accounts: the account this page was about is gone.</summary>
    // busy-exempt: closes the page, nothing is sent and nothing waits
    private async void LeavePage()
    {
        try
        {
            _timer?.Stop();
            if (Navigation.NavigationStack.Contains(this)) await Navigation.PopAsync();
        }
        catch (Exception)
        {
            // the page is already closing
        }
    }

    /// <summary>
    /// The account's own header and what depends on how far the account is: an account that has not been confirmed by its owner (or was refused, expired, removed) shows its state line and
    /// the way out, nothing of a working copy; an invited account never shows the pairing-code steps, because it spent its secret and never waits for a computer's answer.
    /// </summary>
    private void ShowAccount(BlindAccountSnapshot account, BlindAccountCard card)
    {
        var entry = account.Entry;
        Title = entry.Name;
        AccountStateLabel.Text = card.State;
        AccountStateLabel.TextColor = (Color)Application.Current!.Resources[card.Kind switch
        {
            BlindCardKind.Ok => "SuccessColor",
            BlindCardKind.Waiting => "WarningColor",
            BlindCardKind.Problem => "ErrorColor",
            BlindCardKind.Paused or BlindCardKind.Removing => "SecondaryTextColor",
            _ => "TextColor",
        }];
        AccountStoredLabel.Text = card.Stored.Length > 0 ? "Stored: " + card.Stored : "";
        AccountStoredLabel.IsVisible = card.Stored.Length > 0;
        AccountTodayLabel.Text = card.Today.Length > 0 ? "Today: " + card.Today : "";
        AccountTodayLabel.IsVisible = card.Today.Length > 0;
        AccountAverageLabel.Text = card.Average;
        AccountAverageLabel.IsVisible = entry.Phase == BlindAccountPhases.Active;
        PauseButton.Text = entry.PausedByUser ? "Resume this account" : "Pause this account";
        PauseButton.IsVisible = entry.Phase == BlindAccountPhases.Active;
        ReconnectButton.IsVisible = entry.Phase == BlindAccountPhases.RemovedByOwner;
        RepairUsageButton.IsVisible = card.CanRepairUsage;

        var active = entry.Phase == BlindAccountPhases.Active;
        StateFrame.IsVisible = active;
        BackupsFrame.IsVisible = active;
        LogFrame.IsVisible = active;
        LimitsFrame.IsVisible = entry.Phase != BlindAccountPhases.Deleting;
        CodesFrame.IsVisible = active && entry.InviteId is null;
        ShowLimits(entry);
    }

    /// <summary>The account's limits and its two switches, filled once and after a save, never over what the person is typing.</summary>
    private void ShowLimits(BlindAccountEntry entry)
    {
        if (_limitsLoaded) return;
        var form = BlindLimitsForm.From(entry);
        StoredLimitEntry.Text = form.Stored;
        TrafficLimitEntry.Text = form.TrafficPerDay;
        RequestsLimitEntry.Text = form.RequestsPerDay;
        UnmeteredSwitch.IsToggled = form.UnmeteredOnly;
        PowerSavingSwitch.IsToggled = form.PauseOnPowerSaving;
        LimitsHintLabel.Text = BlindLimitsForm.Explanation;
        _limitsLoaded = true;
    }

    private void Show(BlindHomeView view, BlindAppStatus status, BlindAccountSnapshot account)
    {
        StatusErrorLabel.IsVisible = false;
        NameLabel.Text = view.Name;
        NodeLabel.Text = view.Node;
        PairLabel.Text = view.Pair;
        LoadLabel.Text = view.Load;
        StoreLabel.Text = view.Store;
        SyncLabel.Text = view.Sync;
        BackupLabel.Text = view.Backup;
        if (view.Job != null)
        {
            JobProgress.IsVisible = JobLabel.IsVisible = true;
            JobProgress.Progress = view.JobProgress ?? 0;
            JobLabel.Text = view.Job;
        }
        else
        {
            JobProgress.IsVisible = JobLabel.IsVisible = false;
        }

        FailureLabel.Text = view.Problem;
        FailureLabel.IsVisible = view.Problem != null;
        KeyLostLabel.Text = view.KeyLostText;
        KeyLostLabel.IsVisible = view.KeyLostText != null;
        KeyStoreLabel.Text = view.KeyStoreText;
        KeyStoreLabel.IsVisible = view.KeyStoreText != null;
        StartErrorLabel.Text = view.StartErrorText;
        StartErrorLabel.IsVisible = view.StartErrorText != null;
        ShowBackgroundState(status);

        ShowPhoneCode(view);
        _applying = true;
        try { SchedulePicker.SelectedIndex = view.ScheduleIndex; }
        finally { _applying = false; }
        BackupsLabel.Text = view.Backups;
        SaveToButton.IsEnabled = view.CanSaveTo;
        BackupNowButton.Text = view.BackupNowText;
        BackupNowButton.IsEnabled = view.CanBackupNow;
        SyncNowButton.IsEnabled = view.CanSyncNow;
        ConnectButton.IsEnabled = PasteButton.IsEnabled = view.CanConnect;
        LogLabel.Text = view.Log;
    }

    /// <summary>What iOS lets the copy do when the app is not in front, and whether it may warn about a silent node.</summary>
    private void ShowBackgroundState(BlindAppStatus status)
    {
        var refresh = UIApplication.SharedApplication.BackgroundRefreshStatus;
        var lowPower = NSProcessInfoLowPower();
        var round = IosLastRound.Describe(AccountServices.GetService(typeof(IBlindStateStore)) is IBlindStateStore store ? store.Get(IosBlindBackground.LastRoundKey) : null);
        BackgroundLabel.Text = refresh != UIBackgroundRefreshStatus.Available
            ? $"Background App Refresh is off for this app ({refresh}): the copy syncs only while it is open. Settings > General > Background App Refresh turns it on.\n{round}"
            : lowPower
                ? $"Low Power Mode is on: iOS gives the copy no background time until it is off.\n{round}"
                : round;

        var warning = IosHomeLines.SilenceWarning(status, _silence.NextWarningOf(_accountId), _silence.Allowed);
        AlertsLabel.Text = warning;
        AlertsLabel.IsVisible = warning != null;
        AlertsLabel.TextColor = _silence.Allowed == false ? (Color)Application.Current!.Resources["WarningColor"] : (Color)Application.Current!.Resources["SecondaryTextColor"];
    }

    private static bool NSProcessInfoLowPower() => Foundation.NSProcessInfo.ProcessInfo.LowPowerModeEnabled;

    /// <summary>The first load just finished: a sync round at once, so that "last contact" and the notes do not wait for the next slot.</summary>
    private void FollowFirstLoad(BlindAppStatus status)
    {
        if (_wasLoaded == false && status.InitialLoadDone && status.IsPaired)
        {
            _runtime.Scheduler.RequestSync(_accountId);
            _storeAt = DateTimeOffset.MinValue;
        }
        _wasLoaded = status.InitialLoadDone;
    }

    /// <summary>Reads what the copy holds now and then, off the UI thread (docs/design/step-a-spec.md 10.2); the line shows the last answer.</summary>
    private void ReadStoreSoon(BlindAppStatus status)
    {
        if (_readingStore || DateTimeOffset.UtcNow - _storeAt < StoreEvery) return;
        if (!status.InitialLoadDone) { _store = null; return; }
        _readingStore = true;
        _storeAt = DateTimeOffset.UtcNow;
        var accountId = _accountId;
        var stats = AccountServices.GetRequiredService<BlindStoreStats>();
        _ = Task.Run(async () =>
        {
            var summary = await stats.GetAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _readingStore = false;
                if (accountId != _accountId) return;
                _store = summary;
                try { StoreLabel.Text = BlindHomeView.StoreText(summary, Controller.GetStatus(), DateTimeOffset.UtcNow); }
                catch (Exception ex) { ShowProblem(ex); }
            });
        });
    }

    private void ShowPhoneCode(BlindHomeView view)
    {
        PhoneCodeImage.IsVisible = view.ShowPairingCode;
        CopyCodeButton.IsEnabled = view.ShowPairingCode;
        RePairButton.IsVisible = view.CanRePair;
        PhoneCodeLabel.Text = view.PairingMessage;
        if (view.PairingCode == _qrFor) return;

        // The picture is a convenience: a failing QR library must not take the rest of the screen with it, and it is made once per code.
        _qrFor = view.PairingCode;
        try
        {
            if (view.PairingCode is { } code)
            {
                using var generator = new QRCodeGenerator();
                var png = new PngByteQRCode(generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.Q)).GetGraphic(10);
                PhoneCodeImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
            }
            else
            {
                PhoneCodeImage.Source = null;
            }
        }
        catch (Exception ex)
        {
            PhoneCodeImage.IsVisible = false;
            PhoneCodeLabel.Text = $"The QR picture could not be made ({BusyAction.Describe(ex)}). Copy the code with the button below instead.";
        }
    }

    private void Notice(string text)
    {
        NoticeLabel.Text = text;
        NoticeLabel.IsVisible = true;
    }

    /// <summary>Even the status could not be read: say so with the way out, and keep the wipe button usable.</summary>
    private void ShowProblem(Exception ex)
    {
        try
        {
            StatusErrorLabel.Text = BlindHomeView.StatusFailedText(ex);
            StatusErrorLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the controls are gone (the page is closing); nothing is left to tell
        }
    }

    /// <summary>
    /// A bmb-blind-call: link the app was opened with (the Camera scanned the computer's QR code), or one that waited for this screen, or the code the end-to-end check
    /// hands over. It goes the SAME way as the Connect button (BMB-311 fix2, R3): behind the busy helper, keyed to the Connect button, so the screen is covered until the
    /// first load has ended and a second link or a tap cannot start another one.
    /// </summary>
    public async Task AcceptLinkAsync(string url)
    {
        CallCodeEntry.Text = "";
        await ConnectAsync(ConnectButton, url, fromLink: true);
    }

    /// <summary>
    /// The computer's answer, pasted or from a link: given to the phone's accounts and accepted only by the one whose pairing secret made it (a code made for another phone, or for an
    /// account that is not waiting, is refused with a sentence and changes nothing). When it lands in another account than this page's, the page moves over to that one.
    /// </summary>
    private bool Accept(string text)
    {
        try
        {
            // The host says WHICH account took the code (the one whose own pairing secret made it): not guessed from the states of the accounts that wait, which are all paired during a re-pair.
            var accepted = _runtime.Accounts.AcceptCallCodeWithAccount(text);
            CallErrorLabel.Text = accepted.Error;
            CallErrorLabel.IsVisible = accepted.Error != null;
            if (accepted.Ok)
            {
                CallCodeEntry.Text = "";
                if (accepted.AccountId != _accountId) MoveTo(accepted.AccountId!);
                // Paired: the first load at once, not at the next hourly slot.
                _runtime.Scheduler.EnsureScheduled();
                _runtime.Scheduler.RequestHeavy(_accountId);
            }
            Refresh();
            return accepted.Ok;
        }
        catch (Exception ex)
        {
            CallErrorLabel.Text = $"Could not connect: {BusyAction.Describe(ex)}";
            CallErrorLabel.IsVisible = true;
            return false;
        }
    }

    /// <summary>The page now shows another account (the computer's answer landed there).</summary>
    private void MoveTo(string accountId)
    {
        _accountId = accountId;
        _limitsLoaded = false;
        _qrFor = null;
        _wasLoaded = null;
        _store = null;
    }

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyPhoneCodeClicked(object? sender, EventArgs e)
    {
        try
        {
            if (Controller.PairingCode() is { } code)
            {
                await Clipboard.Default.SetTextAsync(code);
                Notice("The code is on the clipboard. Paste it only on your own computer.");
            }
        }
        catch (Exception ex)
        {
            await TellAsync("Copy this iPhone's code", BusyAction.Describe(ex));
        }
    }

    // busy-exempt: pastes from the clipboard, nothing is sent and nothing waits
    private async void OnPasteClicked(object? sender, EventArgs e)
    {
        try
        {
            CallCodeEntry.Text = (await Clipboard.Default.GetTextAsync())?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            await TellAsync("Paste", BusyAction.Describe(ex));
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        await ConnectAsync(sender as Button, CallCodeEntry.Text ?? "", fromLink: false);
    }

    private async Task ConnectAsync(Button? sender, string code, bool fromLink)
    {
        // A long operation (BMB-311): pairing and the first copy. The one busy helper covers the screen from the start until the state of the phone says the
        // first load has ended, failed or was never started, so Connect cannot be started twice (by a tap, a link or the end-to-end code); the reason a code was
        // refused is written under the field.
        await BusyAction.RunAsync(sender, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, Controller.GetStatus(), DateTimeOffset.UtcNow);
            if (!Accept(code)) return;
            if (fromLink) Notice("Connected with the code from the link. The first load starts now.");
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy);
            if (outcome.Phase == BlindJobPhase.Failed) Notice("The first load did not finish. The reason is in the Problem line of this page.");
            else if (outcome.Phase == BlindJobPhase.Unreported) Notice(outcome.Message ?? "");
            Refresh();
        }, new BusyOptions
        {
            Long = true,
            Title = "Connecting",
            Message = "Please wait. The first copy of the data is being loaded; this can take a while. Keep the app open until it is done; iOS pauses it in the background.",
            FailureTitle = "Connect",
        });
    }

    /// <summary>
    /// Waits for the job just asked for, until the STATE of the phone says it has ended (<see cref="BlindJobWait.Judge"/>): it runs, or it is queued and not
    /// started yet, and it ends when its mark appears, its failure is recorded, or - after a grace for the queue - nothing was ever seen of it. There is no
    /// limit on how long a running job may take: the button is not given back while it runs, and after half an hour the pop-up says that it is still
    /// running and not to start it again.
    /// </summary>
    private async Task<BlindJobOutcome> WaitForJobAsync(BusyProgress busy, BlindJobAsk ask, params string[] kinds)
    {
        var said = false;
        for (;;)
        {
            var activity = AccountServices.GetRequiredService<BlindActivity>();
            var now = DateTimeOffset.UtcNow;
            var outcome = BlindJobWait.Judge(ask, Controller.GetStatus(), kinds.Any(activity.IsActive), now);
            if (!outcome.IsOpen) return outcome;
            if (!said && now - ask.At > BlindJobWait.StillRunningAfter)
            {
                busy.Report(null, BlindJobWait.StillRunningText);
                said = true;
            }
            await Task.Delay(1000);
        }
    }

    private async void OnRePairClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await DisplayAlertAsync("Re-pair",
                    "This makes a new code for adding the iPhone on a computer again. The current connection stays until the computer's new answer is accepted.",
                    "Make a new code", "Cancel"))
                return;
            // The one busy helper (BMB-311): the button is off and spins until the new code is made; a failure is said in full.
            await BusyAction.RunAsync(sender as Button, () =>
            {
                Controller.StartRePair();
                Refresh();
                return Task.CompletedTask;
            }, new BusyOptions { FailureTitle = "Re-pair" });
        }
        catch (Exception ex)
        {
            await TellAsync("Re-pair", BusyAction.Describe(ex));
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the state of the phone says the sync has
        // ended (it has no time limit: the button is not given back while the round runs); a failure to even ask is said in full.
        await BusyAction.RunAsync(sender as Button, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.Sync, Controller.GetStatus(), DateTimeOffset.UtcNow);
            var scheduler = _runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestSync(_accountId);
            Notice("Sync asked for.");
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Sync);
            if (outcome.Phase == BlindJobPhase.Unreported) Notice(outcome.Message ?? "");
            Refresh();
        }, new BusyOptions
        {
            Long = true,
            Title = "Syncing",
            Message = "Please wait while this iPhone syncs. Keep the app open until it is done; iOS pauses it in the background.",
            FailureTitle = "Sync now",
        });
    }

    private async void OnBackupNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the state of the phone says the backup has
        // ended (it has no time limit: the button is not given back while the job runs). The button is off at once: a second tap cannot ask for a second
        // job (the loop runs one job at a time anyway).
        var ran = await BusyAction.RunAsync(sender as Button, async busy =>
        {
            _backupAskedAt = DateTimeOffset.UtcNow;
            var ask = BlindJobWait.Ask(BlindJobWait.Backup, Controller.GetStatus(), _backupAskedAt);
            var scheduler = _runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestBackup(_accountId);
            Notice("Backup asked for. Keep the app open until it is done; iOS pauses it in the background.");
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy, BlindActivity.BackupService);
            if (outcome.Phase == BlindJobPhase.Failed) Notice("The backup did not finish. The reason is in the Problem line of this page.");
            else if (outcome.Phase == BlindJobPhase.Unreported) Notice(outcome.Message ?? "");
        }, new BusyOptions
        {
            Long = true,
            Title = "Backing up",
            Message = "Please wait while the backup is made. Keep the app open until it is done; iOS pauses it in the background.",
            FailureTitle = "Back up now",
        });
        if (!ran) _backupAskedAt = DateTimeOffset.MinValue;
        Refresh();
    }

    /// <summary>The share sheet ("Save to Files", AirDrop, another app) with the newest backup file of the backup folder, by name.</summary>
    private async void OnSaveToClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the file leaves the app through the share sheet, which waits for the person. The one busy helper keeps the screen
        // covered until the sheet is done, so nothing else is started meanwhile; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            if (Controller.GetStatus().Backups.FirstOrDefault() is not { } latest) return;
            var path = Path.Combine(BlindPaths.Backups(AccountServices.GetRequiredService<IBlindPaths>().DataDirectory), Path.GetFileName(latest.Name));
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Save the blind copy's backup", File = new ShareFile(path) });
        }, new BusyOptions
        {
            Long = true,
            Title = "Saving the backup",
            Message = "Please wait while the backup is handed to the share sheet.",
            FailureTitle = "Save to…",
        });
        Refresh();
    }

    private void OnScheduleChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!_applying && SchedulePicker.SelectedIndex >= 0) Controller.SetSchedule(Schedules[SchedulePicker.SelectedIndex]);
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    /// <summary>"Save" of the Limits section: the form checks what was typed (an empty box is no limit, 0 is a pause, too little to keep up is refused with the reason), then the limits and the two switches are stored.</summary>
    private void OnSaveLimitsClicked(object? sender, EventArgs e)
    {
        try
        {
            var form = new BlindLimitsForm(StoredLimitEntry.Text ?? "", TrafficLimitEntry.Text ?? "", RequestsLimitEntry.Text ?? "", UnmeteredSwitch.IsToggled, PowerSavingSwitch.IsToggled);
            var checkedForm = form.Validate();
            var error = checkedForm.Ok
                ? _runtime.Accounts.SetLimits(_accountId, checkedForm.Limits!) ?? _runtime.Accounts.SetSwitches(_accountId, form.UnmeteredOnly, form.PauseOnPowerSaving)
                : string.Join(Environment.NewLine, checkedForm.Problems);
            LimitsErrorLabel.Text = error;
            LimitsErrorLabel.IsVisible = error is not null;
            if (error is not null) return;
            _limitsLoaded = false;
            Notice("The limits are saved.");
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    /// <summary>"Pause this account" and "Resume": the person's own pause. The source is told that it is the person's, so that its page says "paused", not "silent".</summary>
    private void OnPauseClicked(object? sender, EventArgs e)
    {
        try
        {
            var entry = _runtime.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId)?.Entry;
            if (entry is null) return;
            var error = _runtime.Accounts.SetPaused(_accountId, !entry.PausedByUser);
            if (error is not null) Notice(error);
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    // busy-exempt: asks for a name and writes it to the local list of accounts; nothing is sent and nothing waits
    private async void OnRenameClicked(object? sender, EventArgs e)
    {
        try
        {
            var entry = _runtime.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId)?.Entry;
            if (entry is null) return;
            var name = await DisplayPromptAsync("Rename", "What do you call this account?", "Save", "Cancel", initialValue: entry.Name, maxLength: BlindAccountEntry.MaxNameLength);
            if (name is null) return;
            var error = _runtime.Accounts.Rename(_accountId, name);
            if (error is not null) Notice(error);
            Refresh();
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    /// <summary>The source stopped accepting this phone and the person wants to try again (a mistake on its side): the next round decides.</summary>
    private void OnReconnectClicked(object? sender, EventArgs e)
    {
        var error = _runtime.Accounts.Reconnect(_accountId);
        Notice(error ?? "It will try the source again at the next round.");
        Refresh();
    }

    /// <summary>Repair, on purpose: the usage counts of this account cannot be read or trusted, so its limits are not checked and it is paused. The damaged file is kept aside.</summary>
    private void OnRepairUsageClicked(object? sender, EventArgs e)
    {
        var error = _runtime.Accounts.StartNewUsageLedger(_accountId);
        Notice(error ?? "New usage counts were started. The damaged file is kept aside.");
        Refresh();
    }

    /// <summary>"Remove this account": what goes and who is told, in the words of <see cref="BlindRemoveConfirmation"/>; then the removal.</summary>
    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        try
        {
            var card = _runtime.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId);
            if (card is null) return;
            var ask = BlindRemoveConfirmation.Build(card);
            if (!await DisplayAlertAsync(ask.Title, ask.Message, ask.ConfirmText, ask.CancelText)) return;
            // Off the UI thread: the removal waits (bounded) for a running sync, first load or backup to end before it deletes anything; it tells the source when this phone is online.
            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up until the account is gone.
            var accountId = _accountId;
            var incomplete = false;
            await BusyAction.RunAsync(sender as Button, async () =>
            {
                try
                {
                    await Task.Run(() => _runtime.Accounts.RemoveAsync(accountId));
                }
                catch (AggregateException)
                {
                    incomplete = true;
                    throw;
                }
            }, new BusyOptions
            {
                Long = true,
                Title = "Removing the account",
                Message = "Please wait. A running sync or backup is stopped first, then the account, its backups and its keys are deleted from this iPhone.",
                Result = (ok, text) =>
                {
                    if (ok) return;
                    _ = TellAsync("Remove this account", incomplete
                        ? $"{text}{Environment.NewLine}{Environment.NewLine}Press Remove again to finish."
                        : $"Could not remove it: {text}");
                },
            });
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Remove this account", $"Could not remove it: {BusyAction.Describe(ex)}");
            Refresh();
        }
    }

    private async Task TellAsync(string title, string message)
    {
        try { await DisplayAlertAsync(title, message, "OK"); }
        catch (Exception) { /* the page is closing */ }
    }
}
