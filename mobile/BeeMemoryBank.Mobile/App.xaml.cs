using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile;

public partial class App : Application
{
    private readonly InitializationService _initSvc;
    private readonly MigrationRunner _migrationRunner;
    private readonly DbConnectionFactory _dbFactory;
    private readonly FolderBootstrapper _folderBootstrapper;
    private readonly Services.SyncNotificationService _syncNotify;
    private readonly SessionService _session;
    // Singleton — safe to hold on the singleton App. Used to open a short-lived scope for the
    // scoped MediaBlobBackfillService in OnStart without capturing it as a captive dependency.
    private readonly IServiceScopeFactory _scopeFactory;

    public App(InitializationService initSvc, MigrationRunner migrationRunner, DbConnectionFactory dbFactory, FolderBootstrapper folderBootstrapper, Services.SyncNotificationService syncNotify, SessionService session, IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _initSvc = initSvc;
        _migrationRunner = migrationRunner;
        _dbFactory = dbFactory;
        _folderBootstrapper = folderBootstrapper;
        _syncNotify = syncNotify;
        _session = session;
        _scopeFactory = scopeFactory;
        MainPage = new ContentPage();
    }

    protected override async void OnStart()
    {
        base.OnStart();
        _syncNotify.SetForegroundMode(true);

        try
        {
            await _migrationRunner.RunMigrationsAsync();
            await _folderBootstrapper.RunIfNeededAsync();

            using var conn = _dbFactory.CreateConnection();
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Migration error: {ex}");
        }

        // Move any legacy .enc media into the content-addressed blob store and delete the redundant
        // files (item 16b), mirroring the server's startup task. Fire-and-forget so it never delays
        // the shell swap; runs in its own scope (MediaBlobBackfillService is scoped) and works on a
        // locked vault — it only moves ciphertext, no decryption.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var media = scope.ServiceProvider.GetRequiredService<MediaBlobBackfillService>();
                await media.BackfillAsync();
                await media.SweepRedundantEncFilesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Media blob backfill error: {ex}");
            }
        });

        MainPage = new AppShell();
        _session.Locked -= OnSessionLocked;
        _session.Locked += OnSessionLocked;

        if (!await _initSvc.IsInitializedAsync())
        {
            Shell.Current.GoToAsync("//setup").FireAndForget();
        }
        else
        {
            // Unlock first. UnlockPage routes through PostUnlockRouter,
            // which sends the user to //initialSync if a prior join never
            // completed its first sync.
            Shell.Current.GoToAsync("//unlock").FireAndForget();
        }
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        _syncNotify.SetForegroundMode(false);

        // Auto-lock when the app leaves the foreground. Without this, an
        // attacker who grabs an unlocked phone (snatch-and-run, "show me
        // a photo") gets full vault access via the recent-apps switcher.
        // Re-navigation happens in OnResume so the user lands on UnlockPage.
        if (_session.IsUnlocked) _session.Lock();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _syncNotify.SetForegroundMode(true);

        // After OnSleep locked the session, route the user to the unlock
        // page. Skip if the app is in setup or already on /unlock to avoid
        // loops during first-run.
        if (!_session.IsUnlocked) RouteToUnlock();
    }

    // Leave the content pages the moment the vault locks, not only on the next OnResume: when
    // Android recreates the activity (memory pressure, "Don't keep activities") the new window
    // shows the Shell's last page without an OnResume ever firing, and the user saw the
    // article list of a locked vault ("Session is locked" on open). With the Shell already on
    // //unlock, every way back into the app lands on the unlock page.
    private void OnSessionLocked()
    {
        MainThread.BeginInvokeOnMainThread(RouteToUnlock);
    }

    private static void RouteToUnlock()
    {
        try
        {
            var shell = Shell.Current;
            if (shell == null) return;

            var route = shell.CurrentState?.Location?.OriginalString ?? "";
            if (!route.Contains("unlock", StringComparison.OrdinalIgnoreCase) &&
                !route.Contains("setup", StringComparison.OrdinalIgnoreCase))
            {
                shell.GoToAsync("//unlock").FireAndForget();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Routing to unlock failed: {ex}");
        }
    }

    public static void StartSyncService()
    {
#if ANDROID
        var intent = new Android.Content.Intent(Platform.AppContext, typeof(Platforms.Android.SyncForegroundService));
        Platform.AppContext.StartForegroundService(intent);
        ScheduleSyncWork();
#endif
    }

    // WorkManager backstop: guarantees a periodic sync even when the OEM kills the foreground service.
    public static void ScheduleSyncWork()
    {
#if ANDROID
        Platforms.Android.SyncWorkScheduler.Ensure(Platform.AppContext);
#endif
    }

    public static void StopSyncService()
    {
#if ANDROID
        var intent = new Android.Content.Intent(Platform.AppContext, typeof(Platforms.Android.SyncForegroundService));
        Platform.AppContext.StopService(intent);
#endif
    }
}

file static class TaskExtensions
{
    public static void FireAndForget(this Task task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.Exception != null)
                System.Diagnostics.Debug.WriteLine($"FireAndForget error: {t.Exception}");
        });
    }
}
