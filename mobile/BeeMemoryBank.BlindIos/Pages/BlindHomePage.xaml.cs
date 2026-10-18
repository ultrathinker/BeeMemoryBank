using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Busy;
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
    private static readonly TimeSpan StoreEvery = TimeSpan.FromSeconds(15);

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
    private DateTimeOffset _storeAt = DateTimeOffset.MinValue;
    private BlindStoreInfo? _store;
    private bool _readingStore;
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
        _storeAt = DateTimeOffset.MinValue;
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
            Show(BlindHomeView.Build(status, code, backupRunning, _store), status);
            FollowFirstLoad(runtime, status);
            ReadStoreSoon(runtime, status);
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
        StoreLabel.Text = view.Store;
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
            _storeAt = DateTimeOffset.MinValue;
        }
        _wasLoaded = status.InitialLoadDone;
    }

    /// <summary>Reads what the copy holds now and then, off the UI thread (docs/design/step-a-spec.md 10.2); the line shows the last answer.</summary>
    private void ReadStoreSoon(IosBlindRuntime runtime, BlindAppStatus status)
    {
        if (_readingStore || DateTimeOffset.UtcNow - _storeAt < StoreEvery) return;
        if (!status.InitialLoadDone) { _store = null; return; }
        _readingStore = true;
        _storeAt = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            var summary = await runtime.Stats.GetAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _readingStore = false;
                if (!ReferenceEquals(runtime, Runtime)) return;
                _store = summary;
                try { StoreLabel.Text = BlindHomeView.StoreText(summary, runtime.App.GetStatus(), DateTimeOffset.UtcNow); }
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

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
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

    // busy-exempt: pastes from the clipboard, nothing is sent and nothing waits
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
            var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, Runtime.App.GetStatus(), DateTimeOffset.UtcNow);
            if (!Accept(code)) return;
            if (fromLink) Notice("Connected with the code from the link. The first load starts now.");
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy);
            if (outcome.Phase == BlindJobPhase.Failed) Notice($"The first load did not finish: {outcome.Message}");
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
            var runtime = Runtime;
            var now = DateTimeOffset.UtcNow;
            var outcome = BlindJobWait.Judge(ask, runtime.App.GetStatus(), kinds.Any(runtime.Activity.IsActive), now);
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
                Runtime.App.StartRePair();
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
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up from the tap until the state of the phone says the sync has
        // ended (it has no time limit: the button is not given back while the round runs); a failure to even ask is said in full.
        await BusyAction.RunAsync(sender as Button, async busy =>
        {
            var ask = BlindJobWait.Ask(BlindJobWait.Sync, Runtime.App.GetStatus(), DateTimeOffset.UtcNow);
            var scheduler = Runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestSync();
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
            var ask = BlindJobWait.Ask(BlindJobWait.Backup, Runtime.App.GetStatus(), _backupAskedAt);
            var scheduler = Runtime.Scheduler;
            scheduler.EnsureScheduled();
            scheduler.RequestBackup();
            Notice("Backup asked for. Keep the app open until it is done; iOS pauses it in the background.");
            Refresh();
            var outcome = await WaitForJobAsync(busy, ask, BlindActivity.Heavy, BlindActivity.BackupService);
            if (outcome.Phase == BlindJobPhase.Failed) Notice($"The backup did not finish: {outcome.Message}");
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
            var runtime = Runtime;
            if (runtime.App.GetStatus().Backups.FirstOrDefault() is not { } latest) return;
            var path = Path.Combine(BlindPaths.Backups(runtime.Paths.DataDirectory), Path.GetFileName(latest.Name));
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
            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up until the wipe has ended.
            var app = Runtime.App;
            var incomplete = false;
            await BusyAction.RunAsync(sender as Button, async () =>
            {
                try
                {
                    await Task.Run(() => app.DisconnectAndWipeAsync());
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
                    _ = TellAsync("Disconnect and wipe", incomplete
                        ? $"{text}{Environment.NewLine}{Environment.NewLine}Press Disconnect and wipe again to finish."
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
