using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The sync state badge of the header, in the shape and colours of the design's .sync-badge-btn.
/// The state is the real one: <see cref="FullSync"/> (a round running, a failure it reported, the
/// local network iOS refused) and the phone's connectivity (no network). A tap opens the Sync
/// tab, the same destination the design's badge has. The Android badge reads SyncStatusService;
/// the iPhone's sync is FullSync, so the same four states are read from it instead.
/// </summary>
public partial class SyncIndicatorView : ContentView
{
    private FullSync? _sync;
    private bool _spinning;
    private bool _collapsed;

    public SyncIndicatorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Icon-only mode for a crowded header (a large system font): the pill keeps its colours and
    /// its tap, the words hide so the page title gets the room back. The shell restores the text
    /// when it fits again.
    /// </summary>
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            StateLabel.IsVisible = !value;
        }
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null)
        {
            _sync = IPlatformApplication.Current!.Services.GetRequiredService<FullSync>();
            _sync.Changed += OnSyncChanged;
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
            if (Application.Current is { } app) app.RequestedThemeChanged += OnRequestedThemeChanged;
            UpdateState();
        }
        else
        {
            if (_sync != null) _sync.Changed -= OnSyncChanged;
            Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
            if (Application.Current is { } app) app.RequestedThemeChanged -= OnRequestedThemeChanged;
            StopSpin();
            _sync = null;
        }
    }

    private void OnSyncChanged() => MainThread.BeginInvokeOnMainThread(UpdateState);

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => MainThread.BeginInvokeOnMainThread(UpdateState);

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateState();

    /// <summary>The state of the badge: what the sync service last said, over what the network says.</summary>
    private string CurrentState()
    {
        var access = Connectivity.Current.NetworkAccess;
        if (access != NetworkAccess.Internet && access != NetworkAccess.ConstrainedInternet)
            return "offline";

        if (_sync is { IsRunning: true }) return "syncing";
        if (_sync?.LastRound?.Problem is not null || _sync?.LocalNetworkNotice is not null) return "error";
        // A computer that is simply away is not an error: yellow, never red.
        if (_sync?.LastRound is { Unreachable: > 0 }) return "unreachable";
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
            case "unreachable":
                foreground = "Warning"; border = "WarningBorder"; background = "WarningBackground";
                icon = "sync_off"; labelKey = "SyncBadgeUnreachable";
                break;
            case "error":
                foreground = "Error"; border = "ErrorBorder"; background = "ErrorBackground";
                icon = "sync_err"; labelKey = "SyncBadgeError";
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
        StateIcon.Source = ThemeManager.Icon("icon_" + icon);
        SemanticProperties.SetDescription(Badge, AppStrings.Format("SyncBadgeAccessibility", StateLabel.Text));

        if (state == "syncing") StartSpin();
        else StopSpin();
    }

    // The design spins the badge's icon while a sync runs (.is-syncing .od-icon).
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

    // busy-exempt: opens the Sync tab; no work that waits is started
    private void OnBadgeTapped(object? sender, TappedEventArgs e)
    {
        _ = Shell.Current?.GoToAsync("//sync");
    }
}
