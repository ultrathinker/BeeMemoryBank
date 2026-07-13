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
    private CancellationTokenSource? _switchCts;
    private bool _startMinimized = Program.StartMinimized;
    private string? _frontUrl;
    private string? _activeProfileId;
    private Services.PowerEventsService? _powerEventsService;

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

    public MainWindow()
    {
        // ProfileSwitchService must be constructed AFTER _profiles and _nodeLifecycle are
        // initialized (field initializers run in declaration order — _profileSwitch is
        // declared last, so this is safe). It uses the same _nodeLifecycle instance that
        // HostOrAttachAsync will later call into, which is the contract it relies on.
        _profileSwitch = new Services.ProfileSwitchService(_profiles, _nodeLifecycle);
        InitializeComponent();
        Opened += MainWindow_Opened;
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
        // the inlined implementation that used to live here, only the ownership moved.
        //
        // §4.6: which profile to start is autostartMode/lastUsed-driven, not the hardcoded
        // default vault - a single-profile installation still resolves to "default" via
        // ProfileService's own first-run fallback, so behavior is unchanged when there is
        // only one profile.
        var profile = Services.AutostartProfileResolver.Resolve(_profiles);
        var progress = new Progress<string>(UpdateStatus);
        var result = await _nodeLifecycle.StartOrAttachAsync(profile.DataPath, progress, token);

        Dispatcher.UIThread.Post(() =>
        {
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

        _switchCts?.Cancel();
        _switchCts = new CancellationTokenSource();
        var ct = _switchCts.Token;

        // Reuse the splash panel as the "switching" view (same visual as a cold start).
        WebPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SplashPanel.IsVisible = true;
        StatusText.Text = "Переключение хранилища...";

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
            ShowError($"Сбой переключения: {ex.Message}");
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The switch service already handled revert-to-A on B-start failure, so even on
        // result.Success==false the active profile may have CHANGED (back to A). Always fire
        // the event so the tray menu re-reads _activeProfileId and the title re-formats.
        if (result.Success && result.Profile != null && !string.IsNullOrEmpty(result.FrontUrl))
        {
            ApplySuccessfulNodeStart(result.Profile, result.FrontUrl!);
        }
        else
        {
            // On failure, re-apply the current state if any node is still alive (revert
            // target may have succeeded), otherwise show the error.
            if (!string.IsNullOrEmpty(_frontUrl))
            {
                SplashPanel.IsVisible = false;
                WebPanel.IsVisible = true;
                UpdateShellTitle();
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Неизвестная ошибка.");
            }
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
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
            _switchCts?.Cancel();
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
    }

    private void StartPowerEventsMonitoring()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _powerEventsService?.Dispose();
            _powerEventsService = new Services.PowerEventsService(HandleSystemSleep);
            _powerEventsService.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start power events monitoring: {ex.Message}");
        }
    }

    private void HandleSystemSleep()
    {
        var url = _frontUrl;
        if (string.IsNullOrEmpty(url)) return;

        // Fire-and-forget: /node/lock is currently a 501 stub pending the internal-key
        // client wiring, so failures here are expected for now - don't crash the app.
        Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await client.PostAsync($"{url.TrimEnd('/')}/node/lock", null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to POST /node/lock on sleep: {ex.Message}");
            }
        });
    }

    private void OnWebViewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
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