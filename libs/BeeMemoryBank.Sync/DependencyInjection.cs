using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public static class DependencyInjection
{
    public static IServiceCollection AddSync(this IServiceCollection services)
    {
        services.AddSingleton<SnapshotRequiredState>();

        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());

        services.AddSingleton<SyncTrigger>();
        services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncTrigger>());

        services.AddScoped<IEventLogger, EventLogger>();
        services.AddScoped<EventApplier>();
        services.AddScoped<SyncClient>();
        services.AddScoped<HardDeleteService>();

        // ILazySlotRewrapService is needed by SessionService.UnlockAsync to handle
        // post-DEK-rotation slot rewrap (when a node didn't auto-accept eagerly).
        // Registering here means CLI/mobile/server all share the same impl — without
        // this, CLI's bmb commands fail with "invalid password" after a network DEK
        // rotation since slot can't be rewrapped against the new DEK.
        services.AddSingleton<ILazySlotRewrapService, LazySlotRewrapService>();

        // Default no-op implementations of restore/dek-rotation initiators for environments
        // (mobile, CLI) that don't have the server-side handlers. The server's Program.cs
        // overrides these via AddSingleton later. EventApplier needs both as constructor
        // dependencies; without these the DI container fails to activate it.
        services.TryAddSingleton<IRestoreInitiator, NoOpRestoreInitiator>();
        services.TryAddSingleton<IDekRotationApplier, NoOpDekRotationApplier>();

        return services;
    }

    // Both NoOps log a loud warning when invoked. EventApplier requires these handlers
    // to be activate-able; on the server the real implementations are registered in
    // Program.cs via AddSingleton (which wins over TryAddSingleton). If a future
    // refactor accidentally drops that override, restore/rotation events would be
    // silently swallowed without these warnings — a security-relevant regression
    // (peers think DEK rotated; this node still uses old DEK). The warning forces
    // the misconfiguration into operator logs immediately.
    private sealed class NoOpRestoreInitiator(ILogger<NoOpRestoreInitiator> logger) : IRestoreInitiator
    {
        public Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent)
        {
            logger.LogWarning(
                "NoOpRestoreInitiator invoked for event {EventId} — server's real IRestoreInitiator is NOT registered. " +
                "RESTORE_NETWORK event will be persisted in event log but NOT applied. This is a server config bug.",
                eventId);
            return Task.CompletedTask;
        }
        public Task RetryPendingRestoresAsync() => Task.CompletedTask;
    }

    private sealed class NoOpDekRotationApplier(ILogger<NoOpDekRotationApplier> logger) : IDekRotationApplier
    {
        public Task AutoAcceptCommitAsync(SyncEvent commitEvent)
        {
            logger.LogWarning(
                "NoOpDekRotationApplier.AutoAcceptCommitAsync invoked for event {EventId} — server's real IDekRotationApplier is NOT registered. " +
                "DEK rotation will NOT be applied; node will fall behind cluster on next rotation.",
                commitEvent.EventId);
            return Task.CompletedTask;
        }
        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
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
                periodicCleanupFactory?.Invoke(sp),
                sp.GetRequiredService<SnapshotRequiredState>()));
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
