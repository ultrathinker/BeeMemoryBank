using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
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

        // Narrow receive-only sync composition. This deliberately does not call AddStorage() or
        // AddSync(): those wire vault/session, search-index, scheduler and cleanup subsystems that
        // cannot exist on a blind phone. These are only EventApplier's replicated-row dependencies.
        services.AddScoped<ICallerScopeStore, InstanceCallerScopeStore>();
        services.AddScoped<CallerScopeHolder>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
        services.AddScoped<IBlobRepository, BlobRepository>();
        services.AddScoped<ISyncPositionRepository, SyncPositionRepository>();
        services.AddScoped<ITombstoneRepository, TombstoneRepository>();
        services.AddScoped<IConflictVersionRepository, ConflictVersionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IRestoreReplayShieldRepository, RestoreReplayShieldRepository>();
        services.AddScoped<IRestoreEventStateRepository, RestoreEventStateRepository>();
        services.AddScoped<IDekRotationStateRepository, DekRotationStateRepository>();
        services.AddScoped<ISyncQuarantineRepository, SyncQuarantineRepository>();
        services.AddSingleton(new MediaStorageOptions(Path.Combine(dataDir, "media")));
        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());
        services.AddScoped<IEventLogger, NullEventLogger>();
        services.AddScoped<HardDeleteService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<FolderAccessService>();
        services.AddSingleton<BlindState>();
        services.AddSingleton<BlindRestoreInitiator>();
        services.AddSingleton<IRestoreInitiator>(sp => sp.GetRequiredService<BlindRestoreInitiator>());
        services.AddScoped<IDekRotationApplier, BlindDekRotationApplier>();
        services.AddSingleton<IEmbeddingGenerator, BlindEmbeddingGenerator>();
        services.AddScoped<EventApplier>();

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
            .AddSingleton<INodeAuthSigner, BlindKeystoreNodeAuthSigner>()
            .AddSingleton(sp => new BlindPhoneReplicaClient(
                sp.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<INodeAuthSigner>(), dataDir, dbPath,
                sp.GetRequiredService<ILogger<BlindPhoneReplicaClient>>()))
            .AddSingleton<IBlindReplicaSource, BlindReplicaSource>()
            .AddSingleton<BlindPhonePullClient>()
            .AddSingleton<IBlindPhoneSync, BlindPhoneSync>()
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
