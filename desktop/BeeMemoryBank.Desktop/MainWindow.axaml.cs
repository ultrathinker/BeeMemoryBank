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
    private bool _isRealClose;
    private CancellationTokenSource? _initCts;
    private bool _startMinimized = Program.StartMinimized;
    private string? _frontUrl;
    private string? _activeProfileId;
    private Services.PowerEventsService? _powerEventsService;

    public string? FrontUrl => _frontUrl;

    public MainWindow()
    {
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
            if (result.Success)
            {
                _activeProfileId = profile.Id;
                try { _profiles.SetLastUsed(profile.Id); }
                catch (Exception ex) { Debug.WriteLine($"Failed to record last-used profile: {ex.Message}"); }

                var targetUrl = result.FrontUrl;
                if (!string.IsNullOrEmpty(targetUrl))
                {
                    // Subscribe BEFORE assigning Source: the origin-lock handlers must be in
                    // place before the very first navigation happens, otherwise a tampered
                    // .runtime.json that passed the loose /node/status probe could navigate
                    // once, unguarded, before these handlers ever attach.
                    _frontUrl = targetUrl;
                    BmbWebView.NavigationStarted -= OnWebViewNavigationStarted;
                    BmbWebView.NavigationStarted += OnWebViewNavigationStarted;
                    BmbWebView.NewWindowRequested -= OnWebViewNewWindowRequested;
                    BmbWebView.NewWindowRequested += OnWebViewNewWindowRequested;
                    BmbWebView.Source = new Uri(targetUrl);
                    StartPowerEventsMonitoring();
                }
                SplashPanel.IsVisible = false;
                WebPanel.IsVisible = true;
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Unknown error.");
            }
        });
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