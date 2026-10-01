using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Composes the minimal service collection for the Android blind node.
/// Invariant: Vault services (SessionService, ArticleService, KeyManagementService,
/// TreeService, SearchService, MediaService, RestoreService) and vault repositories
/// (article, blob, key slot, search index segments, retired DEK) are NOT registered.
/// </summary>
public static class BlindMobileServices
{
    public static IServiceCollection ConfigureServices(IServiceCollection services, string dataDir, string dbPath)
    {
        DapperConfig.Configure();

        // Minimal SQLite infrastructure and blind-specific repositories:
        services.AddSingleton(_ => new DbConnectionFactory(dbPath));
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<DbConnectionFactory>());
        services.AddSingleton<MigrationRunner>();

        services.AddSingleton<INodeIdentityRepository, NodeIdentityRepository>();
        services.AddSingleton<IWhitelistRepository, WhitelistRepository>();
        services.AddScoped<IEventLogRepository, EventLogRepository>();

        services.AddLogging();

        // Pinned HTTP client and maintenance detection
        services.AddTransient<MaintenanceDetectingHandler>();
        services.AddTransient<BlindHttpHandler>();
        services.AddSingleton<BlindHttpClientProvider>();
        services.AddSingleton<IHttpClientFactory>(sp => sp.GetRequiredService<BlindHttpClientProvider>());
        services.AddTransient<HttpClient>(sp => sp.GetRequiredService<BlindHttpClientProvider>().GetClient());

        // Blind node state, keys, pairing, and pending sources
        services
            .AddSingleton<IBlindPhoneStore, PreferencesBlindStore>()
            .AddSingleton<BlindPhoneState>()
            .AddSingleton(_ => new BlindPhoneLog(BlindPaths.Log(dataDir), TimeProvider.System))
            .AddSingleton<SqliteBlindIdentityRecorder>()
            .AddSingleton<IBlindIdentityRecorder>(sp => sp.GetRequiredService<SqliteBlindIdentityRecorder>())
            .AddSingleton<IBlindReplicaSource, PendingBlindReplicaSource>()
            .AddSingleton<IBlindPhoneSync, PendingBlindPhoneSync>()
            .AddSingleton<IBlindPackageSource, PendingBlindPackageSource>()
            .AddSingleton<IRecoverySetJsonSource, PendingRecoverySetSource>()
            .AddSingleton<BlindMobilePairing>()
            .AddSingleton(sp => new BlindPhoneBackupRunner(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindPhoneKeys>(),
                sp.GetRequiredService<IBlindPackageSource>(),
                sp.GetRequiredService<IRecoverySetJsonSource>(),
                sp.GetRequiredService<IDeviceStateProvider>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Backups(dataDir), TimeProvider.System))
            .AddSingleton(sp => new BlindHeavyWork(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindReplicaSource>(),
                sp.GetRequiredService<BlindPhoneBackupRunner>(),
                sp.GetRequiredService<IDeviceStateProvider>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Replica(dataDir), TimeProvider.System));

        return services;
    }
}
