using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using QRCoder;
using UIKit;

namespace BeeMemoryBank.BlindIos.Pages;

/// <summary>
/// The iPhone blind copy's only screen, the Android one's counterpart: state (with the number of notes, the last contact and the newest
/// problem), the two pairing codes, backups, the log and "Disconnect and wipe". There is no unlock and no password field anywhere: a blind
/// copy has no master password.
/// <para>The page holds no blind logic and reads no key: it shows <see cref="BlindHomeView"/>, built from the status of the shared
/// <see cref="IBlindAppController"/>, and asks the in-app loop for jobs. What is iOS's own stays here: the share sheet, the clipboard, the
/// background-refresh state. The controller is always the CURRENT runtime's (a wipe replaces it). Every callback is guarded: a failure
/// becomes a sentence on the screen, never an unhandled exception on the UI thread.</para>
/// </summary>
public partial class BlindHomePage : ContentPage
{
    private static readonly BlindBackupSchedule[] Schedules = BlindHomeView.Schedules;
    private static readonly TimeSpan NotesEvery = TimeSpan.FromSeconds(15);

    private readonly IosBlindHost _host;
    private readonly IosSilenceNotifier _silence;
    private IosBlindRuntime? _bound;
    private IDispatcherTimer? _timer;
    private bool _applying;
    private bool _refreshPending;
    private bool? _wasLoaded;
    private string? _qrFor;
    private string _wipeName = "";
    private DateTimeOffset _backupAskedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _notesAt = DateTimeOffset.MinValue;
    private long? _notes;
    private bool _countingNotes;
#if DEBUG || BMB_E2E
    private bool _e2eCodeUsed;
#endif

    public BlindHomePage(IosBlindHost host, IosSilenceNotifier silence)
    {
        InitializeComponent();
        _host = host;
        _silence = silence;
        SchedulePicker.ItemsSource = new[] { "Off", "Daily", "Weekly" };
    }

    private IosBlindRuntime Runtime => _host.Current;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _host.Replaced += OnRuntimeReplaced;
            await BindAsync();

            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromSeconds(5);
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();

