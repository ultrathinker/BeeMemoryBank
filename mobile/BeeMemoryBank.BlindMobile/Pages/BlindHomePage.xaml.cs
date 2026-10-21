using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.BlindMobile.Services.Blind;
using QRCoder;

namespace BeeMemoryBank.BlindMobile.Pages;

/// <summary>
/// The page of ONE account (plan section 10, bound to one account's controller): state, the card of the account (what it is doing, what it used), its limits, the two pairing codes of the old way,
/// backups (now, "Save to…", schedule), the log, and "Remove this account". There is no unlock: a blind copy has no master password.
/// <para>The page holds no blind logic and never reads a key itself: it shows <see cref="BlindHomeView"/>, built from the status of the
/// shared <see cref="IBlindAppController"/> of its account (the same one the desktop hosts use), whose reads of the AndroidKeyStore cannot throw into the
/// screen, and <see cref="BlindAccountCard"/>, whose sentences are the shared AppCore's. Only what is Android's own stays here: WorkManager and service requests, the file picker, the
/// clipboard, the QR picture. Every callback of the page is guarded: a failure becomes a sentence on the screen, never an unhandled exception on the UI thread.</para>
/// </summary>
public partial class BlindHomePage : ContentPage
{
    private static readonly BlindBackupSchedule[] Schedules = BlindHomeView.Schedules;

    private readonly BlindDevice _device;
    private readonly string _accountId;
    private readonly IBlindAppController _app;
    private readonly BlindHeavyWork _work;
    private readonly BlindActivity _activity;
    private readonly IBlindPaths _paths;
    private readonly BlindStoreStats _stats;
    private static readonly TimeSpan StoreEvery = TimeSpan.FromSeconds(15);
    private DateTimeOffset _storeAt = DateTimeOffset.MinValue;
    private BlindStoreInfo? _store;
    private bool _readingStore;
    private IDispatcherTimer? _timer;
    private bool _applying;
    private bool _refreshPending;
    private bool _closing;
    private bool _limitsFilled;
    private string? _qrFor;
    private DateTimeOffset _backupAskedAt = DateTimeOffset.MinValue;
    private BusyProgress? _busyProgress;   // the pop-up of the long operation now running, if any (the job's progress is shown in it too)

