using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop;

public partial class MainWindow : Window
{
    private readonly Services.NodeLifecycleService _nodeLifecycle = new();
    private readonly BeeMemoryBank.Profiles.ProfileService _profiles =
        new(BeeMemoryBank.AppPaths.BmbPaths.ProfilesFile);
    // §4.4/§4.5: ProfileSwitchService is a single per-app instance sharing the SAME
    // NodeLifecycleService that hosts the current node. Only the instance that spawned the
    // process can stop it gracefully, so passing a different _nodeLifecycle here would break
    // the ownership-aware stop inside SwitchToAsync.
    private readonly Services.ProfileSwitchService _profileSwitch;
    private bool _isRealClose;
    private CancellationTokenSource? _initCts;
    // Created ONCE and only ever cancelled on window close - NOT recreated per switch call.
    // ProfileSwitchService already single-flight-rejects a second concurrent SwitchToAsync
    // call, so a "cancel the previous switch's token" pattern here would actively fight that:
    // cancelling this shared token from a NEW SwitchProfileAsync call would cancel whatever
    // an EARLIER, still-in-flight call (e.g. its own revert-to-previous-profile attempt) is
    // doing with the SAME token, aborting it mid-operation instead of letting the service's
    // own gate reject the new call cleanly.
    private readonly CancellationTokenSource _switchLifetimeCts = new();
    // The request the power-events services make when the computer goes to sleep (see LockNodeOnSleepAsync). Set in the constructor,
    // where _nodeLifecycle exists.
    private readonly Services.NodeLockRequest _lockOnSleep;
    // The poll of the open node's blind-node alarms (BMB-77) and what turns them into notifications; started with the first node.
    private readonly Services.NodeAlarmsRequest _alarmsRequest;
    private Services.BlindAlarmWatcher? _blindAlarms;
    private Services.IUserNotifier? _notifier;

    /// <summary>
    /// "Lock the vault when this computer sleeps" (off by default, per app like the other flags in desktop-settings.json). The sleep
    /// monitors ask it at each sleep, so the settings window changing it takes effect at once.
    /// </summary>
    public Services.LockOnSleepSetting LockOnSleep { get; } = new(new Services.DesktopSettingsStore());
    private bool _startMinimized = Program.StartMinimized;
    private string? _frontUrl;
    private string? _activeProfileId;
    private Services.IPowerEventsService? _powerEventsService;

    public string? FrontUrl => _frontUrl;

    /// <summary>
    /// The profile id currently bound to the running node + WebView, or null before the first
    /// successful start. Read by the tray menu (App.axaml.cs) to mark the active entry and by
    /// the title/tooltip formatter.
    /// </summary>
    public string? ActiveProfileId => _activeProfileId;

    /// <summary>
    /// Exposes the profile registry so App.axaml.cs / dialogs can enumerate/rename/forget
    /// without each creating their own ProfileService (which would be a different in-memory
    /// cache over the same file — fine for atomic ops, but a needless second source of truth
    /// for "active profile" lookups).
    /// </summary>
    public BeeMemoryBank.Profiles.ProfileService Profiles => _profiles;

    /// <summary>
    /// Raised on the UI thread whenever the active profile changes (initial start success,
    /// successful switch, or switch failure that reverted to another profile). Subscribers
    /// (the tray menu) use it to refresh the radio-checkmark next to the now-active profile
    /// and to rebuild the title/tooltip text per §4.5.
    /// </summary>
    public event EventHandler? ActiveProfileChanged;

    /// <summary>How many blind nodes need attention now (the tray tooltip says so); 0 before the first node started.</summary>
    public int BlindAlarmCount => _blindAlarms?.AttentionCount ?? 0;

    /// <summary>Raised, on a background thread, when <see cref="BlindAlarmCount"/> changes.</summary>
    public event EventHandler? BlindAlarmsChanged;

