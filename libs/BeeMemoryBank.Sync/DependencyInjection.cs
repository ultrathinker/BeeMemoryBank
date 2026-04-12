using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync;

public static class DependencyInjection
{
    public static IServiceCollection AddSync(this IServiceCollection services)
    {
        // Replace null implementations from Core with real ones
        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());

        services.AddSingleton<SyncTrigger>();
        services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncTrigger>());

        services.AddScoped<IEventLogger, EventLogger>();
        services.AddScoped<EventApplier>();
        services.AddScoped<SyncClient>();
        services.AddScoped<WhitelistRevokeBackfill>();

        return services;
    }

    /// <summary>
    /// Adds the background sync scheduler.
    /// Called from API/CLI where IHostedService is available.
    /// </summary>
    public static IServiceCollection AddSyncScheduler(this IServiceCollection services, TimeSpan? interval = null, Func<IServiceProvider, Action?>? periodicCleanupFactory = null)
    {
        services.AddHttpClient("SyncScheduler");
        services.AddHostedService(sp =>
            new SyncScheduler(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncScheduler>>(),
                sp.GetRequiredService<ISyncTrigger>(),
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                interval,
                periodicCleanupFactory?.Invoke(sp)));
        return services;
    }

    /// <summary>
    /// Adds the background periodic cleanup service.
    /// </summary>
    public static IServiceCollection AddCleanupService(this IServiceCollection services, TimeSpan? interval = null)
    {
        services.AddHostedService(sp =>
            new CleanupService(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CleanupService>>(),
                interval));
        return services;
    }

    /// <summary>
    /// Adds the background pending embeddings processor.
    /// </summary>
    public static IServiceCollection AddEmbeddingProcessor(this IServiceCollection services, TimeSpan? interval = null)
    {
        services.AddHostedService(sp =>
            new PendingEmbeddingProcessor(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PendingEmbeddingProcessor>>(),
                interval));
        return services;
    }
}