    /// <param name="accountId">The account this page is about; it must be there (the caller handles <see cref="KeyNotFoundException"/>).</param>
    public BlindHomePage(IServiceProvider services, string accountId)
    {
        InitializeComponent();
        _device = services.GetRequiredService<BlindDevice>();
        _accountId = accountId;
        // The controller and the services of THIS account: one account's page never reaches another's.
        _app = _device.Accounts.For(accountId);
        var account = _device.Accounts.ServicesFor(accountId);
        _work = account.GetRequiredService<BlindHeavyWork>();
        _activity = account.GetRequiredService<BlindActivity>();
        _paths = account.GetRequiredService<IBlindPaths>();
        _stats = account.GetRequiredService<BlindStoreStats>();
        SchedulePicker.ItemsSource = new[] { "Off", "Daily", "Weekly" };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _work.Progress += OnJobProgress;
            _app.Changed += OnAppChanged;

            // The account host started this account (it opened the database and made the identity; a failure is kept in the status as StartError): the page only shows it.
            await _device.StartAsync();
            Refresh();

            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromSeconds(5);
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();
        }
        catch (Exception)
        {
            ShowProblem();
        }
    }

    protected override void OnDisappearing()
    {
        _work.Progress -= OnJobProgress;
        _app.Changed -= OnAppChanged;
        _timer?.Stop();
        base.OnDisappearing();
    }

    /// <summary>The controller says something changed (any thread): one refresh on the UI thread, however many events come.</summary>
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

    /// <summary>Reads the status again and shows it. Never throws: the 5-second timer and every handler call it.</summary>
    private void Refresh()
    {
        try
        {
            var status = _app.GetStatus();
            var code = status.AwaitingAnswer ? _app.PairingCode() : null;
            var backupRunning = _activity.IsActive(BlindActivity.Heavy) || _activity.IsActive(BlindActivity.BackupService)
                || DateTimeOffset.UtcNow - _backupAskedAt < TimeSpan.FromSeconds(10);
            Show(BlindHomeView.Build(status, code, backupRunning, _store));
            ReadStoreSoon(status);
            ShowAccount();
        }
        catch (Exception)
        {
            ShowProblem();
        }
    }

    /// <summary>Reads what the copy holds now and then, off the UI thread (docs/design/step-a-spec.md 10.2); the line shows the last answer.</summary>
    private void ReadStoreSoon(BlindAppStatus status)
    {
        if (_readingStore || DateTimeOffset.UtcNow - _storeAt < StoreEvery) return;
        if (!status.InitialLoadDone) { _store = null; return; }
        _readingStore = true;
        _storeAt = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            var summary = await _stats.GetAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _readingStore = false;
                _store = summary;
                try { StoreLabel.Text = BlindHomeView.StoreText(summary, _app.GetStatus(), DateTimeOffset.UtcNow); }
                catch (Exception) { ShowProblem(); }
            });
        });
    }

    /// <summary>The account's card (its state line and what it used, from the shared AppCore view), the pause button, and the parts of the page that only make sense for how this account was added.</summary>
    private void ShowAccount()
    {
        var account = _device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId);
        if (account is null)
        {
            // Removed (here or by a repair): there is nothing left to show.
            CloseSoon();
            return;
        }

        var entry = account.Entry;
        var card = BlindAccountCard.Build(account, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        Title = entry.Name;
        CardStateLabel.Text = card.State;
        CardStateLabel.TextColor = ColourOf(card.Kind);
        CardUsageLabel.Text = string.Join(Environment.NewLine, new[] { card.Stored, card.Today, card.Average }.Where(line => line.Length > 0));
        CardUsageLabel.IsVisible = CardUsageLabel.Text.Length > 0;
        ReconnectButton.IsVisible = entry.Phase == BlindAccountPhases.RemovedByOwner;
        RepairUsageButton.IsVisible = card.CanRepairUsage;
        PauseButton.Text = entry.PausedByUser ? "Resume this account" : "Pause this account";
        PauseButton.IsEnabled = card.CanEdit;
        SaveLimitsButton.IsEnabled = card.CanEdit;
        RenameButton.IsEnabled = card.CanEdit;
        RemoveButton.IsEnabled = entry.Phase != BlindAccountPhases.Deleting;

        // An account added from an invitation never shows the codes of the old way; one that has not been confirmed has no copy yet, so no state or backups to show.
        var invited = entry.InviteId is not null;
        var live = entry.Phase == BlindAccountPhases.Active;
        CodesFrame.IsVisible = !invited;
        StateFrame.IsVisible = live;
        BackupsFrame.IsVisible = live;

        if (!_limitsFilled) FillLimitsForm(entry);
    }

    private static Color ColourOf(BlindCardKind kind) => Application.Current?.Resources.TryGetValue(kind switch
    {
        BlindCardKind.Ok => "SuccessColor",
        BlindCardKind.Paused => "WarningColor",
        BlindCardKind.Problem => "ErrorColor",
        BlindCardKind.Removing => "SecondaryTextColor",
        _ => "TextColor",
    }, out var value) == true && value is Color colour ? colour : Colors.Gray;

    /// <summary>The form as the account's limits are now. Filled once when the page opens and again after a save: never under the person's fingers.</summary>
    private void FillLimitsForm(BlindAccountEntry entry)
    {
        var form = BlindLimitsForm.From(entry);
        StoredEntry.Text = form.Stored;
        TrafficEntry.Text = form.TrafficPerDay;
        RequestsEntry.Text = form.RequestsPerDay;
        UnmeteredSwitch.IsToggled = form.UnmeteredOnly;
        BatterySwitch.IsToggled = form.PauseOnPowerSaving;
        LimitsProblemLabel.IsVisible = false;
        _limitsFilled = true;
    }

    private void CloseSoon()
    {
        if (_closing) return;
        _closing = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await Navigation.PopAsync(); }
            catch (Exception) { /* the page is already gone */ }
        });
    }

    private void Show(BlindHomeView view)
    {
        StatusErrorLabel.IsVisible = false;
        NameLabel.Text = view.Name;
        NodeLabel.Text = view.Node;
        PairLabel.Text = view.Pair;
        LoadLabel.Text = view.Load;
        StoreLabel.Text = view.Store;
        SyncLabel.Text = view.Sync;
        BackupLabel.Text = view.Backup;
        if (view.Job != null) ShowJob(view.Job, view.JobProgress);
        else JobProgress.IsVisible = JobLabel.IsVisible = false;

        KeyLostLabel.Text = view.KeyLostText;
        KeyLostLabel.IsVisible = view.KeyLostText != null;
        KeyStoreLabel.Text = view.KeyStoreText;
        KeyStoreLabel.IsVisible = view.KeyStoreText != null;
        StartErrorLabel.Text = view.StartErrorText;
        StartErrorLabel.IsVisible = view.StartErrorText != null;

        ShowPhoneCode(view);
        _applying = true;
        try { SchedulePicker.SelectedIndex = view.ScheduleIndex; }
        finally { _applying = false; }
        BackupsLabel.Text = view.Backups;
        SaveToButton.IsEnabled = view.CanSaveTo;
        BackupNowButton.Text = view.BackupNowText;
        BackupNowButton.IsEnabled = view.CanBackupNow;
        SyncNowButton.IsEnabled = view.CanSyncNow;
        ConnectButton.IsEnabled = view.CanConnect;
        LogLabel.Text = view.Log;
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
        catch (Exception)
        {
            PhoneCodeImage.IsVisible = false;
            PhoneCodeLabel.Text = BlindPageSentences.QrNotMade;
        }
    }

    /// <summary>Even the status could not be read: a fixed sentence with the way out (never the exception's own text), and the remove button stays usable.</summary>
    private void ShowProblem()
    {
        try
        {
            StatusErrorLabel.Text = BlindPageSentences.StatusUnreadable;
            StatusErrorLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the controls are gone (the page is closing); nothing is left to tell
        }
    }

    private void ShowJob(string title, double? progress) => MainThread.BeginInvokeOnMainThread(() =>
    {
        JobProgress.IsVisible = JobLabel.IsVisible = true;
        JobProgress.Progress = progress ?? 0;
        JobLabel.Text = progress is { } p ? $"{title}: {p:P0}" : title;
    });

    /// <summary>Progress of the scheduled worker and the backup service, which the controller does not run (it only shows jobs it started).</summary>
    private void OnJobProgress(string title, double progress) => MainThread.BeginInvokeOnMainThread(() =>
    {
        try
        {
            _busyProgress?.Report(progress, $"{title}: {progress:P0}");
            JobProgress.IsVisible = JobLabel.IsVisible = progress < 1;
            JobProgress.Progress = progress;
            JobLabel.Text = $"{title}: {progress:P0}";
        }
        catch (Exception)
        {
            ShowProblem();
        }
    });

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyPhoneCodeClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_app.PairingCode() is { } code) await Clipboard.Default.SetTextAsync(code);
        }
        catch (Exception)
        {
            await TellAsync("Copy this phone's code", BlindPageSentences.CodeNotCopied);
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the first load (started by the
        // pairing) has ended, so Connect cannot be tapped twice; the reason it did not work is written under the field.
        await BusyAction.RunAsync(sender as Button, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, _app.GetStatus(), DateTimeOffset.UtcNow);
            var error = _app.AcceptCallCode(CallCodeEntry.Text ?? "");
            CallErrorLabel.Text = error;
            CallErrorLabel.IsVisible = error != null;
            if (error == null)
            {
                CallCodeEntry.Text = "";
                // Paired: request the first load of THIS account without waiting for the next hourly slot.
                _device.HeavyScheduler.RequestHeavy(_accountId);
#if ANDROID
                Platforms.Android.BlindWorkScheduler.RunHeavyNow(Platform.AppContext);
#endif
                // Locked until the first load has ended, or failed, or was never started (the state says which), not until a clock runs out.
                var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy);
                if (outcome.Phase is BlindJobPhase.Failed or BlindJobPhase.Unreported)
                {
                    NoticeLabel.Text = BlindPageSentences.JobEnd(outcome, BlindJobWait.FirstLoad, finished: "", BlindPageSentences.FirstLoadNotFinished, ended: "");
                    NoticeLabel.IsVisible = true;
                }
            }
            Refresh();
        }, new BusyOptions
        {
            Long = true,
            Title = "Connecting",
            Message = "Please wait. The first copy of the data is being loaded; this can take a while. You can leave the app: the job goes on.",
            Result = (ok, text) =>
            {
                if (!ok) { CallErrorLabel.Text = text; CallErrorLabel.IsVisible = true; }
            },
        });
    }

    /// <summary>
    /// Waits for the job just asked for, until the STATE of the phone says it has ended (<see cref="BlindJobWait.Judge"/>): it runs (the worker or service is
    /// in <see cref="BlindActivity"/>, or the status names it), or it is queued by the system and not started yet, and it ends when its mark appears, its
    /// failure is recorded, or - after a grace for the queue - nothing was ever seen of it. There is no limit on how long a running job may take: the
    /// button is not given back while it runs, and after half an hour the pop-up says that it is still running and not to start it again. The job's own
    /// progress reaches the pop-up through <see cref="OnJobProgress"/>.
    /// </summary>
    private async Task<BlindJobOutcome> WaitForJobAsync(BusyProgress busy, BlindJobAsk ask, params string[] kinds)
    {
        _busyProgress = busy;
        try
        {
            var said = false;
            for (;;)
            {
                var now = DateTimeOffset.UtcNow;
                var outcome = BlindJobWait.Judge(ask, _app.GetStatus(), kinds.Any(_activity.IsActive), now);
                if (!outcome.IsOpen) return outcome;
                if (!said && now - ask.At > BlindJobWait.StillRunningAfter)
                {
                    busy.Report(null, BlindJobWait.StillRunningText);
                    said = true;
                }
                await Task.Delay(1000);
            }
        }
        finally
        {
            _busyProgress = null;
        }
    }

    private async void OnRePairClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await DisplayAlertAsync("Re-pair",
                    "This makes a new phone code for adding the phone on a computer again. The current connection stays until the computer's new answer is accepted.",
                    "Make a new code", "Cancel"))
                return;
            // The one busy helper (BMB-311): the button is off and spins until the new code is made; a failure is said in full.
            await BusyAction.RunAsync(sender as Button, () =>
            {
                _app.StartRePair();
                Refresh();
                return Task.CompletedTask;
            }, new BusyOptions { FailureTitle = "Re-pair" });
        }
        catch (Exception)
        {
            await TellAsync("Re-pair", BlindPageSentences.RePairNotStarted);
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the sync round has ended; a
        // failure to even ask is said in full.
        await BusyAction.RunAsync(sender as Button, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.Sync, _app.GetStatus(), DateTimeOffset.UtcNow);
            // This account first (the scheduler's plan), then the system's one-time job, which runs the pass for every account that is due.
            _device.SyncScheduler.RequestSync(_accountId);
#if ANDROID
            Platforms.Android.BlindWorkScheduler.SyncNow(Platform.AppContext);
#endif
            NoticeLabel.Text = "Sync asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Sync);
            NoticeLabel.Text = BlindPageSentences.JobEnd(outcome, BlindJobWait.Sync,
                finished: "Sync finished. The time of the last sync is above, what happened is in the log below.",
                failed: BlindPageSentences.SyncNotFinished,
                ended: "Sync ended. What happened is in the log below.");
            Refresh();
        }, new BusyOptions
        {
            Long = true,
            Title = "Syncing",
            Message = "Please wait while this phone syncs. You can leave the app: the job goes on.",
            FailureTitle = "Sync now",
        });
    }

    private async void OnBackupNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the backup has ended (the
        // button is off at once, a second tap cannot start a second job; the service also ignores a start while its task runs).
        var ran = await BusyAction.RunAsync(sender as Button, async busy =>
        {
            _backupAskedAt = DateTimeOffset.UtcNow;
            var ask = BlindJobWait.Ask(BlindJobWait.Backup, _app.GetStatus(), _backupAskedAt);
#if ANDROID
            // One backup service, one long job on the device at a time. The account is reserved by the call that starts the service, before anything asynchronous happens, so a second account's
            // request cannot slip in while the first is still starting and be merged into it: it is refused here, with the name of the account that holds the service.
            if (!Platforms.Android.BlindBackupService.Start(Platform.AppContext, _accountId, out var refusal))
            {
                _backupAskedAt = DateTimeOffset.MinValue;
                var holderName = _device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == refusal.Holder)?.Entry.Name;
                NoticeLabel.Text = BlindManualBackup.RefusalText(holderName, refusal.Stopping);
                NoticeLabel.IsVisible = true;
                return;
            }
#endif
            NoticeLabel.Text = "Backup asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy, BlindActivity.BackupService);
            NoticeLabel.Text = BlindPageSentences.JobEnd(outcome, BlindJobWait.Backup,
                finished: "Backup finished. The time of the last backup is above, what happened is in the log below.",
                failed: BlindPageSentences.BackupNotFinished,
                ended: "The backup ended. What happened is in the log below.");
        }, new BusyOptions
        {
            Long = true,
            Title = "Backing up",
            Message = "Please wait while the backup is made. You can leave the app: the job goes on.",
            FailureTitle = "Back up now",
        });
        if (!ran) _backupAskedAt = DateTimeOffset.MinValue;
        Refresh();
    }

    private async void OnSaveToClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the file is copied out of the app to a place the person chooses, and the picker waits for them. The one busy helper
        // keeps the screen covered until the file is saved, so nothing else is started meanwhile; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            if (_app.GetStatus().Backups.FirstOrDefault() is not { } latest) return;
