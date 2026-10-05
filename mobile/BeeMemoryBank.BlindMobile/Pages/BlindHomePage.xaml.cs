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
    private IDispatcherTimer? _timer;
    private bool _applying;
    private bool _refreshPending;
    private string? _qrFor;
    private string _wipeName = "";
    private DateTimeOffset _backupAskedAt = DateTimeOffset.MinValue;

    public BlindHomePage(IBlindAppController app, BlindHeavyWork work, BlindActivity activity, IBlindPaths paths)
    {
        InitializeComponent();
        _app = app;
        _work = work;
        _activity = activity;
        _paths = paths;
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
            Show(BlindHomeView.Build(status, code, backupRunning), status.DisplayName);
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    private void Show(BlindHomeView view, string? displayName)
    {
        _wipeName = displayName ?? "";
        StatusErrorLabel.IsVisible = false;
        NameLabel.Text = view.Name;
        NodeLabel.Text = view.Node;
        PairLabel.Text = view.Pair;
        LoadLabel.Text = view.Load;
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
            JobProgress.IsVisible = JobLabel.IsVisible = progress < 1;
            JobProgress.Progress = progress;
            JobLabel.Text = $"{title}: {progress:P0}";
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    });

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
        try
        {
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
            }
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Connect", ex.Message);
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
            _app.StartRePair();
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Re-pair", ex.Message);
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        try
        {
#if ANDROID
            Platforms.Android.BlindWorkScheduler.SyncNow(Platform.AppContext);
#endif
            NoticeLabel.Text = "Sync asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Sync now", ex.Message);
        }
    }

    private async void OnBackupNowClicked(object? sender, EventArgs e)
    {
        try
        {
            // Off at once and until the service shows up in BlindActivity: a second tap cannot start a second job (the service
            // also ignores a start while its task runs).
            _backupAskedAt = DateTimeOffset.UtcNow;
            BackupNowButton.IsEnabled = false;
#if ANDROID
            Platforms.Android.BlindBackupService.Start(Platform.AppContext);
#endif
            NoticeLabel.Text = "Backup asked for.";
            NoticeLabel.IsVisible = true;
            Refresh();
        }
        catch (Exception ex)
        {
            _backupAskedAt = DateTimeOffset.MinValue;
            await TellAsync("Back up now", ex.Message);
            Refresh();
        }
    }

    private async void OnSaveToClicked(object? sender, EventArgs e)
    {
        try
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
#endif
        }
        catch (Exception ex)
        {
            await TellAsync("Save to…", ex.Message);
        }
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
            // Off the UI thread: the wipe waits (bounded) for a running sync, first load or backup to end before it deletes anything.
            await Task.Run(() => _app.DisconnectAndWipeAsync());
        }
        catch (AggregateException ex)
        {
            // Not restarted over a half-wiped copy (or the work did not stop in time and nothing was deleted): say what is left and let
            // the user press it again.
            await TellAsync("Disconnect and wipe", $"{ex.Message}{System.Environment.NewLine}{System.Environment.NewLine}Press Disconnect and wipe again to finish.");
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