            if (App.TakePendingLink() is { } link) AcceptLink(link);
#if DEBUG || BMB_E2E
            // End-to-end checks on a simulator or a development iPhone (tools/ios-e2e): the computer's answer is handed over at launch,
            // because a script cannot tap the "Open in ...?" prompt iOS shows for a link. Accepted exactly like a pasted code. Only in a
            // Debug build or one made with -p:BmbE2E=true; a Release build for people does not contain it (IosBoundaryTests).
            if (!_e2eCodeUsed && Environment.GetEnvironmentVariable("BMB_BLIND_E2E_CALL_CODE") is { Length: > 0 } e2eCode)
            {
                _e2eCodeUsed = true;
                AcceptLink(e2eCode);
            }
            // On a real iPhone there is no screenshot to read the QR code from: the check starts the app with its console attached
            // (xcrun devicectl ... --console) and reads the phone code there. The code is on the screen anyway; this only repeats it to
            // the developer tool that asked for it.
            if (Environment.GetEnvironmentVariable("BMB_BLIND_E2E_PRINT_PHONE_CODE") == "1" && Runtime.App.PairingCode() is { } shown)
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
        _host.Replaced -= OnRuntimeReplaced;
        Unbind();
        _timer?.Stop();
        base.OnDisappearing();
    }

    /// <summary>Listens to the current runtime, opens its database and makes its identity if there is none (a failure is in the status).</summary>
    private async Task BindAsync()
    {
        Unbind();
        var runtime = Runtime;
        _bound = runtime;
        _wasLoaded = null;
        _notesAt = DateTimeOffset.MinValue;
        runtime.App.Changed += OnAppChanged;
        runtime.Scheduler.UserJobReported += OnUserJobReported;
        await runtime.App.InitializeAsync();
        Refresh();
    }

    private void Unbind()
    {
        if (_bound is not { } old) return;
        old.App.Changed -= OnAppChanged;
        old.Scheduler.UserJobReported -= OnUserJobReported;
        _bound = null;
    }

    /// <summary>A wipe built the first-run composition: the screen moves over to it and shows the new phone code.</summary>
    private void OnRuntimeReplaced() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            _qrFor = null;
            await BindAsync();
            Runtime.Scheduler.EnsureScheduled();
            Notice("The copy was wiped. This iPhone has a new identity and a new code; pair it again to use it.");
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    });

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

    private void OnUserJobReported(string sentence) => MainThread.BeginInvokeOnMainThread(() => Notice(sentence));

    /// <summary>Reads the status again and shows it. Never throws: the 5-second timer and every handler call it.</summary>
    private void Refresh()
    {
        try
        {
            var runtime = Runtime;
            var status = runtime.App.GetStatus();
            var code = status.AwaitingAnswer ? runtime.App.PairingCode() : null;
            var backupRunning = runtime.Activity.IsActive(BlindActivity.Heavy) || DateTimeOffset.UtcNow - _backupAskedAt < TimeSpan.FromSeconds(10);
            Show(BlindHomeView.Build(status, code, backupRunning), status);
            FollowFirstLoad(runtime, status);
            CountNotesSoon(runtime, status);
            _silence.Update(status);
        }
        catch (Exception ex)
        {
            ShowProblem(ex);
        }
    }

    private void Show(BlindHomeView view, BlindAppStatus status)
    {
        _wipeName = status.DisplayName ?? "";
        StatusErrorLabel.IsVisible = false;
        NameLabel.Text = view.Name;
        NodeLabel.Text = view.Node;
        PairLabel.Text = view.Pair;
        LoadLabel.Text = view.Load;
        NotesLabel.Text = BlindHomeView.NotesText(_notes, status);
        SyncLabel.Text = IosHomeLines.LastContact(status, DateTimeOffset.UtcNow);
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

        var problem = IosHomeLines.LastProblem(status);
        FailureLabel.Text = problem;
        FailureLabel.IsVisible = problem != null;
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
        var round = IosLastRound.Describe(Runtime.Services.GetService(typeof(IBlindStateStore)) is IBlindStateStore store ? store.Get(IosBlindBackground.LastRoundKey) : null);
        BackgroundLabel.Text = refresh != UIBackgroundRefreshStatus.Available
            ? $"Background App Refresh is off for this app ({refresh}): the copy syncs only while it is open. Settings > General > Background App Refresh turns it on.\n{round}"
            : lowPower
                ? $"Low Power Mode is on: iOS gives the copy no background time until it is off.\n{round}"
                : round;

        var warning = IosHomeLines.SilenceWarning(status, _silence.NextWarning, _silence.Allowed);
        AlertsLabel.Text = warning;
        AlertsLabel.IsVisible = warning != null;
        AlertsLabel.TextColor = _silence.Allowed == false ? (Color)Application.Current!.Resources["WarningColor"] : (Color)Application.Current!.Resources["SecondaryTextColor"];
    }

    private static bool NSProcessInfoLowPower() => Foundation.NSProcessInfo.ProcessInfo.LowPowerModeEnabled;

    /// <summary>The first load just finished: a sync round at once, so that "last contact" and the notes do not wait for the next slot.</summary>
    private void FollowFirstLoad(IosBlindRuntime runtime, BlindAppStatus status)
    {
        if (_wasLoaded == false && status.InitialLoadDone && status.IsPaired)
        {
            runtime.Scheduler.RequestSync();
            _notesAt = DateTimeOffset.MinValue;
        }
        _wasLoaded = status.InitialLoadDone;
    }

    /// <summary>Counts the notes now and then, off the UI thread; the line shows the last count.</summary>
    private void CountNotesSoon(IosBlindRuntime runtime, BlindAppStatus status)
    {
        if (_countingNotes || DateTimeOffset.UtcNow - _notesAt < NotesEvery) return;
        if (!status.InitialLoadDone) { _notes = null; return; }
        _countingNotes = true;
        _notesAt = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            var count = await runtime.Stats.CountNotesAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _countingNotes = false;
                if (!ReferenceEquals(runtime, Runtime)) return;
                _notes = count;
                try { NotesLabel.Text = BlindHomeView.NotesText(count, runtime.App.GetStatus()); }
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
            PhoneCodeLabel.Text = $"The QR picture could not be made ({ex.Message}). Copy the code with the button below instead.";
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

    /// <summary>A bmb-blind-call: link the app was opened with (the Camera scanned the computer's QR code).</summary>
    public void AcceptLink(string url)
    {
        CallCodeEntry.Text = "";
        if (Accept(url)) Notice("Connected with the code from the link. The first load starts now.");
    }

    /// <summary>The computer's answer, pasted or from a link: accepted only when it was made for this phone's current pairing secret.</summary>
    private bool Accept(string text)
    {
        try
        {
            var runtime = Runtime;
            var error = runtime.App.AcceptCallCode(text);
            CallErrorLabel.Text = error;
            CallErrorLabel.IsVisible = error != null;
            if (error == null)
            {
                CallCodeEntry.Text = "";
                // Paired: the first load at once, not at the next hourly slot.
                runtime.Scheduler.EnsureScheduled();
                runtime.Scheduler.RequestHeavy();
            }
            Refresh();
            return error == null;
        }
        catch (Exception ex)
        {
            CallErrorLabel.Text = $"Could not connect: {ex.Message}";
            CallErrorLabel.IsVisible = true;
            return false;
        }
    }

    private async void OnCopyPhoneCodeClicked(object? sender, EventArgs e)
    {
        try
        {
            if (Runtime.App.PairingCode() is { } code)
            {
                await Clipboard.Default.SetTextAsync(code);
                Notice("The code is on the clipboard. Paste it only on your own computer.");
            }
        }
        catch (Exception ex)
        {
            await TellAsync("Copy this iPhone's code", ex.Message);
        }
    }

    private async void OnPasteClicked(object? sender, EventArgs e)
    {
        try
        {
            CallCodeEntry.Text = (await Clipboard.Default.GetTextAsync())?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            await TellAsync("Paste", ex.Message);
        }
    }

    private void OnConnectClicked(object? sender, EventArgs e) => Accept(CallCodeEntry.Text ?? "");

    private async void OnRePairClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!await DisplayAlertAsync("Re-pair",
                    "This makes a new code for adding the iPhone on a computer again. The current connection stays until the computer's new answer is accepted.",
                    "Make a new code", "Cancel"))
                return;
            Runtime.App.StartRePair();
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
            var scheduler = Runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestSync();
            Notice("Sync asked for.");
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
            // Off at once: a second tap cannot ask for a second job while the first one starts (the loop runs one job at a time anyway).
            _backupAskedAt = DateTimeOffset.UtcNow;
            BackupNowButton.IsEnabled = false;
            var scheduler = Runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestBackup();
            Notice("Backup asked for. Keep the app open until it is done; iOS pauses it in the background.");
            Refresh();
        }
        catch (Exception ex)
        {
            _backupAskedAt = DateTimeOffset.MinValue;
            await TellAsync("Back up now", ex.Message);
            Refresh();
        }
    }

    /// <summary>The share sheet ("Save to Files", AirDrop, another app) with the newest backup file of the backup folder, by name.</summary>
    private async void OnSaveToClicked(object? sender, EventArgs e)
    {
        try
        {
            var runtime = Runtime;
            if (runtime.App.GetStatus().Backups.FirstOrDefault() is not { } latest) return;
            var path = Path.Combine(BlindPaths.Backups(runtime.Paths.DataDirectory), Path.GetFileName(latest.Name));
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Save the blind copy's backup", File = new ShareFile(path) });
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
            if (!_applying && SchedulePicker.SelectedIndex >= 0) Runtime.App.SetSchedule(Schedules[SchedulePicker.SelectedIndex]);
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
                $"This deletes the blind copy from this iPhone. Type its name \"{name}\" to confirm.",
                "Wipe", "Cancel");
            if (typed == null) return;
            if (typed.Trim() != name)
            {
                await DisplayAlertAsync("Disconnect and wipe", "The name does not match. Nothing was deleted.", "OK");
                return;
            }
            // Off the UI thread: the wipe waits (bounded) for a running sync, first load or backup to end before it deletes anything; when it
            // succeeds, the host puts the first-run composition in place and the screen moves to it (OnRuntimeReplaced).
            var app = Runtime.App;
            await Task.Run(() => app.DisconnectAndWipeAsync());
        }
        catch (AggregateException ex)
        {
            await TellAsync("Disconnect and wipe", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Press Disconnect and wipe again to finish.");
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
