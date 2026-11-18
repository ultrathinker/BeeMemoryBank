using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The sync state badge of the header, in the shape and colours of the mock-up's .sync-badge-btn.
/// The state is the real one: <see cref="SyncStatusService.StatusText"/> (a sync running, a failure
/// it reported, the invisible mode) and the phone's connectivity (no network). A tap opens the
/// Sync tab, the same destination the mock-up's badge has.
/// </summary>
public partial class SyncIndicatorView : ContentView
{
    private SyncStatusService? _status;
    private SyncNotificationService? _syncNotify;
    private bool _spinning;

    public SyncIndicatorView()
    {
        InitializeComponent();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null)
        {
            var services = IPlatformApplication.Current!.Services;
            _status = services.GetRequiredService<SyncStatusService>();
            _syncNotify = services.GetRequiredService<SyncNotificationService>();
            _status.PropertyChanged += OnStatusPropertyChanged;
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
            if (Application.Current is { } app) app.RequestedThemeChanged += OnRequestedThemeChanged;
            // Seed the registration finding so the badge is right even when the Sync tab was never
            // opened (the flag is only a default until a refresh has read the peer channels).
            _ = _status.RefreshAsync();
            UpdateState();
        }
        else
        {
            if (_status != null) _status.PropertyChanged -= OnStatusPropertyChanged;
            Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
            if (Application.Current is { } app) app.RequestedThemeChanged -= OnRequestedThemeChanged;
            StopSpin();
            _status = null;
            _syncNotify = null;
        }
    }

    private void OnStatusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncStatusService.StatusText)
            or nameof(SyncStatusService.IsRegisteredOnServer)
            or nameof(SyncStatusService.IsStatusKnown)
            or nameof(SyncStatusService.LastProblemCode)
            or nameof(SyncStatusService.LastUnreachable))
            MainThread.BeginInvokeOnMainThread(UpdateState);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => MainThread.BeginInvokeOnMainThread(UpdateState);

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateState();

    /// <summary>The state of the badge: what the sync service last said, over what the network says.</summary>
    private string CurrentState()
    {
        // The action-required state dominates: the phone knows no way to reach a peer, so it waits for
        // approval. Only once a refresh has actually read the channels (the flag is a default before that).
        if (_status is { IsStatusKnown: true, IsRegisteredOnServer: false })
            return "pending";

        var access = Connectivity.Current.NetworkAccess;
        if (access != NetworkAccess.Internet && access != NetworkAccess.ConstrainedInternet)
            return "offline";

        var text = _status?.StatusText ?? "";
        if (text.StartsWith("Syncing", StringComparison.Ordinal)) return "syncing";
        // A real failure is red; a computer that is simply away is yellow, never red.
        if (_status?.LastProblemCode is not null) return "error";
        if (_status is { LastUnreachable: > 0 }) return "unreachable";
        if (text == "Invisible") return "invisible";
        return "synced";
    }

    private void UpdateState()
    {
        var state = CurrentState();

        string foreground, border, background, icon, labelKey;
        switch (state)
        {
            case "syncing":
                foreground = "AccentText"; border = "AccentBorder"; background = "AccentSoft";
                icon = "sync_run"; labelKey = "SyncBadgeSyncing";
                break;
            case "offline":
                foreground = "TextMuted"; border = "Border"; background = "Surface";
                icon = "sync_off"; labelKey = "SyncBadgeOffline";
                break;
            case "error":
                foreground = "Error"; border = "ErrorBorder"; background = "ErrorBackground";
                icon = "sync_err"; labelKey = "SyncBadgeError";
                break;
            case "unreachable":
                foreground = "Warning"; border = "WarningBorder"; background = "WarningBackground";
                icon = "sync_off"; labelKey = "SyncBadgeUnreachable";
                break;
            case "pending":
                foreground = "Error"; border = "ErrorBorder"; background = "ErrorBackground";
                icon = "sync_err"; labelKey = "SyncBadgePending";
                break;
            case "invisible":
                foreground = "TextMuted"; border = "Border"; background = "Surface";
                icon = "sync_off"; labelKey = "SyncBadgeInvisible";
                break;
            default:
                foreground = "Success"; border = "SuccessBorder"; background = "SuccessBackground";
                icon = "sync_ok"; labelKey = "SyncBadgeSynced";
                break;
        }

        var fg = ThemeManager.Token(foreground);
        Badge.BackgroundColor = ThemeManager.Token(background);
        Badge.Stroke = ThemeManager.Token(border);
        StateLabel.TextColor = fg;
        StateLabel.Text = AppStrings.Get(labelKey);
        var theme = ThemeManager.Effective == AppTheme.Light ? "light" : "dark";
        StateIcon.Source = $"icon_{icon}_{theme}.svg";
        SemanticProperties.SetDescription(Badge, AppStrings.Format("SyncBadgeAccessibility", StateLabel.Text));

        if (state == "syncing") StartSpin();
        else StopSpin();
    }

    // The mock-up spins the badge's icon while a sync runs (.is-syncing .od-icon).
    private void StartSpin()
    {
        if (_spinning) return;
        _spinning = true;
        new Animation(v => StateIcon.Rotation = v, 0, 360)
            .Commit(this, "SyncBadgeSpin", length: 1100, easing: Easing.Linear, repeat: () => true);
    }

    private void StopSpin()
    {
        if (!_spinning) return;
        this.AbortAnimation("SyncBadgeSpin");
        StateIcon.Rotation = 0;
        _spinning = false;
    }

    // busy-exempt: opens the Sync tab and clears the "new data" mark; no work that waits is started
    private void OnBadgeTapped(object? sender, TappedEventArgs e)
    {
        _syncNotify?.ClearPendingUpdates();
        _ = Shell.Current?.GoToAsync("//status");
    }
}
