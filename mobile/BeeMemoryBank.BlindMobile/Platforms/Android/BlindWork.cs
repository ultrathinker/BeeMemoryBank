using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Work;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Java.Util.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// Background work of the Android blind node (plan section 10), for ALL accounts of the device with the four unique work names the 2.6 app had: a sync round of every account every 15 minutes
/// (<see cref="BlindSyncWorker"/>), and the long jobs - first load and backups - every hour as a foreground job with a notification (<see cref="BlindHeavyWorker"/>). There is one job for the device, not one
/// per account: the workers hand the pass to the device's schedulers (<see cref="BlindDevice"/>), which choose the account, the order and what is due, and which the limits guard may answer "not now".
/// </summary>
public static class BlindWorkScheduler
{
    private const string SyncWork = "bmb_blind_sync";
    private const string HeavyWork = "bmb_blind_heavy";
    private const string HeavyNowWork = "bmb_blind_heavy_now";
    private const string SyncNowWork = "bmb_blind_sync_now";

    private static readonly object Gate = new();
    private static BlindWorkRequirements? _applied;

    /// <summary>
    /// Queues the two periodic jobs. With the accounts' <paramref name="requirements"/> (all of them wait for Wi-Fi; all of them pause on battery saver) the requests are replaced, once per change; with none (the boot
    /// receiver runs before there is a host) it only makes sure something is queued and never loosens what the accounts asked for. The runtime check of the guard stays the authority either way.
    /// </summary>
    public static void Ensure(Context context, BlindWorkRequirements? requirements = null)
    {
        lock (Gate)
        {
            if (requirements is { } known && _applied == known) return;
            var wanted = requirements ?? BlindWorkRequirements.Default;
            var wm = WorkManager.GetInstance(context);
            var policy = PeriodicPolicy(requirements is not null);

            var sync = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindSyncWorker)), 15, TimeUnit.Minutes!)
                .SetConstraints(PeriodicConstraints(wanted))
                .SetBackoffCriteria(BackoffPolicy.Exponential!, 30, TimeUnit.Seconds!)
                .Build();
            wm.EnqueueUniquePeriodicWork(SyncWork, policy, sync);

            var heavy = new PeriodicWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)), 1, TimeUnit.Hours!)
                .SetConstraints(PeriodicConstraints(wanted))
                .Build();
            wm.EnqueueUniquePeriodicWork(HeavyWork, policy, heavy);

            if (requirements is not null) _applied = requirements;
        }
    }

    /// <summary>Known requirements replace the queued requests (an update of the app picks the rule up; so does a changed switch); unknown ones keep them.</summary>
    private static ExistingPeriodicWorkPolicy PeriodicPolicy(bool known) =>
        known && BlindWorkPlan.UpdateExistingPeriodicWork ? ExistingPeriodicWorkPolicy.Update! : ExistingPeriodicWorkPolicy.Keep!;

    /// <summary>
    /// Runs the heavy job as soon as possible instead of at the next hourly slot. Repeated requests preserve queued or running work. It waits for a connected network and nothing else: a switch of an
    /// account that says "not on this network" is answered at once by the guard, where the person can read it.
    /// </summary>
    public static void RunHeavyNow(Context context)
    {
        var once = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(typeof(BlindHeavyWorker)))
            .SetConstraints(AskedForConstraints())
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
            .SetConstraints(AskedForConstraints())
            .Build();
        WorkManager.GetInstance(context).EnqueueUniqueWork(SyncNowWork, BlindWorkPlan.KeepExistingOneTimeWork ? ExistingWorkPolicy.Keep! : ExistingWorkPolicy.Replace!, once);
    }

    internal static Constraints AskedForConstraints() => new Constraints.Builder()
        .SetRequiredNetworkType(BlindWorkPlan.RequiresConnectedNetwork ? NetworkType.Connected! : NetworkType.NotRequired!)
        .SetRequiresCharging(BlindWorkPlan.RequiresCharging)
        .Build();

    internal static Constraints PeriodicConstraints(BlindWorkRequirements r) => new Constraints.Builder()
        .SetRequiredNetworkType(r.UnmeteredNetwork ? NetworkType.Unmetered! : NetworkType.Connected!)
        .SetRequiresCharging(BlindWorkPlan.RequiresCharging)
        .SetRequiresBatteryNotLow(r.BatteryNotLow)
        .Build();
}

