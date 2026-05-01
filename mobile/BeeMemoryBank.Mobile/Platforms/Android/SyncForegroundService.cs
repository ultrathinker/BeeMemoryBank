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

            var token = _cts.Token;

            Task.Run(async () =>
            {
                var services = IPlatformApplication.Current!.Services;
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                var logger = services.GetRequiredService<ILogger<SyncScheduler>>();
                var syncTrigger = services.GetRequiredService<ISyncTrigger>();
                var syncNotify = services.GetRequiredService<Services.SyncNotificationService>();

                using var scope = scopeFactory.CreateScope();
                var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
                var maxTs = await eventLogRepo.GetMaxLamportTimestampAsync();
                var lamportClock = services.GetRequiredService<LamportClock>();
                lamportClock.Initialize(maxTs);

                var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
                var scheduler = new SyncScheduler(scopeFactory, logger, syncTrigger, httpClientFactory, interval: TimeSpan.FromMinutes(5));
                syncNotify.AttachScheduler(scheduler);
                
                await scheduler.StartAsync(token);
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
