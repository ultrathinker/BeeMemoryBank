using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The full node's sync registrations: everything every node needs (<see cref="NodeDependencyInjection.AddNodeSync"/>, in the shared
/// Sync) plus the event logger that signs with the master key, the session-backed node-auth signer, the search-index lifecycle, the
/// master-DEK sentinel check, lazy slot re-wrap, the DEK rotation applier and the recovery services that create and open boxes.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddSync(this IServiceCollection services)
    {
        services.AddNodeSync();

        services.AddScoped<IEventLogger, EventLogger>();
        // The master-DEK sentinel check of SyncClient (a blind node registers none and skips the check).
        services.TryAddScoped<IRemoteSentinelVerifier, RemoteSentinelVerifier>();

        // The search index lifecycle. IndexBuilder and SearchIndexRuntimeState are process-
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
        // a peer rotates the master DEK; with a no-op it would stay on the retired key forever and
        // everything synced afterwards would be silently unreadable. The server registers its own
        // DekRotationService over this (AddSingleton beats TryAddSingleton) because it also
        // proposes, accepts and reports progress — but both run the identical rewrap.
        services.TryAddSingleton<IDekRotationApplier, DekRotation.PeerDekRotationApplier>();

        services.AddRecovery();

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
        => services.AddNodeSyncScheduler(interval, periodicCleanupFactory);

    /// <summary>
    /// Adds the background periodic cleanup service.
    /// </summary>
    public static IServiceCollection AddCleanupService(this IServiceCollection services, TimeSpan? interval = null)
        => services.AddNodeCleanupService(interval);

    /// <summary>
    /// Adds the background pending search-index processor. Requires AddStorage() (for
    /// EncryptedSegmentStore/SegmentManifestRepository/SegmentTombstoneRepository) and AddSync()
    /// (for the IndexBuilder/SearchIndexLifecycleService registrations above) to have already run.
    /// </summary>
    public static IServiceCollection AddIndexProcessor(this IServiceCollection services, TimeSpan? interval = null, int? batchSize = null)
    {
        services.AddSingleton(sp =>
            new PendingIndexProcessor(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<PendingIndexProcessor>>(),
                interval,
                batchSize));
        services.AddHostedService(sp => sp.GetRequiredService<PendingIndexProcessor>());
        return services;
    }
}
