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
        services.AddSingleton<PeerNewerProtocolState>();

        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());

        services.AddSingleton<SyncTrigger>();
        services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncTrigger>());

        services.AddScoped<IEventLogger, EventLogger>();
        services.AddScoped<EventApplier>();
        services.AddScoped<SyncClient>();
        services.AddScoped<HardDeleteService>();

        // M5: registered here (Sync's own DI) rather than Storage's AddStorage(), unlike the other
        // repositories this project consumes — this one is Sync-specific (only ever consumed by
        // SyncEventQuarantine/SyncClient and the GET+DELETE /api/sync/quarantine endpoints), so it
        // stays colocated with its only consumer, the same way EventLogger/EventApplier/SyncClient
        // themselves are registered here rather than in Storage.
        services.AddScoped<Core.Interfaces.ISyncQuarantineRepository, BeeMemoryBank.Storage.Sqlite.SyncQuarantineRepository>();

        // WP-11: the search index lifecycle. IndexBuilder and SearchIndexRuntimeState are process-
        // lifetime singletons (the in-memory index itself, and the internal-segment-id -> persisted
        // Guid map / rebuild coordination lock must survive across PendingIndexProcessor's per-cycle
        // scopes); SearchIndexLifecycleService is scoped like its repository/store dependencies.
        services.AddSingleton<BeeMemoryBank.Search.Indexing.IndexBuilder>();
        services.AddSingleton<Search.SearchIndexRuntimeState>();
        services.AddScoped<Search.SearchIndexLifecycleService>();

        // Default sync-auth signer derives the node key via the master DEK. Mobile overrides
        // this with a Keystore-backed signer so background backup-sync works while locked.
        services.TryAddScoped<INodeAuthSigner, SessionNodeAuthSigner>();

        // ILazySlotRewrapService is needed by SessionService.UnlockAsync to handle
        // post-DEK-rotation slot rewrap (when a node didn't auto-accept eagerly).
        // Registering here means CLI/mobile/server all share the same impl — without
        // this, CLI's bmb commands fail with "invalid password" after a network DEK
        // rotation since slot can't be rewrapped against the new DEK.
        services.AddSingleton<ILazySlotRewrapService, LazySlotRewrapService>();

        // Restore stays a no-op off-server: initiating a network restore is a server-side flow.
        // EventApplier takes it as a constructor dependency, so something must be registered.
        services.TryAddSingleton<IRestoreInitiator, NoOpRestoreInitiator>();
        // The REAL peer applier, not a no-op. A mobile or CLI node must rewrap its own vault when
        // a peer rotates the master DEK; with the no-op it stayed on the retired key forever and
        // everything that synced afterwards was silently unreadable. The server registers its own
        // DekRotationService over this (AddSingleton beats TryAddSingleton) because it also
        // proposes, accepts and reports progress — but both run the identical rewrap.
        services.TryAddSingleton<IDekRotationApplier, DekRotation.PeerDekRotationApplier>();

        return services;
    }

    // The NoOp logs a loud warning when invoked. EventApplier requires the handler to be
    // activate-able; on the server the real implementation is registered in Program.cs via
    // AddSingleton (which wins over TryAddSingleton). If a future refactor accidentally drops
    // that override, restore events would be silently swallowed without this warning — a
    // security-relevant regression
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
    /// Adds the background pending embeddings processor. Also registered as itself (not just as
    /// IHostedService) so the admin one-shot backfill endpoint can inject the same singleton
    /// instance and call <see cref="PendingEmbeddingProcessor.DrainAllPendingAsync"/> directly.
    /// </summary>
    public static IServiceCollection AddEmbeddingProcessor(this IServiceCollection services, TimeSpan? interval = null, int? batchSize = null)
    {
        services.AddSingleton(sp =>
            new PendingEmbeddingProcessor(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PendingEmbeddingProcessor>>(),
                interval,
                batchSize));
        services.AddHostedService(sp => sp.GetRequiredService<PendingEmbeddingProcessor>());
        return services;
    }

    /// <summary>
    /// WP-11: adds the background pending search-index processor. Requires AddStorage() (for
    /// EncryptedSegmentStore/SegmentManifestRepository/SegmentTombstoneRepository) and AddSync()
    /// (for the IndexBuilder/SearchIndexLifecycleService registrations above) to have already run.
    /// Also registered as itself, same reason as <see cref="AddEmbeddingProcessor"/> above.
    /// </summary>
    public static IServiceCollection AddIndexProcessor(this IServiceCollection services, TimeSpan? interval = null, int? batchSize = null)
    {
        services.AddSingleton(sp =>
            new PendingIndexProcessor(
                sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PendingIndexProcessor>>(),
                interval,
                batchSize));
        services.AddHostedService(sp => sp.GetRequiredService<PendingIndexProcessor>());
        return services;
    }
}
