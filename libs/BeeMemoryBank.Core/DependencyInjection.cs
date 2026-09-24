using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<SessionService>();
        services.AddSingleton<InvisibleModeService>();
        services.AddSingleton<MaintenanceModeService>();

        // Singleton: the query cache is shared across all requests/scopes so concurrent identical
        // searches coalesce onto one in-flight task and near-repeat hits are served from the TTL
        // cache. ACL safety comes from the scope fingerprint embedded in each cache key.
        services.AddSingleton<SearchQueryCache>();

        // Singleton so the rolling latency/result-count windows are process-wide and every request
        // feeds the same admin-visible numbers. Records only timings + coarse result-count buckets
        // + fixed labels -- never query text or content (see SearchMetrics doc comment).
        services.AddSingleton<SearchMetrics>();

        // Null implementations are replaced with real ones when BeeMemoryBank.Sync / Api / Cli is registered
        services.TryAddSingleton<ILamportClock, NullLamportClock>();
        services.TryAddScoped<IEventLogger, NullEventLogger>();
        services.TryAddSingleton<IActorProvider>(new NullActorProvider());

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
        services.AddScoped<MediaBlobBackfillService>();
        services.AddScoped<UserService>();
        services.AddScoped<FolderAccessService>();
        services.AddScoped<RoleService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<ObsidianImportService>();
        services.AddScoped<BeeImportService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        services.AddScoped<RemoteAccountService>();
        services.AddScoped<RemoteEventApplier>();
        return services;
    }
}