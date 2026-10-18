using BeeMemoryBank.Busy;
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

    // Diagnostic: show when the background sync loop last actually ran on this device (from the
    // persisted heartbeat) — works even after the app was killed / off-charger / rebooted.
    private void UpdateBackgroundSyncHeartbeat()
    {
        var (lastCycleUtc, status, count, serviceStartUtc) = Services.SyncHeartbeat.Read();
        if (lastCycleUtc == null)
        {
            BgSyncLabel.Text = "No background sync has run yet on this device.";
            BgSyncDetailLabel.Text = serviceStartUtc != null
                ? $"Service started {Ago(serviceStartUtc.Value)}, but no cycle completed."
                : "Service hasn't started yet.";
            return;
        }
        BgSyncLabel.Text = $"Last cycle: {Ago(lastCycleUtc.Value)} — {status}";
        BgSyncDetailLabel.Text = $"Cycles so far: {count}"
            + (serviceStartUtc != null ? $" · service (re)started {Ago(serviceStartUtc.Value)}" : "");
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h {(int)d.Minutes}m ago";
        return $"{(int)d.TotalDays}d {(int)d.Hours}h ago";
    }

    private async Task LoadAsync()
    {
        await Task.Run(async () => await _statusSvc.RefreshAsync());
        _statusSvc.StartPolling();
        WarningBanner.IsVisible = !_statusSvc.IsRegisteredOnServer;

        // Self-heal: if the ingest key never enrolled (e.g. a transient Keystore failure at first
        // setup left background backup dead), enrol it now while unlocked. Surface a banner only if
        // it still isn't armed afterwards, so the failure is never silent.
        var enroller = _services.GetRequiredService<IngestKeyEnroller>();
        await enroller.TryEnrollAsync();
        BackupWarningBanner.IsVisible = !enroller.IsArmed();
        UpdateBackgroundSyncHeartbeat();
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
                // Defense-in-depth fail-closed filter: if identity read fails, show nothing
                // rather than risk leaking the self-row. The real invariant is that self
                // never lives in tbl_whitelist (enforced in InitializationService / NodeSetupService).
                return local == null
                    ? new List<WhitelistEntry>()
                    : all.Where(n => n.NodeId != local.NodeId).ToList();
            });
            PeersList.ItemsSource = peers;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadPeers error: {ex.Message}");
        }
    }

    private async void OnRemovePeerClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not WhitelistEntry entry) return;

        bool confirmed = await DisplayAlert(
            "Remove Node",
            $"Remove \"{entry.DisplayName}\" from peers? They will no longer sync with this node.",
            "Remove", "Cancel");
        if (!confirmed) return;

        // The one busy helper (BMB-311): the button is off and spins; a failure is said in full in a dialog (a list row has no room for a line).
        await BusyAction.RunAsync(btn, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
                // Log first, then stamp the row with that version — see WhitelistEndpoints for
                // why the order matters (a stale add must lose to this revoke).
                var version = await eventLogger.LogWhitelistRevokeAsync(entry.NodeId);
                await whitelist.RevokeAsync(entry.NodeId, version);
            });
            await LoadPeersAsync();
        });
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

        // The one busy helper (BMB-311): the button is off and spins through the check of the new address and the save; a failure is said in
        // full in a dialog (a list row has no room for a line).
        var typed = newUrl;
        await BusyAction.RunAsync(btn, async () =>
        {
            // PeerAddressService is the one writer of a peer's address, the same one the node's "Change node address" calls (BMB-307): the same policy
            // (no user name, query, link-local or metadata address), the same check of what answers there (a sync server with this node id and this key),
            // the event, the audit line, and the loop told. Its sentences never quote the address, so none is put in a dialog here.
            var result = await ChangeAddressAsync(entry.NodeId, typed, saveWhenUnanswered: false);
            if (result.Outcome == PeerAddressOutcome.NoAnswer)
            {
                // Server unreachable - warn but allow saving (the address may be filled in before the server is live). Nothing was checked, and the
                // first session that reaches it will: it has to be this node, with this node's key.
                bool proceed = await DisplayAlert("Warning", result.Message + "\n\nSave the address anyway?", "Save", "Cancel");
                if (!proceed) return;
                result = await ChangeAddressAsync(entry.NodeId, typed, saveWhenUnanswered: true);
            }
            if (!result.Succeeded)
            {
                await DisplayAlert("Not saved", result.Message, "OK");
                return;
            }

            await DisplayAlert(result.Outcome == PeerAddressOutcome.Unchanged ? "Nothing to change" : "Success",
                result.Outcome == PeerAddressOutcome.Unchanged ? result.Message : $"The address of {entry.DisplayName} is updated.", "OK");
            await LoadPeersAsync();
        }, new BusyOptions { FailureTitle = "Failed to update" });
    }

    private async Task<PeerAddressResult> ChangeAddressAsync(Guid nodeId, string typed, bool saveWhenUnanswered)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PeerAddressService>().ChangeAsync(nodeId, typed, "phone", "phone", saveWhenUnanswered);
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

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyNodeIdClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_statusSvc.NodeId);
    }

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyPublicKeyClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_statusSvc.PublicKeyB64);
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the list down asks for a sync round: a request to the computer, not a read (BMB-311). The one busy helper refuses a second round while one runs;
        // the spinner of the pull is the pull's own, so no pop-up; a failure to even ask is said in full.
        try
        {
            await BusyAction.RunAsync(RefreshView, async () =>
            {
                var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
                if (!syncNotify.HasPendingUpdates)
                {
                    await _statusSvc.SyncNowAsync();
                }

                await LoadAsync();
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = "Sync" });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up, so a second tap is impossible, and says in full why
        // a sync failed. What each peer answered is on the page below, whatever happened.
        await BusyAction.RunAsync(SyncNowButton, async () =>
        {
            IReadOnlyList<PeerSyncFailure> failed;
            try
            {
                failed = await _statusSvc.SyncNowAsync();
                var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
                syncNotify.ClearPendingUpdates();
            }
            finally
            {
                await LoadAsync();
            }
            // A peer that could not be served is said in full ("no sync server at this address" for a wrong address), never as a pass (BMB-307).
            if (failed.Count > 0) throw new InvalidOperationException(string.Join("\n", failed));
        }, new BusyOptions
        {
            Long = true,
            Title = "Syncing",
            Message = "Please wait while this phone syncs with its peers. This can take a while on a slow connection.",
            FailureTitle = "The sync did not work",
        });
    }

    private async void OnResetNodeClicked(object? sender, EventArgs e)
    {
        bool confirmed = await DisplayAlert(
            "Reset Node",
            "This will DELETE all local data (articles, keys, sync history). You will need to rejoin the network. Are you sure?",
            "Reset", "Cancel");
        if (!confirmed) return;

        // A long operation (BMB-311): the one busy helper puts up the pop-up until the app restarts; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "beememorybank.db");

            App.StopSyncService();

            // Clear biometric stored password
            _services.GetService<IBiometricService>()?.Clear();
            // Clear the ingest key (bound to the old node identity) so the next setup re-enrols cleanly.
            _services.GetService<IIngestKeyStore>()?.Clear();

            // Clear SQLite connection pool before deleting so no stale connections remain
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Delete the WAL/-shm/-journal sidecars too — an orphaned -wal next to a fresh db gets
            // replayed on next open and resurrects the wiped state.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var f = dbPath + suffix;
                if (File.Exists(f)) File.Delete(f);
            }

            // Restart the app process — the only reliable way to reinitialize all singletons
            // and re-run migrations on the fresh database.
#if ANDROID
            var intent = Android.App.Application.Context.PackageManager!
                .GetLaunchIntentForPackage(Android.App.Application.Context.PackageName!)!;
            intent.AddFlags(Android.Content.ActivityFlags.ClearTop | Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
            return Task.CompletedTask;
#else
            return Shell.Current.GoToAsync("//setup");
#endif
        }, new BusyOptions
        {
            Long = true,
            Title = "Resetting this node",
            Message = "Please wait. The local data is being removed and the app restarts by itself.",
            FailureTitle = "The reset did not work",
        });
    }
}
