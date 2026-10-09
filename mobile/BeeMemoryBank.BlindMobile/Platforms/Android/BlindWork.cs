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
/// (<see cref="BlindSyncWorker"/>), and the long jobs — first load and backups — on any connected
/// network (<see cref="BlindHeavyWorker"/>, run as a foreground job with a notification).
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
        wm.EnqueueUniquePeriodicWork(SyncWork, BlindWorkPlan.UpdateExistingPeriodicWork ? ExistingPeriodicWorkPolicy.Update! : ExistingPeriodicWorkPolicy.Keep!, sync);

        // A connected network is the only heavy-work constraint. Power, battery, and network cost never gate it.
        var heavy = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)), 1, TimeUnit.Hours!)
            .SetConstraints(HeavyConstraints())
            .Build();
        wm.EnqueueUniquePeriodicWork(HeavyWork, BlindWorkPlan.UpdateExistingPeriodicWork ? ExistingPeriodicWorkPolicy.Update! : ExistingPeriodicWorkPolicy.Keep!, heavy);
    }

    /// <summary>
    /// Runs the heavy job as soon as possible instead of at the next hourly slot. Repeated requests preserve
    /// queued or running work.
    /// </summary>
    public static void RunHeavyNow(Context context)
    {
        var once = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)))
            .SetConstraints(HeavyConstraints())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(HeavyNowWork, BlindWorkPlan.KeepExistingOneTimeWork ? ExistingWorkPolicy.Keep! : ExistingWorkPolicy.Replace!, once);
    }

    /// <summary>
    /// One sync round now, from the "Sync now" button: the periodic job cannot be asked to run early (WorkManager's shortest period is
    /// 15 minutes and it delays a periodic job that is started before its time). Repeated requests preserve queued or running work.
    /// </summary>
    public static void SyncNow(Context context)
    {
        var once = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindSyncWorker)))
            .SetConstraints(new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(SyncNowWork, BlindWorkPlan.KeepExistingOneTimeWork ? ExistingWorkPolicy.Keep! : ExistingWorkPolicy.Replace!, once);
    }

    internal static Constraints HeavyConstraints() => new Constraints.Builder()
        .SetRequiredNetworkType(BlindWorkPlan.RequiresConnectedNetwork ? NetworkType.Connected! : NetworkType.NotRequired!)
        .SetRequiresCharging(BlindWorkPlan.RequiresCharging)
        .SetRequiresBatteryNotLow(BlindWorkPlan.RequiresBatteryNotLow)
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
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services == null) return Result.InvokeSuccess()!;
        var state = services.GetRequiredService<BlindPhoneState>();
        if (state.CallCode is not { } target || !state.InitialLoadDone) return Result.InvokeSuccess()!;

        // Registered before the database or the keys are touched: "Disconnect and wipe" cancels this token and waits for the round to end;
        // while the wipe runs no new round starts. OnStopped (WorkManager or the wipe cancelled the work) fires the same token.
        using var operation = services.GetRequiredService<BlindActivity>().TryBegin(BlindActivity.Sync, _stop.Token);
        if (operation is null) return Result.InvokeSuccess()!;
        var token = operation.Token;
        try
        {
            // A worker can be the first thing to run after a reboot or an update: open (migrate) the database first.
            services.GetRequiredService<BlindStartup>().EnsureReadyAsync().WaitAsync(token).GetAwaiter().GetResult();
            Task.Run(() => services.GetRequiredService<IBlindPhoneSync>().SyncOnceAsync(target, token))
                .GetAwaiter().GetResult();
            state.LastSyncAt = DateTimeOffset.UtcNow;
            return Result.InvokeSuccess()!;
        }
        catch (BlindFeaturePendingException)
        {
            return Result.InvokeSuccess()!;
        }
        catch (System.OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped on purpose: WorkManager runs it again when it is due, the wipe does not want it back.
            return Result.InvokeSuccess()!;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<BlindPhoneLog>().Add("sync", $"Sync failed: {BlindRunReport.Reason(ex)}");
            return RunAttemptCount < 3 ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
    }

    // WorkManager (a constraint is gone, the work was cancelled by the wipe) stops the worker: the round ends at its next await.
    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
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

        // Registered first (see BlindSyncWorker): the wipe waits for this worker, and refuses it while it runs.
        using var operation = services.GetRequiredService<BlindActivity>().TryBegin(BlindActivity.Heavy, _stop.Token);
        if (operation is null) return Result.InvokeSuccess()!;
        var token = operation.Token;

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
                services.GetRequiredService<BlindStartup>().EnsureReadyAsync().WaitAsync(token).GetAwaiter().GetResult();
                var result = Task.Run(() => work.RunAsync(forceBackup: false, token)).GetAwaiter().GetResult();
                BlindRunReport.Record(log, "run", result);
            }
            catch (Exception ex) when (ex is not System.OperationCanceledException)
            {
                // An unplanned error: the log tells it, the next hourly slot tries again.
                BlindRunReport.RecordFailure(log, "run", ex);
            }
            catch (System.OperationCanceledException)
            {
                // Stopped before the job began (WorkManager or the wipe): nothing to report.
            }
            return Result.InvokeSuccess()!;
        }
        finally
        {
            work.Progress -= OnProgress;
        }
    }

    // WorkManager or the user can stop the worker; cancellation is handled as an interrupted run.
    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }
}

