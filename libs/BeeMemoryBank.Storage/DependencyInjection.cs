using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Storage;

public static class DependencyInjection
{
    public static IServiceCollection AddStorage(this IServiceCollection services, string dataPath)
    {
        DapperConfig.Configure();

        var factory = new DbConnectionFactory(dataPath);
        services.AddSingleton(factory);
        services.AddSingleton<Core.Interfaces.IDbConnectionFactory>(factory);
        services.AddSingleton<MigrationRunner>();

        // WP-09: encrypted-at-rest search index segments. Files live in a sibling directory next
        // to the sqlite DB (dataPath may itself be a directory or a ".db" file path -- mirror
        // DbConnectionFactory's own handling of both shapes rather than duplicating its logic).
        var segmentsDirectory = Path.Combine(
            Path.GetExtension(dataPath)?.Equals(".db", StringComparison.OrdinalIgnoreCase) == true
                ? Path.GetDirectoryName(dataPath) ?? dataPath
                : dataPath,
            "search-index-segments");
        services.AddScoped<SegmentManifestRepository>();
        services.AddScoped<SegmentTombstoneRepository>();
        services.AddScoped(sp => new EncryptedSegmentStore(
            sp.GetRequiredService<SegmentManifestRepository>(),
            sp.GetRequiredService<SessionService>(),
            segmentsDirectory));

        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
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
        services.AddScoped<IProjectionMatrixRepository, ProjectionMatrixRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IFolderAclRepository, FolderAclRepository>();
        services.AddScoped<IArticleVersionRepository, ArticleVersionRepository>();
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
