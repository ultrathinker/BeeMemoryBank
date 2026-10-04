using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Blind;

/// <summary>
/// The container registrations of a blind node's library half. Written out here - not the AddStorage / AddCore /
/// AddSync of the full application, which register the whole application - so that a service which is not part of a
/// blind node is not registered, and its implementation is not in this assembly at all.
///
/// <para>Derived from those three methods (tools/blind-link/registrations.py lists what each registration needs and
/// whether it is linked). What is left out: the search index and its segment store, article/folder/tree/user/role/key
/// management, import and export, restore of a vault, the projection matrix, favourites, agents, article versions,
/// the embedding processors. The same lifetimes as the originals, for the same reasons (the comments of the full
/// application's registrations apply here as well).</para>
///
/// <para>Also left out since the vault split (BMB-99, 2.0.1): everything that holds or uses a master key - <c>SessionService</c>,
/// <c>CommentService</c>, <c>MediaService</c>, <c>RemoteAccountService</c>, the master-key and content crypto, the event logger that signs
/// with the DEK (a refusing <c>BlindEventLogger</c> stands in), the recovery publisher / reconciler / state-anchor service. That code is
/// <c>BeeMemoryBank.Vault</c>, which a blind node does not contain. docs/blind-node/COMPOSITION.md lists what stays shared and why.</para>
/// </summary>
public static class BlindComposition
{
    public static IServiceCollection AddBlindComposition(this IServiceCollection services, string dataPath)
    {
        DapperConfig.Configure();

        // Storage. Factory delegates, not instances: the container disposes only what it creates, and
        // DbConnectionFactory.Dispose clears the SQLite connection pool (see AddStorage).
        services.AddSingleton(_ => new DbConnectionFactory(dataPath));
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<DbConnectionFactory>());
        services.AddSingleton<MigrationRunner>();

        services.AddSingleton<EmbeddingVectorCache>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddSingleton<ChunkEmbeddingVectorCache>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
        services.AddScoped<IBlobRepository, BlobRepository>();
        services.AddSingleton<IKeySlotRepository, KeySlotRepository>();
        // Singletons because the singleton SnapshotService consumes them; stateless (see AddStorage).
        services.AddSingleton<INodeIdentityRepository, NodeIdentityRepository>();
        services.AddSingleton<IWhitelistRepository, WhitelistRepository>();
        services.AddScoped<IEventLogRepository, EventLogRepository>();
        services.AddScoped<ISyncPositionRepository, SyncPositionRepository>();
        services.AddScoped<ISyncPushPositionRepository, SyncPushPositionRepository>();
        services.AddScoped<ITombstoneRepository, TombstoneRepository>();
        services.AddScoped<IConflictVersionRepository, ConflictVersionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IFolderAclRepository, FolderAclRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRoleAclRepository, RoleAclRepository>();
        services.AddScoped<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IRestoreReplayShieldRepository, RestoreReplayShieldRepository>();
        services.AddScoped<IRestoreEventStateRepository, RestoreEventStateRepository>();
        services.AddScoped<IDekRotationStateRepository, DekRotationStateRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IRemoteAccountRepository, RemoteAccountRepository>();
        services.AddScoped<IRemoteSubscriptionRepository, RemoteSubscriptionRepository>();
        services.AddScoped<IRemoteApiTokenRepository, RemoteApiTokenRepository>();
        services.AddScoped<FolderBootstrapper>();
        services.TryAddScoped<ICallerScopeStore, InstanceCallerScopeStore>();
        services.AddScoped<CallerScopeHolder>();

        // Core. No SessionService, CommentService, MediaService, RemoteAccountService: they hold or use the master key
        // and are vault code (BeeMemoryBank.Vault), which a blind node does not contain.
        services.AddSingleton<InvisibleModeService>();
        services.AddSingleton<MaintenanceModeService>();
        services.AddSingleton<SearchMetrics>();
        services.TryAddSingleton<IActorProvider>(new NullActorProvider());
        services.AddScoped<MediaBlobBackfillService>();
        services.AddScoped<FolderAccessService>();
        // A blind node stores the ciphertext of vectors and never computes one; nothing here can hold a model.
        services.AddSingleton<IEmbeddingGenerator, BlindEmbeddingGenerator>();
        services.AddScoped<ConceptTagService>();

        // Sync.
        services.AddSingleton<SnapshotRequiredState>();
        services.AddSingleton<PeerNewerProtocolState>();
        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());
        services.AddSingleton<SyncTrigger>();
        services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncTrigger>());
        // A blind node authors nothing: its event logger refuses (the full EventLogger signs with the master DEK and is vault code).
        services.AddScoped<IEventLogger, BlindEventLogger>();
        services.AddScoped<EventApplier>();
        // No IRemoteSentinelVerifier: the master-DEK sentinel check is made only by a node that holds the DEK.
        services.AddScoped<SyncClient>();
        services.AddScoped<HardDeleteService>();
        services.AddScoped<ISyncQuarantineRepository, SyncQuarantineRepository>();
        // Signs with the external (v=2) identity key from IExternalNodeKey; never derives a key through the master DEK.
        services.TryAddScoped<INodeAuthSigner, ExternalKeyNodeAuthSigner>();
        // Which peers present a pinned, self-signed TLS key (plan 4.4): every client that dials a sync peer builds its
        // handler from this.
        services.AddSingleton<SpkiPinRegistry>();
        services.AddHttpClient(SyncScheduler.HttpClientName).UsePinnedSyncHandler();

        // Recovery: the part a blind node takes part in (receiving, validating and serving recovery sets). Creating and
        // opening recovery boxes (publisher, reconciler, state anchors, lazy slot re-wrap) handles the master key: vault code.
        services.TryAddSingleton<IOwnStandingProvider, UnknownOwnStanding>();
        services.AddScoped<RecoverySetBuilder>();
        return services;
    }

    /// <summary>The sync loop. Registered as itself as well as a hosted service: a flow that is about to delete this
    /// node's tables asks the loop to stand down first (the wipe and the reseed cutover do).</summary>
    public static IServiceCollection AddBlindSyncScheduler(this IServiceCollection services,
        TimeSpan? interval = null, Func<IServiceProvider, Action?>? periodicCleanupFactory = null)
    {
        services.AddSingleton(sp => new SyncScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SyncScheduler>>(),
            sp.GetRequiredService<ISyncTrigger>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            interval,
            periodicCleanupFactory?.Invoke(sp),
            sp.GetRequiredService<SnapshotRequiredState>()));
        services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());
        return services;
    }

    /// <summary>The background periodic cleanup.</summary>
    public static IServiceCollection AddBlindCleanupService(this IServiceCollection services, TimeSpan? interval = null)
    {
        services.AddHostedService(sp => new CleanupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<CleanupService>>(),
            interval));
        return services;
    }
}
