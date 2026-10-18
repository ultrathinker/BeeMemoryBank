using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.BlindMobile.Services.Blind;
using QRCoder;

namespace BeeMemoryBank.BlindMobile.Pages;

/// <summary>
/// The blind node's only screen (plan section 10): state, the two pairing codes, backups (now, "Save
/// to…", schedule), the log, and "Disconnect and wipe". There is no unlock: a blind copy has no
/// master password.
/// <para>The page holds no blind logic and never reads a key itself: it shows <see cref="BlindHomeView"/>, built from the status of the
/// shared <see cref="IBlindAppController"/> (the same one the desktop hosts use), whose reads of the AndroidKeyStore cannot throw into the
/// screen. Only what is Android's own stays here: WorkManager and service requests, the file picker, the clipboard, the QR picture. Every
/// callback of the page is guarded: a failure becomes a sentence on the screen, never an unhandled exception on the UI thread.</para>
/// </summary>
public partial class BlindHomePage : ContentPage
{
    private static readonly BlindBackupSchedule[] Schedules = BlindHomeView.Schedules;

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
    private string? _qrFor;
    private string _wipeName = "";
    private DateTimeOffset _backupAskedAt = DateTimeOffset.MinValue;
    private BusyProgress? _busyProgress;   // the pop-up of the long operation now running, if any (the job's progress is shown in it too)

    public BlindHomePage(IBlindAppController app, BlindHeavyWork work, BlindActivity activity, IBlindPaths paths, BlindStoreStats stats)
    {
        InitializeComponent();
        _app = app;
        _work = work;
        _activity = activity;
        _paths = paths;
        _stats = stats;
        SchedulePicker.ItemsSource = new[] { "Off", "Daily", "Weekly" };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _work.Progress += OnJobProgress;
            _app.Changed += OnAppChanged;

            // Opens the database and makes the identity; a failure is kept in the status (StartError), it is not thrown.
            await _app.InitializeAsync();
            Refresh();

            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromSeconds(5);
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
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
            Show(BlindHomeView.Build(status, code, backupRunning, _store), status.DisplayName);
            ReadStoreSoon(status);
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
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
                catch (Exception ex) { ShowProblem(ex); }
            });
        });
    }

    private void Show(BlindHomeView view, string? displayName)
    {
        _wipeName = displayName ?? "";
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
        catch (Exception ex)
        {
            PhoneCodeImage.IsVisible = false;
            PhoneCodeLabel.Text = $"The QR picture could not be made ({ex.Message}). Copy the code with the button below instead.";
        }
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
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    });

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyPhoneCodeClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_app.PairingCode() is { } code) await Clipboard.Default.SetTextAsync(code);
        }
        catch (Exception ex)
        {
            await TellAsync("Copy this phone's code", ex.Message);
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
#if ANDROID
                // Paired: request the first load without waiting for the next hourly slot.
                Platforms.Android.BlindWorkScheduler.RunHeavyNow(Platform.AppContext);
#endif
                // Locked until the first load has ended, or failed, or was never started (the state says which), not until a clock runs out.
                var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy);
                if (outcome.Phase is BlindJobPhase.Failed or BlindJobPhase.Unreported)
                {
                    NoticeLabel.Text = outcome.Phase == BlindJobPhase.Failed ? $"The first load did not finish: {outcome.Message}" : outcome.Message;
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
        catch (Exception ex)
        {
            await TellAsync("Re-pair", ex.Message);
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the sync round has ended; a
        // failure to even ask is said in full.
        await BusyAction.RunAsync(sender as Button, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.Sync, _app.GetStatus(), DateTimeOffset.UtcNow);
#if ANDROID
            Platforms.Android.BlindWorkScheduler.SyncNow(Platform.AppContext);
#endif
            NoticeLabel.Text = "Sync asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Sync);
            NoticeLabel.Text = outcome.Phase == BlindJobPhase.Finished
                ? "Sync finished. The time of the last sync is above, what happened is in the log below."
                : outcome.Message ?? "Sync ended. What happened is in the log below.";
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
            Platforms.Android.BlindBackupService.Start(Platform.AppContext);
#endif
            NoticeLabel.Text = "Backup asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy, BlindActivity.BackupService);
            NoticeLabel.Text = outcome.Phase switch
            {
                BlindJobPhase.Finished => "Backup finished. The time of the last backup is above, what happened is in the log below.",
                BlindJobPhase.Failed => $"The backup did not finish: {outcome.Message}",
                _ => outcome.Message ?? "The backup ended. What happened is in the log below.",
            };
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
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    private async void OnWipeClicked(object? sender, EventArgs e)
    {
        try
        {
            var name = _wipeName;
            var typed = await DisplayPromptAsync("Disconnect and wipe",
                $"This deletes the blind copy from this phone. Type its name \"{name}\" to confirm.",
                "Wipe", "Cancel");
            if (typed == null) return;
            if (typed.Trim() != name)
            {
                await DisplayAlertAsync("Disconnect and wipe", "The name does not match. Nothing was deleted.", "OK");
                return;
            }
            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up until the wipe has ended. Off the UI thread: the
            // wipe waits (bounded) for a running sync, first load or backup to end before it deletes anything.
            var incomplete = false;
            await BusyAction.RunAsync(sender as Button, async () =>
            {
                try
                {
                    await Task.Run(() => _app.DisconnectAndWipeAsync());
                }
                catch (AggregateException)
                {
                    incomplete = true;
                    throw;
                }
            }, new BusyOptions
            {
                Long = true,
                Title = "Disconnecting and wiping",
                Message = "Please wait. A running sync or backup is stopped first, then this copy, its backups and its keys are deleted.",
                Result = (ok, text) =>
                {
                    if (ok) return;
                    // Not restarted over a half-wiped copy (or the work did not stop in time and nothing was deleted): say what is left and let
                    // the user press it again.
                    _ = TellAsync("Disconnect and wipe", incomplete
                        ? $"{text}{System.Environment.NewLine}{System.Environment.NewLine}Press Disconnect and wipe again to finish."
                        : $"Could not wipe: {text}");
                },
            });
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Disconnect and wipe", $"Could not wipe: {ex.Message}");
            Refresh();
        }
    }

    private async Task TellAsync(string title, string message)
    {
        try { await DisplayAlertAsync(title, message, "OK"); }
        catch (Exception) { /* the page is closing */ }
    }
}
