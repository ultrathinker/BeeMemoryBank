using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Core;

/// <summary>
/// The Core registrations every node needs, a blind node included. The full node's <c>AddCore</c> (BeeMemoryBank.Vault)
/// calls this and adds the session and the services that open, create and search content.
/// </summary>
public static class NodeDependencyInjection
{
    public static IServiceCollection AddNodeCore(this IServiceCollection services)
    {
        services.AddSingleton<InvisibleModeService>();
        services.AddSingleton<MaintenanceModeService>();

        // Singleton so the rolling latency/result-count windows are process-wide and every request
        // feeds the same admin-visible numbers. Records only timings + coarse result-count buckets
        // + fixed labels -- never query text or content (see SearchMetrics doc comment).
        services.AddSingleton<SearchMetrics>();

        // The null implementation is replaced with a real one when a host that authors events is registered
        services.TryAddSingleton<IActorProvider>(new NullActorProvider());

        services.AddScoped<MediaBlobBackfillService>();
        services.AddScoped<FolderAccessService>();
        services.AddScoped<ConceptTagService>();
        return services;
    }
}
