using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.BlindMobile;

public partial class App : Application
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly MigrationRunner _migrationRunner;

    public App(DbConnectionFactory dbFactory, MigrationRunner migrationRunner, BlindHomePage homePage)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _migrationRunner = migrationRunner;
        MainPage = homePage;
    }

    protected override async void OnStart()
    {
        base.OnStart();

        try
        {
            await _migrationRunner.RunMigrationsAsync();

            using var conn = _dbFactory.CreateConnection();
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Migration error: {ex}");
        }

        StartBlindWork();
    }

    /// <summary>Schedules the blind copy's sync and long jobs (WorkManager).</summary>
    public static void StartBlindWork()
    {
#if ANDROID
        Platforms.Android.BlindWorkScheduler.Ensure(Platform.AppContext);
#endif
    }
}
