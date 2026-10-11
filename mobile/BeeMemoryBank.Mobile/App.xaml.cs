using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
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
    // The root provider: PhoneStart / PhoneVaultLifecycle are registered only when the phone starts through the vault lifecycle (Step B).
    private readonly IServiceProvider _services;

    public App(InitializationService initSvc, MigrationRunner migrationRunner, DbConnectionFactory dbFactory, FolderBootstrapper folderBootstrapper, Services.SyncNotificationService syncNotify, SessionService session, IServiceScopeFactory scopeFactory, IServiceProvider services)
    {
        InitializeComponent();
        _initSvc = initSvc;
        _migrationRunner = migrationRunner;
        _dbFactory = dbFactory;
        _folderBootstrapper = folderBootstrapper;
        _syncNotify = syncNotify;
        _session = session;
        _scopeFactory = scopeFactory;
        _services = services;
        MainPage = new ContentPage();
    }

    protected override async void OnStart()
    {
        base.OnStart();
        _syncNotify.SetForegroundMode(true);
        Services.MauiPhoneEnvironment.Foreground = true;

        // An encrypted phone, or a folder that cannot start: the database is not read until the unlock page has opened it (spec 5.8, 12.1);
        // what OnStart does below runs then, as the vault gate's open steps (Services/AndroidOpenWork.cs).
        if (_services.GetService<PhoneStart>() is { } phoneStart && (!phoneStart.CanStart || phoneStart.Mode == VaultStoreMode.Sealed))
        {
            await StartWithoutDatabaseAsync(phoneStart);
            return;
        }

        try
        {
            // What a join killed half-way left in the staging folder (the downloaded archive, the extracted database): removed at the next start.
            BeeMemoryBank.Core.IO.SnapshotStaging.Sweep(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            await _migrationRunner.RunMigrationsAsync();
            await _folderBootstrapper.RunIfNeededAsync();

            using var conn = _dbFactory.CreateConnection();
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");

            // The phone has no admin screen to approve a DEK rotation, so without this it never
            // applies one: every article written after a rotation stays unreadable on it
            // (AuthTagMismatch), and what it writes stays on the old key (BMB-31, scenario 20).
            // A superadmin peer already holds the master key, so following its rotations adds no
            // trust the phone had not given it. Runs at every start so a superadmin peer learned
            // since is covered too; a rotation left pending is applied at the next unlock.
            await PhoneRotationAutoArm.ArmAsync(conn);
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

    private async Task StartWithoutDatabaseAsync(PhoneStart start)
    {
        if (!start.CanStart)
        {
            // The reason, in words (spec 2.5); nothing was opened, created, changed or deleted.
            MainPage = new ContentPage { Content = new ScrollView { Content = new Label { Text = start.Broken!.Message, Padding = 24, FontSize = 16 } } };
            return;
        }

        try
        {
            BeeMemoryBank.Core.IO.SnapshotStaging.Sweep(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            await PhoneVaultLifecycle.PrepareSealedStartAsync(_services);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Sealed start error: {ex}");
        }

        MainPage = new AppShell();
        _session.Locked -= OnSessionLocked;
        _session.Locked += OnSessionLocked;
        Shell.Current.GoToAsync("//unlock").FireAndForget();
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        _syncNotify.SetForegroundMode(false);
        Services.MauiPhoneEnvironment.Foreground = false;

        // Auto-lock when the app leaves the foreground. Without this, an
        // attacker who grabs an unlocked phone (snatch-and-run, "show me
        // a photo") gets full vault access via the recent-apps switcher.
        // Re-navigation happens in OnResume so the user lands on UnlockPage.
        // On an encrypted phone the lock also closes the database (spec 5.4, 12.1); the lifecycle does both.
        if (_session.IsUnlocked)
        {
            if (_services.GetService<PhoneVaultLifecycle>() is { } lifecycle) _ = lifecycle.OnBackgroundAsync();
            else _session.Lock();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        _syncNotify.SetForegroundMode(true);
        Services.MauiPhoneEnvironment.Foreground = true;

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
