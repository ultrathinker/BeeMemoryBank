using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Storage;

/// <summary>
/// The storage registrations every node needs, a blind node included: the connection factory, the migration runner and the
/// repositories of the replicated data model. The full node's <c>AddStorage</c> (BeeMemoryBank.Vault) calls this and adds
/// what only it uses: the encrypted search-index segments, the retired master keys, agents, favourites, article versions and the
/// projection matrix.
/// </summary>
public static class NodeDependencyInjection
{
    public static IServiceCollection AddNodeStorage(this IServiceCollection services, string dataPath)
    {
        DapperConfig.Configure();

        // Registered through a factory delegate, NOT as a pre-built instance. The DI container
        // only disposes what it creates itself: an object handed to AddSingleton(instance) is
        // never disposed, so DbConnectionFactory.Dispose — which clears the SQLite connection
        // pool — would never run. Every pooled handle would stay open on the database file, and
        // on Windows "delete the data directory" fails long after the provider is gone.
        // Both registrations resolve the same instance; Dispose is idempotent.
        services.AddSingleton(_ => new DbConnectionFactory(dataPath));
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<DbConnectionFactory>());
        services.AddSingleton<MigrationRunner>();

        // Process-wide semantic-search vector cache, shared by every scoped
        // ArticleRepository instance (see EmbeddingVectorCache's own doc comment for why this
        // must be a singleton — without this registration, ArticleRepository's optional
        // constructor parameter would fall back to `new EmbeddingVectorCache(factory)` per
        // scope, silently defeating cross-request caching entirely).
        services.AddSingleton<EmbeddingVectorCache>();
        services.AddScoped<IArticleRepository, ArticleRepository>();

        // Same reasoning as EmbeddingVectorCache above, for the chunk-embedding cache.
        // ChunkEmbeddingVectorCache queries the DB directly (not through
        // IArticleChunkEmbeddingRepository) rather than depending on the repository interface --
        // see its constructor comment for why (the repository's write path needs to call
        // Invalidate() on this cache, and a two-way dependency isn't resolvable by the container).
        services.AddSingleton<ChunkEmbeddingVectorCache>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
        services.AddScoped<IBlobRepository, BlobRepository>();
        services.AddSingleton<IKeySlotRepository, KeySlotRepository>();
        // Singleton (not Scoped) — these repos are pulled into the singleton SnapshotService
        // factory in Api/Program.cs. Resolving a scoped service from a singleton throws under
        // ASPNETCORE_ENVIRONMENT=Development (scope validation enabled), which is exactly the
        // mode README's "From Source" Quick Start recommends. They're stateless (primary
        // ctor + singleton DbConnectionFactory + fresh connection per method), so Singleton
        // is semantically equivalent — just compatible with how Api/Program.cs consumes them.
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
        services.AddSingleton<IRestoreReplayShieldRepository, RestoreReplayShieldRepository>();  // see comment near INodeIdentityRepository — same reason
        services.AddScoped<IRestoreEventStateRepository, RestoreEventStateRepository>();
        services.AddScoped<IDekRotationStateRepository, DekRotationStateRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IRemoteAccountRepository, RemoteAccountRepository>();
        services.AddScoped<IRemoteSubscriptionRepository, RemoteSubscriptionRepository>();
        services.AddScoped<IRemoteApiTokenRepository, RemoteApiTokenRepository>();
        services.AddScoped<FolderBootstrapper>();
        services.TryAddScoped<ICallerScopeStore, InstanceCallerScopeStore>();
        services.AddScoped<CallerScopeHolder>();

        return services;
    }
}
