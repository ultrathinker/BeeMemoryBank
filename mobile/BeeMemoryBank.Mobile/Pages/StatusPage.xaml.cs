using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
#if ANDROID
using BeeMemoryBank.Mobile.Platforms.Android;
#endif

namespace BeeMemoryBank.Mobile.Pages;

public record ActivityItem(string Icon, string Summary, string ActorName, string TimeAgo);

public partial class StatusPage : ContentPage
{
    private readonly SyncStatusService _statusSvc;
    private readonly IServiceProvider _services;

    public StatusPage(SyncStatusService statusSvc, IServiceProvider services)
    {
        InitializeComponent();
        _statusSvc = statusSvc;
        _services = services;
        BindingContext = _statusSvc;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
        _statusSvc.StopPolling();
    }

    private async Task LoadAsync()
    {
        await Task.Run(async () => await _statusSvc.RefreshAsync());
        _statusSvc.StartPolling();
        _statusSvc.SyncServiceRunning = true;
        WarningBanner.IsVisible = !_statusSvc.IsRegisteredOnServer;
        UpdateToggleButtonText();
        await LoadActivityAsync();
        await LoadPeersAsync();
    }

    private async Task LoadPeersAsync()
    {
        try
        {
            var peers = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
                var local = await identity.GetAsync();
                var all = await whitelist.GetAllActiveAsync();
                return all.Where(n => local == null || n.NodeId != local.NodeId).ToList();
            });
            PeersList.ItemsSource = peers;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadPeers error: {ex.Message}");
        }
    }

    private async void OnAddPeerClicked(object? sender, EventArgs e)
    {
        var url = await this.ShowInputPopupAsync(
            "Add Node",
            "Enter the node's API address:",
            placeholder: "https://example.com or http://192.168.1.x:5300");

        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.GetAsync($"{url}/api/sync/identity");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            // Parse NodeId and Name from identity JSON (simple extraction)
            var nodeId = ExtractJsonString(json, "nodeId");
            var nodeName = ExtractJsonString(json, "nodeName") ?? ExtractJsonString(json, "name") ?? "Unknown";

            if (string.IsNullOrEmpty(nodeId) || !Guid.TryParse(nodeId, out var nodeGuid))
            {
                await DisplayAlert("Error", "Could not parse node identity from server response.", "OK");
                return;
            }

            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
                var existing = await whitelist.GetByNodeIdAsync(nodeGuid);
                if (existing != null)
                {
                    existing.ApiAddress = url;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await whitelist.UpdateAsync(existing);
                }
                else
                {
                    var entry = new WhitelistEntry
                    {
                        NodeId = nodeGuid,
                        DisplayName = nodeName,
                        ApiAddress = url,
                        Status = "A",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await whitelist.CreateAsync(entry);
                    await eventLogger.LogWhitelistAddAsync(entry);
                }
            });

            await DisplayAlert("Added", $"Node \"{nodeName}\" added.", "OK");
            await LoadPeersAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not add node: {ex.Message}", "OK");
        }
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var search = $"\"{key}\"";
        var idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx += search.Length;
        while (idx < json.Length && (json[idx] == ':' || json[idx] == ' ')) idx++;
        if (idx >= json.Length || json[idx] != '"') return null;
        idx++;
        var end = json.IndexOf('"', idx);
        return end < 0 ? null : json[idx..end];
    }

    private async void OnRemovePeerClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not WhitelistEntry entry) return;

        bool confirmed = await DisplayAlert(
            "Remove Node",
            $"Remove \"{entry.DisplayName}\" from peers? They will no longer sync with this node.",
            "Remove", "Cancel");
        if (!confirmed) return;

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
                await whitelist.RevokeAsync(entry.NodeId);
                await eventLogger.LogWhitelistRevokeAsync(entry.NodeId);
            });
            await LoadPeersAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnEditPeerUrlClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not WhitelistEntry entry) return;

        var newUrl = await this.ShowInputPopupAsync(
            "Change URL",
            $"Enter new API address for {entry.DisplayName}:",
            initialValue: entry.ApiAddress ?? "",
            placeholder: "https://example.com");

        if (string.IsNullOrWhiteSpace(newUrl)) return;
        newUrl = newUrl.Trim().TrimEnd('/');
        if (!newUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !newUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            newUrl = "https://" + newUrl;

        // Validate: ping the new URL and check NodeId
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.GetAsync($"{newUrl}/api/sync/identity");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            // Simple NodeId extraction
            if (!json.Contains(entry.NodeId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlert("Error", "The node at this URL has a different NodeId. Cannot change.", "OK");
                return;
            }
        }
        catch (Exception ex)
        {
            // Server unreachable — warn but allow saving (URL may be updated before server is live)
            bool proceed = await DisplayAlert(
                "Warning",
                $"Could not reach {newUrl} ({ex.Message}).\n\nSave the URL anyway?",
                "Save", "Cancel");
            if (!proceed) return;
        }

        // Update locally
        try
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
                entry.ApiAddress = newUrl;
                entry.UpdatedAt = DateTime.UtcNow;
                await whitelist.UpdateAsync(entry);
                await eventLogger.LogWhitelistUpdateAsync(entry.NodeId, newUrl, null);
            });
            await DisplayAlert("Success", $"URL for {entry.DisplayName} updated to {newUrl}", "OK");
            await LoadPeersAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to update: {ex.Message}", "OK");
        }
    }

    private async Task LoadActivityAsync()
    {
        try
        {
            var items = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var eventRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
                var events = await eventRepo.GetRecentAsync(5);
                return events.Select(evt => new ActivityItem(
                    Icon: GetEventIcon(evt.EventType),
                    Summary: GetEventSummary(evt),
                    ActorName: evt.ActorName ?? evt.NodeId.ToString()[..8],
                    TimeAgo: GetTimeAgo(evt.CreatedAt)
                )).ToList();
            });
            ActivityList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadActivity error: {ex.Message}");
        }
    }

    private static string GetEventIcon(string eventType) => eventType switch
    {
        "article_create" => "✏️",
        "article_update" => "📝",
        "article_delete" => "🗑️",
        "comment_create" => "💬",
        "comment_delete" => "🔇",
        "whitelist_add"  => "🔗",
        "whitelist_revoke" => "🚫",
        _ => "⚡"
    };

    private static string GetEventSummary(SyncEvent evt) => evt.EventType switch
    {
        "article_create" => "Article created",
        "article_update" => "Article updated",
        "article_delete" => "Article deleted",
        "comment_create" => "Comment added",
        "comment_delete" => "Comment deleted",
        "whitelist_add"  => "Node added to whitelist",
        "whitelist_revoke" => "Node revoked",
        _ => evt.EventType
    };

    private static string GetTimeAgo(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToString("MM-dd");
    }

    private async void OnCopyNodeIdClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_statusSvc.NodeId);
    }

    private async void OnCopyPublicKeyClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_statusSvc.PublicKeyB64);
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
        if (!syncNotify.HasPendingUpdates)
        {
            await _statusSvc.SyncNowAsync();
        }
        
        await LoadAsync();
        syncNotify.ClearPendingUpdates();
        RefreshView.IsRefreshing = false;
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        try
        {
            await _statusSvc.SyncNowAsync();
            var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
            syncNotify.ClearPendingUpdates();
        }
        finally
        {
            await LoadAsync();
            SyncNowButton.IsEnabled = true;
        }
    }

    private void OnToggleSyncClicked(object? sender, EventArgs e)
    {
        if (_statusSvc.SyncServiceRunning)
        {
            App.StopSyncService();
            _statusSvc.SyncServiceRunning = false;
            _statusSvc.StatusText = "Stopped";
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(App.StartSyncService);
            _statusSvc.SyncServiceRunning = true;
            _statusSvc.StatusText = "Active";
        }
        UpdateToggleButtonText();
    }

    private async void OnResetNodeClicked(object? sender, EventArgs e)
    {
        bool confirmed = await DisplayAlert(
            "Reset Node",
            "This will DELETE all local data (articles, keys, sync history). You will need to rejoin the network. Are you sure?",
            "Reset", "Cancel");
        if (!confirmed) return;

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "beememorybank.db");

        App.StopSyncService();

        // Clear biometric stored password
        _services.GetService<IBiometricService>()?.Clear();

        // Clear SQLite connection pool before deleting so no stale connections remain
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(dbPath))
            File.Delete(dbPath);

        // Restart the app process — the only reliable way to reinitialize all singletons
        // and re-run migrations on the fresh database.
#if ANDROID
        var intent = Android.App.Application.Context.PackageManager!
            .GetLaunchIntentForPackage(Android.App.Application.Context.PackageName!)!;
        intent.AddFlags(Android.Content.ActivityFlags.ClearTop | Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
        await Shell.Current.GoToAsync("//setup");
#endif
    }

    private void UpdateToggleButtonText()
    {
        ToggleSyncButton.Text = _statusSvc.SyncServiceRunning ? "Stop Sync Service" : "Start Sync Service";
        var appResources = Application.Current!.Resources;
        ToggleSyncButton.BackgroundColor = _statusSvc.SyncServiceRunning
            ? (Color)appResources["ErrorColor"]
            : (Color)appResources["SuccessColor"];
    }
}
