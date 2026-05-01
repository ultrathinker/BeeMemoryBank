using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Mobile;

public partial class App : Application
{
    private readonly InitializationService _initSvc;
    private readonly MigrationRunner _migrationRunner;
    private readonly DbConnectionFactory _dbFactory;
    private readonly FolderBootstrapper _folderBootstrapper;
    private readonly Services.SyncNotificationService _syncNotify;
    private readonly SessionService _session;

    public App(InitializationService initSvc, MigrationRunner migrationRunner, DbConnectionFactory dbFactory, FolderBootstrapper folderBootstrapper, Services.SyncNotificationService syncNotify, SessionService session)
    {
        InitializeComponent();
        _initSvc = initSvc;
        _migrationRunner = migrationRunner;
        _dbFactory = dbFactory;
        _folderBootstrapper = folderBootstrapper;
        _syncNotify = syncNotify;
        _session = session;
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

        MainPage = new AppShell();

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
        if (!_session.IsUnlocked && Shell.Current != null)
        {
            var route = Shell.Current.CurrentState?.Location?.OriginalString ?? "";
            if (!route.Contains("unlock", StringComparison.OrdinalIgnoreCase) &&
                !route.Contains("setup", StringComparison.OrdinalIgnoreCase))
            {
                Shell.Current.GoToAsync("//unlock").FireAndForget();
            }
        }
    }

    public static void StartSyncService()
    {
#if ANDROID
        var intent = new Android.Content.Intent(Platform.AppContext, typeof(Platforms.Android.SyncForegroundService));
        Platform.AppContext.StartForegroundService(intent);
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