/// <summary>The sync pass of the device: one round for every account that is due, one account at a time (<see cref="BlindDevice.SyncScheduler"/>).</summary>
public class BlindSyncWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        var device = IPlatformApplication.Current?.Services.GetService<BlindDevice>();
        if (device is null) return Result.InvokeSuccess()!;
        var token = _stop.Token;
        try
        {
            // A worker can be the first thing to run after a reboot or an update: the host opens the registry and every account's database first.
            // Each round is registered in its account's BlindActivity by the controller, so a removal waits for it and refuses a new one.
            Task.Run(async () =>
            {
                await device.StartAsync(token);
                await device.SyncScheduler.TickAsync(token);
            }).GetAwaiter().GetResult();
            return Result.InvokeSuccess()!;
        }
        catch (System.OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped on purpose: WorkManager runs it again when it is due.
            return Result.InvokeSuccess()!;
        }
        catch (Exception ex)
        {
            // An account's own failure is in its log and is not thrown by the scheduler; this is the device's (the host could not start).
            device.Log.Add("sync", $"The sync pass failed: {BlindRunReport.Reason(ex)}");
            return RunAttemptCount < 3 ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
    }

    // WorkManager (a constraint is gone, the work was cancelled) stops the worker: the pass ends at its next await.
    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }
}

