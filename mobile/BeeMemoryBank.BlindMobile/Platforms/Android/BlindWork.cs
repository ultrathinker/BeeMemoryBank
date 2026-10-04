using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Work;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Java.Util.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// Background work of the Android blind node (plan section 10): sync every 15 minutes on any network
/// (<see cref="BlindSyncWorker"/>), and the long jobs — first load, backups — only on Wi-Fi and the
/// charger (<see cref="BlindHeavyWorker"/>, run as a foreground job with a notification).
/// </summary>
public static class BlindWorkScheduler
{
    private const string SyncWork = "bmb_blind_sync";
    private const string HeavyWork = "bmb_blind_heavy";
    private const string HeavyNowWork = "bmb_blind_heavy_now";
    private const string SyncNowWork = "bmb_blind_sync_now";

    public static void Ensure(Context context)
    {
        var wm = WorkManager.GetInstance(context);

        var sync = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindSyncWorker)), 15, TimeUnit.Minutes!)
            .SetConstraints(new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build())
            .SetBackoffCriteria(BackoffPolicy.Exponential!, 30, TimeUnit.Seconds!)
            .Build();
        wm.EnqueueUniquePeriodicWork(SyncWork, ExistingPeriodicWorkPolicy.Keep!, sync);

        // WorkManager's constraints are a first filter; BlindPhoneWork (>= 20 %, checked after every
        // chunk) is the rule. WorkManager stops the job when a constraint goes away; it resumes later.
        var heavy = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)), 1, TimeUnit.Hours!)
            .SetConstraints(HeavyConstraints())
            .Build();
        wm.EnqueueUniquePeriodicWork(HeavyWork, ExistingPeriodicWorkPolicy.Keep!, heavy);
    }

    /// <summary>
    /// Runs the heavy job as soon as Wi-Fi and the charger allow, instead of at the next hourly slot: right after
    /// pairing the first load should not wait up to an hour. Same constraints; one at a time (a second request
    /// while one is queued or running is dropped).
    /// </summary>
    public static void RunHeavyNow(Context context)
    {
        var once = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)))
            .SetConstraints(HeavyConstraints())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(HeavyNowWork, ExistingWorkPolicy.Keep!, once);
    }

    /// <summary>
    /// One sync round now, from the "Sync now" button: the periodic job cannot be asked to run early (WorkManager's shortest period is
    /// 15 minutes and it delays a periodic job that is started before its time). Same worker, same network rule; a second request
    /// while one is queued or running is dropped.
    /// </summary>
    public static void SyncNow(Context context)
    {
        var once = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindSyncWorker)))
            .SetConstraints(new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(SyncNowWork, ExistingWorkPolicy.Keep!, once);
    }

    private static Constraints HeavyConstraints() => new Constraints.Builder()
        .SetRequiredNetworkType(NetworkType.Unmetered!)
        .SetRequiresCharging(true)
        .SetRequiresBatteryNotLow(true)
        .Build();

    public static void Cancel(Context context)
    {
        var wm = WorkManager.GetInstance(context);
        wm.CancelUniqueWork(SyncWork);
        wm.CancelUniqueWork(HeavyWork);
        wm.CancelUniqueWork(HeavyNowWork);
        wm.CancelUniqueWork(SyncNowWork);
    }
}

/// <summary>One sync round with the listening node.</summary>
public class BlindSyncWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public override Result DoWork()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services == null) return Result.InvokeSuccess()!;
        var state = services.GetRequiredService<BlindPhoneState>();
        if (state.CallCode is not { } target || !state.InitialLoadDone) return Result.InvokeSuccess()!;

        try
        {
            // A worker can be the first thing to run after a reboot or an update: open (migrate) the database first.
            services.GetRequiredService<BlindStartup>().EnsureReadyAsync().GetAwaiter().GetResult();
            Task.Run(() => services.GetRequiredService<IBlindPhoneSync>().SyncOnceAsync(target, CancellationToken.None))
                .GetAwaiter().GetResult();
            state.LastSyncAt = DateTimeOffset.UtcNow;
            return Result.InvokeSuccess()!;
        }
        catch (BlindFeaturePendingException)
        {
            return Result.InvokeSuccess()!;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<BlindPhoneLog>().Add("sync", $"Sync failed: {ex.Message}");
            return RunAttemptCount < 3 ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
    }
}

/// <summary>
/// First load and due backups, as a foreground job with a notification (WorkManager's own foreground
/// service: an app in the background may not start one of its own on Android 12+).
/// </summary>
public class BlindHeavyWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services == null) return Result.InvokeSuccess()!;

        var work = services.GetRequiredService<BlindHeavyWork>();
        var notifications = new BlindNotifications(ApplicationContext!);
        void OnProgress(string title, double p) => notifications.Show(title, p);
        work.Progress += OnProgress;
        try
        {
            SetForegroundAsync(notifications.ForegroundInfo("Blind copy", 0))!.Get();
            var log = services.GetRequiredService<BlindPhoneLog>();
            try
            {
                services.GetRequiredService<BlindStartup>().EnsureReadyAsync().GetAwaiter().GetResult();
                var result = Task.Run(() => work.RunAsync(forceBackup: false, _stop.Token)).GetAwaiter().GetResult();
                BlindRunReport.Record(log, "run", result);
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException)
            {
                // An unplanned error: the log tells it, the next hourly slot tries again.
                BlindRunReport.RecordFailure(log, "run", ex);
            }
            return Result.InvokeSuccess()!;
        }
        finally
        {
            work.Progress -= OnProgress;
        }
    }

    // Charger unplugged or Wi-Fi lost: WorkManager stops us; the job pauses and resumes next time.
    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }
}

/// <summary>
/// "Back up now": the same job started by the user while the app is open, as our own foreground
/// service so it survives leaving the screen.
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class BlindBackupService : Service
{
    private CancellationTokenSource? _cts;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(BlindBackupService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notifications = new BlindNotifications(this);
        StartForeground(BlindNotifications.Id, notifications.Build("Backup", 0), ForegroundService.TypeDataSync);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            var services = IPlatformApplication.Current!.Services;
            var work = services.GetRequiredService<BlindHeavyWork>();
            void OnProgress(string title, double p) => notifications.Show(title, p);
            work.Progress += OnProgress;
            try
            {
                await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
                var result = await work.RunAsync(forceBackup: true, token);
                BlindRunReport.Record(services.GetRequiredService<BlindPhoneLog>(), "backup", result);
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException)
            {
                BlindRunReport.RecordFailure(services.GetRequiredService<BlindPhoneLog>(), "backup", ex);
            }
            finally
            {
                work.Progress -= OnProgress;
                StopForeground(StopForegroundFlags.Remove);
                StopSelf(startId);
            }
        });
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _cts?.Cancel();
        base.OnDestroy();
    }
}

/// <summary>The one notification of the blind node's long jobs, with progress.</summary>
internal sealed class BlindNotifications(Context context)
{
    public const int Id = 1101;
    private const string Channel = "bmb_blind";

    public Notification Build(string title, double progress)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "BeeMemoryBank blind copy", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)), PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(context, Channel);
        builder.SetContentTitle("BeeMemoryBank blind copy");
        builder.SetContentText(progress > 0 ? $"{title}: {progress:P0}" : title);
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo);
        builder.SetProgress(100, (int)(progress * 100), progress <= 0);
        builder.SetContentIntent(open);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        return builder.Build()!;
    }

    public void Show(string title, double progress) =>
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(Id, Build(title, progress));

    public ForegroundInfo ForegroundInfo(string title, double progress) =>
        new(Id, Build(title, progress), (int)ForegroundService.TypeDataSync);
}