    public MainWindow()
    {
        // ProfileSwitchService must be constructed AFTER _profiles and _nodeLifecycle are
        // initialized (field initializers run in declaration order — _profileSwitch is
        // declared last, so this is safe). It uses the same _nodeLifecycle instance that
        // HostOrAttachAsync will later call into, which is the contract it relies on.
        _profileSwitch = new Services.ProfileSwitchService(_profiles, _nodeLifecycle);
        // The key is the one NodeLifecycleService generated for the node it STARTED (it also sets it in this process's environment).
        // A node this app merely attached to is not its to authenticate to: the environment may still hold the key of a node it
        // hosted before, so the attached case is excluded here rather than sending a stale key.
        _lockOnSleep = new Services.NodeLockRequest(
            () => _frontUrl,
            () => _nodeLifecycle.IsAttachedToExternalNode ? null : Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY"));
        // The same address and the same key rule as the lock request: a node this app merely attached to is not asked.
        _alarmsRequest = new Services.NodeAlarmsRequest(
            () => _frontUrl,
            () => _nodeLifecycle.IsAttachedToExternalNode ? null : Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY"));
        InitializeComponent();
        BmbWebView.EnvironmentRequested += OnWebViewEnvironmentRequested;
        Opened += MainWindow_Opened;
    }

    /// <summary>
    /// WebView2 keeps its profile next to the exe by default, i.e. inside Velopack's current    /// folder, which every update replaces: the profile would be lost on each update, and its
    /// files, held open by lingering msedgewebview2 processes, can block the swap. Keep it in
    /// the data root instead.
    /// </summary>
    private static void OnWebViewEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs webView2)
            webView2.UserDataFolder = System.IO.Path.Combine(BeeMemoryBank.AppPaths.BmbPaths.Root, "webview2");

        // WKWebView (the Mac app) has no window.chrome.webview for the Setup page to recognise the app by, so it is
        // named in the user agent instead. That is what shows the page's shell-only buttons (the native pickers).
        if (e is Avalonia.Platform.AppleWKWebViewEnvironmentRequestedEventArgs apple)
            apple.ApplicationNameForUserAgent = BeeMemoryBank.Hosting.DesktopShellCommands.UserAgentToken;
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_startMinimized)
        {
            _startMinimized = false;
            Hide();
        }
        StartHostOrAttach();
    }

    private void StartHostOrAttach()
    {
        _initCts?.Cancel();
        _initCts = new CancellationTokenSource();

        SplashPanel.IsVisible = true;
        ErrorPanel.IsVisible = false;
        WebPanel.IsVisible = false;
        StatusText.Text = "Initializing...";

        var token = _initCts.Token;
        Task.Run(() => HostOrAttachAsync(token), token);
    }

    private async Task HostOrAttachAsync(CancellationToken token)
    {
        // The lifecycle service is UI-agnostic: it reports textual progress and returns a
        // plain result. Everything below (Dispatcher.UIThread.Post, UpdateStatus, ShowError,
        // WebView wiring, panel switching) stays in MainWindow - the behavior is identical to
        // the original inlined implementation.
        //
        // Which profile to start is autostartMode/lastUsed-driven, not the hardcoded
        // default vault - a single-profile installation still resolves to "default" via
        // ProfileService's own first-run fallback, so behavior is unchanged when there is
        // only one profile.
        var profile = Services.AutostartProfileResolver.Resolve(_profiles);
        var progress = new Progress<string>(UpdateStatus);
        var result = await _nodeLifecycle.StartOrAttachAsync(profile.DataPath, progress, token);

        Dispatcher.UIThread.Post(() =>
        {
            // The rescue found conflicting data and copied it into a fresh, unregistered
            // recovered-<date> vault instead of the default one. Register a profile for it now
            // so it shows up in Manage Storages instead of sitting invisible on disk - this can
            // be set REGARDLESS of whether the node start itself went on to succeed, since the
            // rescue already physically happened before the start attempt.
            if (!string.IsNullOrEmpty(result.RecoveredVaultDir))
            {
                try
                {
                    var recoveredName = $"Recovered {DateTime.Now:yyyy-MM-dd HH:mm}";
                    _profiles.AddProfile(recoveredName, result.RecoveredVaultDir);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to register recovered vault as a profile: {ex.Message}");
                }
            }

            if (result.Success && !string.IsNullOrEmpty(result.FrontUrl))
            {
                ApplySuccessfulNodeStart(profile, result.FrontUrl!);
                try { _profiles.SetLastUsed(profile.Id); }
                catch (Exception ex) { Debug.WriteLine($"Failed to record last-used profile: {ex.Message}"); }
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Unknown error.");
            }
        });
    }

    /// <summary>
    /// Wires the WebView to a freshly-started node and flips the panels to the Web view.
    /// Shared between the first-launch path (<see cref="HostOrAttachAsync"/>) and a profile
    /// switch (<see cref="SwitchProfileAsync"/>) so the two never drift in how they install
    /// the origin-lock handlers or set _activeProfileId/_frontUrl. MUST run on the UI thread.
    /// </summary>
    private void ApplySuccessfulNodeStart(
        BeeMemoryBank.Profiles.ProfileEntry profile, string frontUrl)
    {
        // Subscribe BEFORE assigning Source: the origin-lock handlers must be in place before
        // the very first navigation happens, otherwise a tampered .runtime.json that passed
        // the loose /node/status probe could navigate once, unguarded, before these handlers
        // ever attach. On a switch the handlers are already attached from profile A, but the
        // unsubscribe/subscribe pair is idempotent and keeps the contract explicit.
        _frontUrl = frontUrl;
        _activeProfileId = profile.Id;
        BmbWebView.NavigationStarted -= OnWebViewNavigationStarted;
        BmbWebView.NavigationStarted += OnWebViewNavigationStarted;
        BmbWebView.NewWindowRequested -= OnWebViewNewWindowRequested;
        BmbWebView.NewWindowRequested += OnWebViewNewWindowRequested;
        BmbWebView.Source = new Uri(frontUrl);
        StartPowerEventsMonitoring();
        StartBlindAlarmWatcher();

        SplashPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        WebPanel.IsVisible = true;

        UpdateShellTitle();
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Switches the active profile via <see cref="Services.ProfileSwitchService"/>. Reuses the
    /// splash panel (exactly like a cold start) so the user gets continuous progress feedback
    /// while the old node stops and the new one comes up. Called from the tray menu and from
    /// the create-storage dialog (a freshly-created profile is switched to as if the user had
    /// picked it — the empty data dir then meets the existing /Setup wizard, nothing
    /// special).
    /// </summary>
    public async Task SwitchProfileAsync(string targetProfileId)
    {
        if (string.IsNullOrWhiteSpace(targetProfileId))
        {
            return;
        }

        // No-op if already on the target: avoids a pointless stop+start of the running node,
        // and avoids the update-in-progress guard firing on the active node for nothing.
        if (string.Equals(_activeProfileId, targetProfileId, StringComparison.Ordinal))
        {
            return;
        }

        var ct = _switchLifetimeCts.Token;

        // Reuse the splash panel as the "switching" view (same visual as a cold start).
        WebPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SplashPanel.IsVisible = true;
        StatusText.Text = "Switching profile...";

        var progress = new Progress<string>(UpdateStatus);
        var cookieClearer = new Services.NativeWebViewCookieClearer(BmbWebView);

        Services.SwitchResult result;
        try
        {
            result = await _profileSwitch.SwitchToAsync(
                targetProfileId, _activeProfileId, _frontUrl, cookieClearer, progress, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowError($"Profile switch failed: {ex.Message}");
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The switch service already handled revert-to-A on B-start failure, so even on
        // result.Success==false the active profile may have CHANGED (back to A). Always fire
        // the event so the tray menu re-reads _activeProfileId and the title re-formats.
        //
        // Profile+FrontUrl are populated on BOTH a genuine success AND a successful revert
        // (SwitchResult.Reverted) - re-wire the WebView in both cases. Using the CALLER's own
        // stale _frontUrl instead (as this used to) is wrong: bmbd falls back to an
        // OS-assigned port when its preferred one is taken, so a reverted node may now be
        // listening somewhere else entirely.
        if (result.Profile != null && !string.IsNullOrEmpty(result.FrontUrl))
        {
            ApplySuccessfulNodeStart(result.Profile, result.FrontUrl!);
            if (!result.Success)
            {
                // Reverted, not a genuine success - the app is usable again but the
                // REQUESTED switch did not happen. No dedicated toast UI exists yet;
                // log so this is at least diagnosable.
                Debug.WriteLine($"Profile switch reverted: {result.ErrorMessage}");
            }
        }
        else
        {
            ShowError(result.ErrorMessage ?? "Unknown error.");
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Moves a profile's data folder (see <see cref="Services.ProfileSwitchService.RelocateAsync"/>).
    /// For the active profile the splash panel covers the WebView while its node is stopped,
    /// copied and started again; for any other profile nothing visible changes here.
    /// </summary>
    public async Task<Services.RelocateResult> MoveProfileAsync(string profileId, string newDataPath)
    {
        var isActive = string.Equals(_activeProfileId, profileId, StringComparison.Ordinal);
        if (isActive)
        {
            WebPanel.IsVisible = false;
            ErrorPanel.IsVisible = false;
            SplashPanel.IsVisible = true;
            StatusText.Text = "Moving profile...";
        }

        Services.RelocateResult result;
        try
        {
            result = await _profileSwitch.RelocateAsync(
                profileId, newDataPath, _activeProfileId, new Progress<string>(UpdateStatus), _switchLifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return Services.RelocateResult.Refused("The move was cancelled.");
        }
        catch (Exception ex)
        {
            result = Services.RelocateResult.Error(ex.Message);
        }

        if (isActive)
        {
            if (result.Profile != null && !string.IsNullOrEmpty(result.FrontUrl))
            {
                ApplySuccessfulNodeStart(result.Profile, result.FrontUrl!);
            }
            else if (result.Rejected)
            {
                // Rejected before anything was stopped: the node is still up, just show it again.
                SplashPanel.IsVisible = false;
                WebPanel.IsVisible = true;
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Unknown error.");
            }
        }

        NotifyProfilesChanged();
        return result;
    }

    /// <summary>
    /// Re-keys the ACTIVE profile's vault (<see cref="Services.DesktopRekeyService"/>): the splash panel covers the
    /// WebView while the node is stopped, the verb runs and the node starts again; on success the WebView opens the
    /// report page. Only the active profile, since the verb needs its node stopped and started again.
    /// </summary>
    public async Task<Services.DesktopRekeyResult> RekeyActiveProfileAsync(string password)
    {
        var profileId = _activeProfileId ?? throw new InvalidOperationException("No profile is open.");
        var profile = _profiles.GetById(profileId);

        WebPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SplashPanel.IsVisible = true;
        StatusText.Text = "Re-keying the vault...";

        Services.DesktopRekeyResult result;
        try
        {
            result = await new Services.DesktopRekeyService(_nodeLifecycle).RunAsync(
                profile.DataPath, password, nodeIsRunning: true, progress: null, new Progress<string>(UpdateStatus), _switchLifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new Services.DesktopRekeyResult { Outcome = Services.RekeyOutcome.NotRun, Message = "The re-key was cancelled." };
        }

        if (result.FrontUrl != null)
        {
            ApplySuccessfulNodeStart(profile, result.FrontUrl);
            if (result.ReportUrl != null) BmbWebView.Source = new Uri(result.ReportUrl);
        }
        else if (result.Outcome == Services.RekeyOutcome.NotRun)
        {
            SplashPanel.IsVisible = false;
            WebPanel.IsVisible = true;
        }
        else
        {
            ShowError($"{result.Message}\n\nThe node did not start again: {result.StartError}");
        }
        return result;
    }

    /// <summary>
    /// "Open an existing profile" from the first-run wizard: pick a folder that holds a vault
    /// and make the active (still empty) profile use it. A folder that another profile already
    /// uses is simply switched to.
    /// </summary>
    private async Task OpenExistingProfileFolderAsync()
    {
        var activeId = _activeProfileId;
        if (string.IsNullOrEmpty(activeId)) return;

        var folder = await Views.FolderPicker.PickAsync(this, "Choose the folder of your Bee Memory Bank profile or backup");
        if (folder == null) return;

        // A backup is not a profile to open: it restores with the master password. Hand it to the
        // wizard's restore form, already filled in.
        var target = Services.ExistingProfileTarget.Classify(folder);
        if (target.Kind == Services.ExistingProfileKind.Backup)
        {
            ShowRestoreForm(target.BackupPath!);
            return;
        }

        var comparison = BeeMemoryBank.AppPaths.PathComparison.ForCurrentPlatform();
        var fullFolder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        foreach (var p in _profiles.GetAll())
        {
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(p.DataPath), fullFolder, comparison))
            {
                if (p.Id != activeId) await SwitchProfileAsync(p.Id);
                return;
            }
        }

        WebPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SplashPanel.IsVisible = true;
        StatusText.Text = "Opening the profile...";

        Services.RelocateResult result;
        try
        {
            result = await _profileSwitch.OpenFolderAsActiveAsync(
                activeId, fullFolder, new Progress<string>(UpdateStatus), _switchLifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = Services.RelocateResult.Error(ex.Message);
        }

        if (result.Profile != null && !string.IsNullOrEmpty(result.FrontUrl))
        {
            ApplySuccessfulNodeStart(result.Profile, result.FrontUrl!);
        }
        else if (result.Rejected)
        {
            SplashPanel.IsVisible = false;
            WebPanel.IsVisible = true;
        }
        else
        {
            ShowError(result.ErrorMessage ?? "Unknown error.");
        }
        NotifyProfilesChanged();

        if (!result.Success)
        {
            await new Views.MessageDialog("Could not open the profile", result.ErrorMessage ?? "Unknown error.").ShowDialog(this);
        }
    }

    /// <summary>
    /// Refreshes <see cref="Title"/> from the current profile count + active profile name per
    /// §4.5 (only show the profile name when ≥ 2 profiles exist). Public so the tray / manage
    /// window can call it after rename/forget without a full switch.
    /// </summary>
    public void UpdateShellTitle()
    {
        Title = Services.StorageDisplayLogic.FormatShellTitle(_profiles, _activeProfileId);
    }

    /// <summary>
    /// Raises <see cref="ActiveProfileChanged"/> so the tray menu rebuilds its radio list
    /// after a profile is added/renamed/forgotten from the manage window without a switch
    /// necessarily following.
    /// </summary>
    public void NotifyProfilesChanged()
    {
        UpdateShellTitle();
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
        });
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        SplashPanel.IsVisible = false;
        ErrorPanel.IsVisible = true;
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        StartHostOrAttach();
    }

    public void ShowAndFocusWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// "Restart to update": stops the node (graceful, so the database is closed before the files are swapped) and keeps the window, so that
    /// <see cref="ResumeAfterFailedUpdate"/> can bring the node back when the update could not be applied. When it is applied the process ends.
    /// </summary>
    public void StopNodeForUpdate()
    {
        WebPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SplashPanel.IsVisible = true;
        StatusText.Text = "Updating...";
        try
        {
            _nodeLifecycle.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping node process for the update: {ex.Message}");
        }
    }

    /// <summary>The update could not be applied: starts (or attaches to) the node again, as after "Retry".</summary>
    public void ResumeAfterFailedUpdate() => StartHostOrAttach();

    public void RealClose()
    {
        _isRealClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isRealClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            StopNodeProcess();
            base.OnClosing(e);
        }
    }

    private void StopNodeProcess()
    {
        // StopAsync now does an ownership-aware graceful stop: for the hosted node process it
        // closes the child's stdin (EOF → bmbd's stdin-lifeline → clean shutdown) and waits up
        // to the given timeout before falling back to a hard kill; for an attached node it
        // leaves the foreign process untouched. OnClosing is synchronous, so we block on the
        // bounded graceful wait (15s ceiling) — safe because the service never touches the UI
        // synchronization context.
        try
        {
            _switchLifetimeCts.Cancel();
            _switchLifetimeCts.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error cancelling in-flight profile switch on close: {ex.Message}");
        }

        try
        {
            _profileSwitch.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing profile switch service: {ex.Message}");
        }

        try
        {
            _nodeLifecycle.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping node process: {ex.Message}");
        }

        try
        {
            _powerEventsService?.Dispose();
            _powerEventsService = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing power events service: {ex.Message}");
        }

        try
        {
            _blindAlarms?.Dispose();
            _blindAlarms = null;
            (_notifier as IDisposable)?.Dispose();
            _notifier = null;
            _alarmsRequest.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping the blind-node alarm watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the blind-node alarm watcher once (it follows profile switches by itself: it reads the open profile at each poll). Its
    /// notifications go through the system's notifier: the balloon on Windows, the banner on macOS.
    /// </summary>
    private void StartBlindAlarmWatcher()
    {
        if (_blindAlarms != null) return;
        try
        {
            _notifier = Services.ShellPlatforms.Current.CreateNotifier();
            _blindAlarms = new Services.BlindAlarmWatcher(
                _alarmsRequest.PollAsync, _notifier,
                new Services.DesktopSettingsEpisodeStore(new Services.DesktopSettingsStore()),
                () => _activeProfileId);
            _blindAlarms.Changed += (_, _) => BlindAlarmsChanged?.Invoke(this, EventArgs.Empty);
            _blindAlarms.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start the blind-node alarm watcher: {ex.Message}");
        }
    }

    private void StartPowerEventsMonitoring()
    {
        try
        {
            _powerEventsService?.Dispose();
            _powerEventsService = Services.ShellPlatforms.Current.CreatePowerEvents(LockNodeOnSleepAsync, () => LockOnSleep.IsEnabled);
            _powerEventsService?.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start power events monitoring: {ex.Message}");
        }
    }

    /// <summary>
    /// The request the power-events services make when the computer goes to sleep: POST /node/lock of the open node, with the
    /// internal key of the node this app started (<see cref="Services.NodeLockRequest"/> has the details: what is sent, to whom,
    /// what each answer means, and why "locked" is advisory). The Windows monitor fires it in the background; the macOS monitor
    /// waits for it (briefly) before it lets the Mac sleep. Failures are reported in the result and never crash the app.
    /// </summary>
    private Task<Services.SleepLockResult> LockNodeOnSleepAsync(CancellationToken cancellationToken) =>
        _lockOnSleep.RequestAsync(cancellationToken);

    private async Task PickBackupFileAsync()
    {
        var file = await Views.FolderPicker.PickFileAsync(this, "Choose a Bee Memory Bank phone backup",
            "Phone backup", "*" + Services.ExistingProfileTarget.AndroidBackupExtension);
        if (file == null) return;
        var target = Services.ExistingProfileTarget.Classify(file);
        if (target.Kind == Services.ExistingProfileKind.Backup)
            ShowRestoreForm(target.BackupPath!);
        else
            await new Views.MessageDialog("Not a backup", "This file is not a Bee Memory Bank phone backup.").ShowDialog(this);
    }

    private void ShowRestoreForm(string backupPath)
    {
        if (_frontUrl == null) return;
        BmbWebView.Source = Services.ExistingProfileTarget.RestoreFormUrl(_frontUrl, backupPath);
    }

    private void OnWebViewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        // The Setup page's "Open an existing profile" and the restore form's "Choose…" navigate to a
        // command address (DesktopShellCommands) — a native picker instead of a path typed by hand.
        switch (Services.ShellCommands.Match(e.Request))
        {
            case Services.ShellCommand.OpenExistingProfile:
                e.Cancel = true;
                Dispatcher.UIThread.Post(async () => await OpenExistingProfileFolderAsync());
                return;
            case Services.ShellCommand.PickBackupFile:
                e.Cancel = true;
                Dispatcher.UIThread.Post(async () => await PickBackupFileAsync());
                return;
        }

        if (e.Request != null && !IsLocalOrigin(e.Request))
        {
            e.Cancel = true;
            OpenUrlInExternalBrowser(e.Request);
        }
    }

    private void OnWebViewNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        if (e.Request != null && !IsLocalOrigin(e.Request))
        {
            e.Handled = true;
            OpenUrlInExternalBrowser(e.Request);
        }
    }

    private bool IsLocalOrigin(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        // Compare the full origin (scheme + host + port) against the app's own actual
        // front URL, not just "is the host 127.0.0.1" - 127.0.0.1 is shared by every local
        // service on the machine, and cookies are host-scoped (not port-scoped) in
        // browsers, so a loose host-only check would let the WebView navigate into an
        // unrelated local service on another port while still carrying this app's session
        // cookie.
        if (_frontUrl == null || !Uri.TryCreate(_frontUrl, UriKind.Absolute, out var frontUri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, frontUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, frontUri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == frontUri.Port;
    }

    private void OpenUrlInExternalBrowser(Uri uri)
    {
        try
        {
            var url = uri.AbsoluteUri;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL in browser: {ex.Message}");
        }
    }
}
