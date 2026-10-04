using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Core;

/// <summary>
/// The full node's Core registrations: everything every node needs (<see cref="NodeDependencyInjection.AddNodeCore"/>, in the shared
/// Core) plus the session and the services that open, create, search and manage content. Every full host (Api, Web, Cli, desktop node,
/// Mobile, Migrator, SeedGen) calls this one, so none of them changed when the shared part was split off (BMB-99).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<SessionService>();
        services.AddNodeCore();

        // Singleton: the query cache is shared across all requests/scopes so concurrent identical
        // searches coalesce onto one in-flight task and near-repeat hits are served from the TTL
        // cache. ACL safety comes from the scope fingerprint embedded in each cache key.
        services.AddSingleton<SearchQueryCache>();

        // Null implementations are replaced with real ones when BeeMemoryBank.Sync / Api / Cli is registered
        services.TryAddSingleton<ILamportClock, NullLamportClock>();
        services.TryAddScoped<IEventLogger, NullEventLogger>();
        services.TryAddScoped<IRecoveryBoxPublisher, NullRecoveryBoxPublisher>();

        services.AddScoped<RestoreBootstrapMarker>();
        services.AddScoped<InitializationService>();
        services.AddScoped<ArticleService>();
        services.AddScoped<ArticleDiffService>();
        services.AddScoped<KeyManagementService>();
        services.AddScoped<TreeService>();
        services.AddScoped<SearchService>();
        services.AddScoped<FolderService>();
        services.AddScoped<CopyService>();
        services.AddScoped<CommentService>();
        services.AddScoped<MediaService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<ObsidianImportService>();
        services.AddScoped<BeeImportService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        services.AddScoped<RemoteAccountService>();
        services.AddScoped<RemoteEventApplier>();
        return services;
    }
}