#if ANDROID
            // The picker and its cut-off-file cleanup are Android's (SafExport); the file is one of the backup folder, by name.
            var path = Path.Combine(BlindPaths.Backups(_paths.DataDirectory), Path.GetFileName(latest.Name));
            if (await Platforms.Android.SafExport.SaveAsync(path))
            {
                NoticeLabel.Text = $"Saved {latest.Name} to the chosen place.";
                NoticeLabel.IsVisible = true;
            }
#else
            await Task.CompletedTask;
#endif
        }, new BusyOptions
        {
            Long = true,
            Title = "Saving the backup",
            Message = "Please wait while the backup is saved to the place you chose.",
            FailureTitle = "Save to…",
        });
        Refresh();
    }

    private void OnScheduleChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!_applying && SchedulePicker.SelectedIndex >= 0) _app.SetSchedule(Schedules[SchedulePicker.SelectedIndex]);
        }
        catch (Exception)
        {
            ShowProblem();
        }
    }

    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        try
        {
            var account = _device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId);
            if (account is null) return;
            var (title, question, remove, keep) = BlindRemoveConfirmation.Build(account);
            if (!await DisplayAlertAsync(title, question, remove, keep)) return;

            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up until the removal has ended. Off the UI thread: the removal waits
            // (bounded) for a running sync, first load or backup of THIS account to end before it deletes anything; the other accounts go on.
            var incomplete = false;
            var removed = await BusyAction.RunAsync(sender as Button, async () =>
            {
                try
                {
                    await Task.Run(() => _device.Accounts.RemoveAsync(_accountId));
                }
                catch (AggregateException)
                {
                    incomplete = true;
                    throw;
                }
            }, new BusyOptions
            {
                Long = true,
                Title = "Removing the copy",
                Message = "Please wait. A running sync or backup of this account is stopped first, then its copy, its backups and its keys are deleted.",
                Result = (ok, text) =>
                {
                    if (ok) return;
                    // Not everything is gone (the work did not stop in time and nothing was deleted, or a step failed): say what is left and let the user press it again.
                    _ = TellAsync("Remove this account", incomplete
                        ? $"{text}{System.Environment.NewLine}{System.Environment.NewLine}Press Remove this account again to finish."
                        : $"Could not remove: {text}");
                },
            });
            if (removed) CloseSoon();
            else Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Remove this account", $"Could not remove: {BusyAction.Describe(ex)}");
            Refresh();
        }
    }

    private async void OnRenameClicked(object? sender, EventArgs e)
    {
        try
        {
            var current = _device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId)?.Entry.Name ?? "";
            var name = await DisplayPromptAsync("Rename", "What do you call this copy?", "Rename", "Cancel", initialValue: current, maxLength: BlindAccountEntry.MaxNameLength);
            if (name is null) return;
            await BusyAction.RunAsync(sender as Button, () =>
            {
                var refused = _device.Accounts.Rename(_accountId, name);
                if (refused is not null) throw BlindPageSentences.Refusal(refused);
                Refresh();
                return Task.CompletedTask;
            }, new BusyOptions { FailureTitle = "Rename" });
        }
        catch (Exception ex)
        {
            await TellAsync("Rename", BusyAction.Describe(ex));
        }
    }

    private async void OnPauseClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var paused = _device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == _accountId)?.Entry.PausedByUser == true;
            var refused = _device.Accounts.SetPaused(_accountId, !paused);
            if (refused is not null) throw BlindPageSentences.Refusal(refused);
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Pause" });
    }

    private async void OnSaveLimitsClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            // The form parses and checks what was typed ("2 GB", "200 MB", "5000"); an empty box is no limit and 0 is a pause. Its sentences say what to change.
            var result = new BlindLimitsForm(StoredEntry.Text ?? "", TrafficEntry.Text ?? "", RequestsEntry.Text ?? "", UnmeteredSwitch.IsToggled, BatterySwitch.IsToggled).Validate();
            if (!result.Ok)
            {
                LimitsProblemLabel.Text = string.Join(System.Environment.NewLine, result.Problems);
                LimitsProblemLabel.IsVisible = true;
                return Task.CompletedTask;
            }
            var refused = _device.Accounts.SetLimits(_accountId, result.Limits!) ?? _device.Accounts.SetSwitches(_accountId, UnmeteredSwitch.IsToggled, BatterySwitch.IsToggled);
            LimitsProblemLabel.Text = refused;
            LimitsProblemLabel.IsVisible = refused is not null;
            if (refused is null)
            {
                _limitsFilled = false;
                NoticeLabel.Text = "Limits saved.";
                NoticeLabel.IsVisible = true;
            }
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Save limits" });
    }

    private async void OnReconnectClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var refused = _device.Accounts.Reconnect(_accountId);
            if (refused is not null) throw BlindPageSentences.Refusal(refused);
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Try again" });
    }

    private async void OnRepairUsageClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var refused = _device.Accounts.StartNewUsageLedger(_accountId);
            if (refused is not null) throw BlindPageSentences.Refusal(refused);
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Start new counts" });
    }

    private async Task TellAsync(string title, string message)
    {
        try { await DisplayAlertAsync(title, message, "OK"); }
        catch (Exception) { /* the page is closing */ }
    }
}
