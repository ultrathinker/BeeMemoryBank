using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Platforms.Android;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class SyncForegroundService : Service
{
    private CancellationTokenSource? _cts;
    private const int NotificationId = 1001;
    private const string ChannelId = "bmb_sync";

    // True while this service is actively syncing in THIS process. The WorkManager backstop checks it
    // and skips its own cycle when the foreground service is alive, so the two never sync concurrently
    // (which could otherwise double-push or hit "database is locked" under WAL). Static defaults to
    // false in a fresh process — exactly when WorkManager should take over.
    public static volatile bool IsRunning;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        global::Android.Util.Log.Debug("BeeSync", "OnStartCommand entered");
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CreateNotificationChannel();
            global::Android.Util.Log.Debug("BeeSync", "CreateNotificationChannel done");
            if (!TryStartForeground(startId))
                return StartCommandResult.NotSticky;
            IsRunning = true;

            var token = _cts.Token;

            // The start-up of the sync loop. Whatever it throws is caught here: faulted and unobserved, the loop never started
            // while IsRunning stayed true, and the WorkManager backstop skips its cycles while that flag is set - background sync was silently dead
            // until the next start command. A failure is logged and IsRunning cleared, so the backstop takes over.
            Task.Run(async () =>
            {
                try
                {
                    var services = IPlatformApplication.Current!.Services;
                    var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                    var logger = services.GetRequiredService<ILogger<SyncScheduler>>();
                    var syncTrigger = services.GetRequiredService<ISyncTrigger>();
                    var syncNotify = services.GetRequiredService<Services.SyncNotificationService>();

                    // The clock continues from the newest event held. On an encrypted phone the service starts at boot, before anything is unlocked,
                    // and the database is closed: nothing is read now (that read faulted this whole task, so the phone never synced
                    // in the background, not even after the unlock); the open steps set the clock when the unlock opens the database
                    // (AndroidOpenWork), and the scheduler's cycles wait for an open vault themselves.
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
                        services.GetRequiredService<LamportClock>().Initialize(maxTs);
                    }
                    catch (VaultSealedException)
                    {
                        global::Android.Util.Log.Debug("BeeSync", "The database is closed until the unlock; the clock is set when it opens");
                    }

                    var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
                    var scheduler = new SyncScheduler(scopeFactory, logger, syncTrigger, httpClientFactory, interval: TimeSpan.FromMinutes(5));
                    syncNotify.AttachScheduler(scheduler);

                    // Persistent heartbeat: each cycle (success OR failure) stamps Preferences so the
                    // Status page can show when background sync last actually ran on this device.
                    Services.SyncHeartbeat.RecordServiceStart();
                    // Problem, not Error: a peer that answered 404 (a wrong address) or did not answer ends the cycle with a failure per peer, and
                    // that must read "error: no sync server at this address ...", never "ok (applied 0)" (BMB-307).
                    scheduler.SyncCycleCompleted += (_, r) =>
                        Services.SyncHeartbeat.RecordCycle(r.Problem == null, r.TotalApplied, r.Problem, "fg");

                    await scheduler.StartAsync(token);
                }
                catch (System.OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A newer start command or the service's end cancelled this start: the newer one owns the flag.
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("BeeSync", $"The sync loop did not start: {ex.GetType().Name}; the WorkManager backstop takes over");
                    IsRunning = false;
                }
            });

            // The look for the paired desktops on the Wi-Fi lives and dies with the sync loop above (docs/design/lan-peer-sync.md 16): a short look every
            // minute, at once when a peer's LAN addresses failed (a DHCP change). The scheduler's cycle plans each peer's channels from what it saw.
            Task.Run(async () =>
            {
                try
                {
                    if (IPlatformApplication.Current!.Services.GetService<NsdPeerDiscovery>() is { } discovery)
                        await discovery.RunAsync(token);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("BeeSync", $"Peer discovery stopped: {ex.GetType().Name}");
                }
            });

            Task.Run(async () =>
            {
                var services = IPlatformApplication.Current!.Services;
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                var cleanupLogger = services.GetRequiredService<ILogger<CleanupService>>();

                var cleanup = new CleanupService(scopeFactory, cleanupLogger);
                await cleanup.StartAsync(token);
            });

            return StartCommandResult.Sticky;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BeeSync", $"OnStartCommand CRASHED: {ex.GetType().Name}: {ex.Message}");
            return StartCommandResult.NotSticky;
        }
    }

    private bool TryStartForeground(int startId)
    {
        Notification notification;
        try
        {
            notification = BuildNotification();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BeeSync", $"BuildNotification failed: {ex.GetType().Name}: {ex.Message}");
            StopSelf(startId);
            return false;
        }

        try
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            global::Android.Util.Log.Debug("BeeSync", "StartForeground(TypeDataSync) OK");
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BeeSync", $"StartForeground(TypeDataSync) failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Fallback: try without explicit type (API 5+ style)
        try
        {
            StartForeground(NotificationId, notification);
            global::Android.Util.Log.Debug("BeeSync", "StartForeground(noType) OK");
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BeeSync", $"StartForeground(noType) failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Both failed — stop the service cleanly to avoid ForegroundServiceDidNotStartInTimeException crash
        StopSelf(startId);
        return false;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var intent = new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("BeeMemoryBank")
            .SetContentText("Sync active")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .Build();
    }

    private void CreateNotificationChannel()
    {
        var channel = new NotificationChannel(ChannelId, "BeeMemoryBank Sync",
            NotificationImportance.Low);
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(channel);
    }
}