/// <summary>
/// First load and due backups of every account, as a foreground job with a notification that names the account (WorkManager's own foreground
/// service: an app in the background may not start one of its own on Android 12+). One account at a time, and one long job on the device at a time (the device gate).
/// <para>The notification is published when an account's job really starts (<see cref="BlindHeavyWork.Started"/>), naming THAT account, and published again for the next account's job: a pass in which nothing
/// is due shows none (it used to flash an anonymous "Blind copy" every hour), and what is shown is never a name that is not the running job's.</para>
/// </summary>
public class BlindHeavyWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        var device = IPlatformApplication.Current?.Services.GetService<BlindDevice>();
        if (device is null) return Result.InvokeSuccess()!;
        var token = _stop.Token;

        var notifications = new BlindNotifications(ApplicationContext!);
        var hooked = new List<(BlindHeavyWork Work, Action Started, Action<string, double> Progress)>();
        try
        {
            Task.Run(async () =>
            {
                await device.StartAsync(token);
                foreach (var account in device.Accounts.GetSnapshot().Accounts.Where(a => a.Status is not null))
                {
                    if (device.Accounts.ServicesFor(account.Entry.Id).GetService<BlindHeavyWork>() is not { } work) continue;
                    var name = account.Entry.Name;
                    void OnStarted() => ShowForeground(device, notifications, name);
                    void OnProgress(string title, double p) => notifications.Show(name, title, p);
                    work.Started += OnStarted;
                    work.Progress += OnProgress;
                    hooked.Add((work, OnStarted, OnProgress));
                }
                await device.HeavyScheduler.TickAsync(token);
            }).GetAwaiter().GetResult();
        }
        catch (System.OperationCanceledException)
        {
            // Stopped before or during the pass (WorkManager, a removal): the jobs paused themselves and said so in their own logs.
        }
        catch (Exception ex)
        {
            // An unplanned error of the device: the log tells it, the next hourly slot tries again.
            device.Log.Add("run", $"The long-job pass failed: {BlindRunReport.Reason(ex)}");
        }
        finally
        {
            foreach (var (work, started, progress) in hooked)
            {
                work.Started -= started;
                work.Progress -= progress;
            }
        }
        return Result.InvokeSuccess()!;
    }

    /// <summary>
    /// Makes the worker a foreground job with the notification of the account whose job has just started (the next account's job calls it again and the notification is replaced). A system that does not allow it
    /// costs the notification, not the job; the log says so without the platform's words.
    /// </summary>
    private void ShowForeground(BlindDevice device, BlindNotifications notifications, string account)
    {
        try { SetForegroundAsync(notifications.ForegroundInfo(account, "Working", 0))!.Get(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            device.Log.Add("run", "The notification of a long job could not be shown; the job goes on without it.");
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
/// "Back up now": the same job started by the user while the app is open, for ONE account (the intent names it), as our own foreground
/// service so it survives leaving the screen.
/// <para>This file is thin on purpose: who may run, what a delivered start means, when the work starts and what an end releases are decided in portable, tested code
/// (<see cref="BlindManualBackup"/>, ONE state machine for the whole process, and <see cref="BlindManualBackupService"/>, one instance's side of it). The page's tap reserves the account synchronously, before the
/// system is asked to start the service: a request for another account is refused on the spot with the holder's name, and while an older service instance's backup is still ending every request is refused as "stopping".
/// The service reports its start, its end and its destruction to that code and does what it answers. The task is registered in the account's <see cref="BlindActivity"/> from its first line (by the controller),
/// so a removal cancels it through that token and waits for it.</para>
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class BlindBackupService : Service, IBlindBackupServiceHost
{
    private const string ExtraAccount = "bmb_account";

    /// <summary>The one state of the manual backup for the whole process (service instances come and go; this does not).</summary>
    private static readonly BlindManualBackup Machine = new();

    private BlindManualBackupService? _core;
    private BlindNotifications? _notifications;

    /// <summary>The account whose backup holds the phone (reserved or running), or null.</summary>
    public static string? RunningFor => Machine.Holder;

    /// <summary>
    /// Asks for the backup of one account. The account is reserved before the system is asked to start the service: false (and nothing started) when another account's backup holds the phone, or is still ending;
    /// <paramref name="refusal"/> then says whose and whether it is stopping. True when the account holds the reservation (a second request for the same account joins its run).
    /// </summary>
    public static bool Start(Context context, string accountId, out BlindBackupReservation refusal)
    {
        refusal = Machine.Reserve(accountId);
        if (!refusal.Accepted) return false;
        try
        {
            var intent = new Intent(context, typeof(BlindBackupService));
            intent.PutExtra(ExtraAccount, accountId);
            context.StartForegroundService(intent);
        }
        catch
        {
            // The system would not start it: the request is taken back (a run that is going is never touched).
            Machine.Withdraw(accountId, refusal.Generation);
            throw;
        }
        return true;
    }

    /// <summary>Stops the service when it runs the backup of this account (a removal of the account).</summary>
    public static void StopFor(Context context, string accountId)
    {
        if (Machine.Holder == accountId) context.StopService(new Intent(context, typeof(BlindBackupService)));
    }

    public override void OnCreate()
    {
        base.OnCreate();
        _notifications = new BlindNotifications(this);
        _core = new BlindManualBackupService(Machine, this);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _core!.OnStart(intent?.GetStringExtra(ExtraAccount), startId);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _core?.OnDestroy();
        base.OnDestroy();
    }

    // ---- what the portable core asks of the platform -----------------------------------------------------------------------------------

    /// <summary>Every start must reach StartForeground (startForegroundService gives the service five seconds to do it), a repeated or refused one too. The notification names the account, as far as the host can say it right now; otherwise it is the neutral "Blind copies".</summary>
    void IBlindBackupServiceHost.ShowForeground(string? accountId) =>
        StartForeground(BlindNotifications.Id, _notifications!.Build(HolderName(accountId), "Backup", 0), ForegroundService.TypeDataSync);

    void IBlindBackupServiceHost.HideForeground() => StopForeground(StopForegroundFlags.Remove);

    // The latest start id: a start that arrived after the task ended keeps the service alive for its own task.
    void IBlindBackupServiceHost.StopAfter(int startId) => StopSelf(startId);

    async Task IBlindBackupServiceHost.RunAsync(string accountId, CancellationToken token)
    {
        BlindDevice? device = null;
        BlindHeavyWork? work = null;
        Action<string, double>? onProgress = null;
        try
        {
            device = IPlatformApplication.Current!.Services.GetRequiredService<BlindDevice>();
            if (!BlindAccountEntry.IsValidId(accountId)) return;
            // The host opens every account's database first; the controller then registers the job in the account's BlindActivity (a removal waits for it, and refuses a new one).
            await device.StartAsync(token);
            var name = device.Accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == accountId)?.Entry.Name;
            work = device.Accounts.ServicesFor(accountId).GetService<BlindHeavyWork>();
            onProgress = (title, p) => _notifications!.Show(name, title, p);
            if (work is not null) work.Progress += onProgress;
            // The account is known now: the notification published at the start (named if the host could say it then, neutral if not) is updated at once.
            _notifications!.Show(name, "Backup", 0);
            await device.Accounts.For(accountId).RunHeavyAsync(forceBackup: true, token);
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            // The account was removed meanwhile, or the device could not start: the account's own log has the job's reasons; this is the device's.
            device?.Log.Add("backup", $"The backup could not be started: {BlindRunReport.Reason(ex)}");
        }
        catch (System.OperationCanceledException)
        {
            // The service was stopped (the removal, the system): the job paused itself and says so.
        }
        finally
        {
            if (work != null && onProgress != null) work.Progress -= onProgress;
        }
    }

    /// <summary>The name of an account if the host already knows it (a snapshot is a memory read); null otherwise.</summary>
    private static string? HolderName(string? accountId)
    {
        if (accountId is null) return null;
        try
        {
            return IPlatformApplication.Current?.Services.GetService<BlindDevice>()?.Accounts.GetSnapshot().Accounts
                .FirstOrDefault(a => a.Entry.Id == accountId)?.Entry.Name;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// The one notification of the blind node's long jobs, with progress. The words are <see cref="BlindNotificationText"/>'s. It is PRIVATE on purpose: the full notification names the account (a source's name is
/// sensitive), and the system shows the PUBLIC version - the app and "Blind copies", nothing else - on a locked screen whose owner chose to hide sensitive content.
/// </summary>
internal sealed class BlindNotifications(Context context)
{
    public const int Id = 1101;
    private const string Channel = "bmb_blind";

    /// <param name="account">The account whose job this is, or null when none has been selected (the neutral words).</param>
    public Notification Build(string? account, string title, double progress)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "BeeMemoryBank blind copy", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)), PendingIntentFlags.Immutable);

        var publicVersion = new NotificationCompat.Builder(context, Channel);
        publicVersion.SetContentTitle(BlindNotificationText.AppName);
        publicVersion.SetContentText(BlindNotificationText.Public);
        publicVersion.SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo);

        var builder = new NotificationCompat.Builder(context, Channel);
        builder.SetContentTitle(BlindNotificationText.AppName);
        builder.SetContentText(BlindNotificationText.Body(account, title, progress));
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo);
        builder.SetProgress(100, (int)(progress * 100), progress <= 0);
        builder.SetContentIntent(open);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetVisibility(NotificationCompat.VisibilityPrivate);
        builder.SetPublicVersion(publicVersion.Build());
        return builder.Build()!;
    }

    /// <summary>The progress of an account's job; the account is named, so that with several copies on the phone the notification says whose it is.</summary>
    public void Show(string? account, string title, double progress) =>
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(Id, Build(account, title, progress));

    public ForegroundInfo ForegroundInfo(string? account, string title, double progress) =>
        new(Id, Build(account, title, progress), (int)ForegroundService.TypeDataSync);
}