/// <summary>
/// "Back up now": the same job started by the user while the app is open, as our own foreground
/// service so it survives leaving the screen.
/// <para>One service, one task, one <see cref="CancellationTokenSource"/> (<see cref="BlindSingleRun"/>): a start while the task runs is
/// coalesced (it only refreshes the notification and remembers its start id) BEFORE anything is replaced, and the service stops itself only
/// when its own task ends. The task is registered in <see cref="BlindActivity"/> from its first line, so "Disconnect and wipe" cancels it
/// through that token and waits for it.</para>
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class BlindBackupService : Service
{
    private readonly BlindSingleRun _run = new();
    private bool _destroyed;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(BlindBackupService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notifications = new BlindNotifications(this);
        _run.TryStart(startId,
            token => RunAsync(notifications, token),
            ended =>
            {
                if (_destroyed) return;
                StopForeground(StopForegroundFlags.Remove);
                // The latest start id: a start that arrived after the task ended keeps the service alive for its own task.
                StopSelf(ended);
            },
            // Every start must reach StartForeground (startForegroundService gives the service five seconds to do it), a repeated one too.
            inLock: () => StartForeground(BlindNotifications.Id, notifications.Build("Backup", 0), ForegroundService.TypeDataSync));
        return StartCommandResult.NotSticky;
    }

    private static async Task RunAsync(BlindNotifications notifications, CancellationToken token)
    {
        BlindOperation? operation = null;
        BlindHeavyWork? work = null;
        BlindPhoneLog? log = null;
        void OnProgress(string title, double p) => notifications.Show(title, p);
        try
        {
            var services = IPlatformApplication.Current!.Services;
            log = services.GetRequiredService<BlindPhoneLog>();
            // The whole task, start-up included, is one registered operation (the wipe waits for it); null while the wipe runs.
            operation = services.GetRequiredService<BlindActivity>().TryBegin(BlindActivity.BackupService, token);
            if (operation is null) return;

            work = services.GetRequiredService<BlindHeavyWork>();
            work.Progress += OnProgress;
            await services.GetRequiredService<BlindStartup>().EnsureReadyAsync().WaitAsync(operation.Token);
            var result = await work.RunAsync(forceBackup: true, operation.Token);
            BlindRunReport.Record(log, "backup", result);
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            if (log != null) BlindRunReport.RecordFailure(log, "backup", ex);
        }
        catch (System.OperationCanceledException)
        {
            // The service was stopped (the wipe, the system): the job paused itself and says so.
        }
        finally
        {
            if (work != null) work.Progress -= OnProgress;
            operation?.Dispose();
        }
    }

    public override void OnDestroy()
    {
        _destroyed = true;
        _run.Cancel();
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
