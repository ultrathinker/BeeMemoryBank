using BeeMemoryBank.Core.Interfaces;
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

    public App(InitializationService initSvc, MigrationRunner migrationRunner, DbConnectionFactory dbFactory, FolderBootstrapper folderBootstrapper, Services.SyncNotificationService syncNotify)
    {
        InitializeComponent();
        _initSvc = initSvc;
        _migrationRunner = migrationRunner;
        _dbFactory = dbFactory;
        _folderBootstrapper = folderBootstrapper;
        _syncNotify = syncNotify;
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
            // Go to unlock page so user can enter password to decrypt content
            Shell.Current.GoToAsync("//unlock").FireAndForget();
        }
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        _syncNotify.SetForegroundMode(false);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _syncNotify.SetForegroundMode(true);
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
