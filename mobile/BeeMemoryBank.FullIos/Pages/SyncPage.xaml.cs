using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Sync in the design's cards: the state pill and the last round with Sync now, this device, the
/// peers it syncs with and how the last contact with each went (a tap opens the peer's page), the
/// banners for no network, a failed round and the local network iOS refused. The data is the real
/// FullSync's; only the display changed.
/// </summary>
public partial class SyncPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly FullSync _sync;
    private readonly FullVault _vault;
    private readonly AppFlow _flow;
    private readonly SecurityOps _security;
    private bool _loading;

    public SyncPage(IServiceProvider services, FullSync sync, FullVault vault, AppFlow flow, SecurityOps security)
    {
        InitializeComponent();
        Title = AppStrings.Get("TabSync");
        _services = services;
        _sync = sync;
        _vault = vault;
        _flow = flow;
        _security = security;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _sync.Changed += OnChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        if (Application.Current is { } app) app.RequestedThemeChanged += OnThemeChanged;
        // RefreshAsync catches what it throws (a lock that lands mid-read): this async void never rethrows it.
        await RefreshAsync(sayFailure: true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sync.Changed -= OnChanged;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        if (Application.Current is { } app) app.RequestedThemeChanged -= OnThemeChanged;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(() => _ = RefreshAsync());

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        => MainThread.BeginInvokeOnMainThread(() => _ = RefreshAsync());

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e)
        => MainThread.BeginInvokeOnMainThread(() => _ = RefreshAsync());

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up, so Sync now cannot be tapped twice; what each
        // peer answered is on the page below, whatever happened.
        await BusyAction.RunAsync(SyncButton, () => _sync.RoundAsync(), new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("SyncBadgeSyncing"),
            Message = AppStrings.Get("SyncBusyMessage"),
            FailureTitle = AppStrings.Get("SyncNow"),
        });
    }

    private void OnInvisibleToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        _flow.Invisible = e.Value;
        _ = RefreshAsync();
    }

    private async Task LoadEventsAsync()
    {
        var events = await Task.Run(() => _security.RecentEventsAsync(5));
        if (!_vault.IsUnlocked) return;
        EventsList.Children.Clear();
        NoEventsLabel.IsVisible = events.Count == 0;
        var resources = Application.Current?.Resources;
        foreach (var evt in events)
        {
            var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 10 };
            var text = new Label { Text = EventSummary(evt.EventType), Style = resources?["ItemMeta"] as Style, TextColor = ThemeManager.Token("Text"), LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
            var time = new Label { Text = TimeAgo(evt.CreatedAt), Style = resources?["ItemMeta"] as Style, VerticalOptions = LayoutOptions.Center };
            grid.Children.Add(text);
            Grid.SetColumn(text, 0);
            grid.Children.Add(time);
            Grid.SetColumn(time, 1);
            EventsList.Children.Add(grid);
        }
    }

    private static string EventSummary(string type) => type switch
    {
        "article_create" => AppStrings.Get("EventArticleCreated"),
        "article_update" => AppStrings.Get("EventArticleUpdated"),
        "article_delete" => AppStrings.Get("EventArticleDeleted"),
        "comment_create" => AppStrings.Get("EventCommentAdded"),
        "comment_delete" => AppStrings.Get("EventCommentDeleted"),
        "whitelist_add" => AppStrings.Get("EventDeviceAdded"),
        "whitelist_revoke" => AppStrings.Get("EventDeviceRemoved"),
        _ => type,
    };

    private static string TimeAgo(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
        if (diff.TotalMinutes < 1) return AppStrings.Get("TimeJustNow");
        if (diff.TotalHours < 1) return AppStrings.Format("TimeMinutesAgoFormat", (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return AppStrings.Format("TimeHoursAgoFormat", (int)diff.TotalHours);
        if (diff.TotalDays < 7) return AppStrings.Format("TimeDaysAgoFormat", (int)diff.TotalDays);
        return ContentFormat.ShortDate(utc);
    }

    /// <summary>
    /// Fills the page. Called by OnAppearing and, fire-and-forget, by the sync, network and theme events: it never throws.
    /// The idle timer, the background lock or Lock now can close an encrypted database under one of its reads; the page then stops quietly (the
    /// unlock page takes over), and it looks again after every await whether the vault is still unlocked. A failure on an open vault is logged,
    /// and said in the app's own sentence when the page is opened - never from the background events, which would repeat it.
    /// </summary>
    private async Task RefreshAsync(bool sayFailure = false)
    {
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            var unlocked = _vault.IsUnlocked;
            _services.GetService<ILogger<SyncPage>>()?.LogWarning(ex, "Refreshing the sync page failed (vault unlocked: {Unlocked})", unlocked);
            if (sayFailure && unlocked) await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (!_vault.IsUnlocked) return;
        _loading = true;
        InvisibleSwitch.IsToggled = _flow.Invisible;
        _loading = false;
        await LoadEventsAsync();
        if (!_vault.IsUnlocked) return;
        var identity = await _vault.IdentityAsync();
        if (!_vault.IsUnlocked) return;
        NodeLabel.Text = identity?.DisplayName ?? "";
        NodeIdLabel.Text = identity?.NodeId.ToString() ?? "";
        int count;
        using (var scope = _services.CreateScope())
            count = (await scope.ServiceProvider.GetRequiredService<IArticleRepository>().ListAsync()).Count;
        if (!_vault.IsUnlocked) return;
        CountLabel.Text = count.ToString();

        var access = Connectivity.Current.NetworkAccess;
        var offline = access != NetworkAccess.Internet && access != NetworkAccess.ConstrainedInternet;
        OfflineBanner.IsVisible = offline;

        var round = _sync.LastRound;
        var disconnected = round?.Problem == FullSync.ProblemDisconnected;
        var state = offline ? "offline"
            : _sync.IsRunning ? "syncing"
            : _flow.Invisible ? "invisible"
            : disconnected ? "disconnected"
            : round?.Problem is not null || _sync.LocalNetworkNotice is not null ? "error"
            : round is { Unreachable: > 0 } ? "unreachable"
            : "synced";
        string foreground, border, background, labelKey;
        switch (state)
        {
            case "syncing":
                foreground = "AccentText"; border = "AccentBorder"; background = "AccentSoft"; labelKey = "SyncBadgeSyncing";
                break;
            case "offline":
                foreground = "TextMuted"; border = "Border"; background = "Surface"; labelKey = "SyncBadgeOffline";
                break;
            case "invisible":
                foreground = "TextMuted"; border = "Border"; background = "Surface"; labelKey = "SyncStateInvisible";
                break;
            case "disconnected":
                foreground = "TextMuted"; border = "Border"; background = "Surface"; labelKey = "SyncBadgeDisconnected";
                break;
            case "unreachable":
                foreground = "Warning"; border = "WarningBorder"; background = "WarningBackground"; labelKey = "SyncBadgeUnreachable";
                break;
            case "error":
                foreground = "Error"; border = "ErrorBorder"; background = "ErrorBackground"; labelKey = "SyncBadgeError";
                break;
            default:
                foreground = "Success"; border = "SuccessBorder"; background = "SuccessBackground"; labelKey = "SyncBadgeSynced";
                break;
        }
        StatePillLabel.Text = AppStrings.Get(labelKey);
        StatePillLabel.TextColor = ThemeManager.Token(foreground);
        StatePill.BackgroundColor = ThemeManager.Token(background);
        StatePill.Stroke = ThemeManager.Token(border);

        LastSyncValue.Text = round is null
            ? (_sync.IsRunning ? AppStrings.Get("SyncBadgeSyncing") : AppStrings.Get("SyncNotYet"))
            : ContentFormat.FullDate(round.At);
        RoundDetail.Text = round is null ? "" : AppStrings.Format("SyncRoundFormat", round.Reached, round.Failed, round.Applied);
        RoundDetail.IsVisible = round is not null;

        // Real failures in red, a computer that is simply away in yellow, each in the app's own words.
        ProblemText.Text = Controls.SyncText.Problem(round?.Problem, "");
        ErrorBanner.IsVisible = round?.Problem is not null && !disconnected;
        DisconnectedBanner.IsVisible = disconnected;
        UnreachableText.Text = round is { Unreachable: > 0 } ? Controls.SyncText.Unreachable(round.UnreachablePeer ?? "") : "";
        UnreachableBanner.IsVisible = round is { Unreachable: > 0 } && round.Problem is null;
        LanText.Text = _sync.LocalNetworkNotice is not null ? AppStrings.Get("SyncLanOff") : "";
        LanBanner.IsVisible = _sync.LocalNetworkNotice is not null;

        var peers = await _sync.PeersAsync();
        if (!_vault.IsUnlocked) return;
        BuildPeerRows(peers);
        NoPeersLabel.IsVisible = peers.Count == 0;
    }

    // The device cards in the app's own words: reachability, last contact, and the last problem
    // (yellow when the computer is simply away, red for a real failure). A tap opens the peer's page.
    private void BuildPeerRows(IReadOnlyList<PeerStatus> peers)
    {
        PeersList.Children.Clear();
        var resources = Application.Current?.Resources;
        foreach (var peer in peers)
        {
            var field = new VerticalStackLayout { Spacing = 2 };
            field.Children.Add(new Label { Text = peer.Name, Style = resources?["ItemTitle"] as Style });
            field.Children.Add(new Label { Text = peer.Address, Style = resources?["ItemMeta"] as Style });
            var reach = Controls.SyncText.Reachability(peer.Reachability, peer.Name);
            if (reach is not null) field.Children.Add(new Label { Text = reach, FontSize = 14 });
            field.Children.Add(new Label { Text = Controls.SyncText.Contact(peer), FontSize = 14 });
            var problem = Controls.SyncText.Problem(peer.ProblemCode, peer.Name);
            if (problem is not null)
            {
                var yellow = peer.ProblemCode == FullSync.ProblemUnreachable;
                var calm = peer.ProblemCode == FullSync.ProblemDisconnected;
                field.Children.Add(new Label
                {
                    Text = problem,
                    FontSize = 14,
                    TextColor = ThemeManager.Token(calm ? "TextSecondary" : yellow ? "Warning" : "Error"),
                });
            }
            var row = new Border { Style = resources?["CardBorder"] as Style, Padding = new Thickness(12), Content = field };
            var captured = peer;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await Navigation.PushAsync(_services.GetRequiredService<PeerPage>().For(captured.NodeId, captured.Name));
            row.GestureRecognizers.Add(tap);
            PeersList.Children.Add(row);
        }
    }
}
