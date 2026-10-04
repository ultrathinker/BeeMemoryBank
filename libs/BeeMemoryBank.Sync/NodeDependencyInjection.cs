using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The sync registrations every node needs, a blind node included: the clock, the trigger, the event applier, the sync client,
/// the pinned-TLS client handler and the recovery sets a node receives and serves. What is NOT here is chosen by the host, because it
/// differs by kind of node: the event logger, the node-auth signer, the restore initiator and the DEK rotation applier. A full node
/// takes them from <c>AddSync</c> (BeeMemoryBank.Vault); the blind host registers its own. There is deliberately no default for
/// them: the event applier cannot be built without an <see cref="IRestoreInitiator"/> and an <see cref="IDekRotationApplier"/>,
/// so a host that forgets one fails when the container resolves it - it is not silently left with a no-op.
/// </summary>
public static class NodeDependencyInjection
{
    public static IServiceCollection AddNodeSync(this IServiceCollection services)
    {
        services.AddSingleton<SnapshotRequiredState>();
        services.AddSingleton<PeerNewerProtocolState>();

        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());

        services.AddSingleton<SyncTrigger>();
        services.AddSingleton<ISyncTrigger>(sp => sp.GetRequiredService<SyncTrigger>());

        services.AddScoped<EventApplier>();
        services.AddScoped<SyncClient>();
        services.AddScoped<HardDeleteService>();

        // Registered here (Sync's own DI) rather than Storage's registrations, unlike the other
        // repositories this project consumes — this one is Sync-specific (only ever consumed by
        // SyncEventQuarantine/SyncClient and the GET+DELETE /api/sync/quarantine endpoints), so it
        // stays colocated with its only consumer, the same way EventApplier/SyncClient
        // themselves are registered here rather than in Storage.
        services.AddScoped<ISyncQuarantineRepository, BeeMemoryBank.Storage.Sqlite.SyncQuarantineRepository>();

        // Which peers present a pinned, self-signed TLS key (plan 4.4). Every client that dials a
        // sync peer builds its handler from this, so the pins apply on every node alike.
        services.AddSingleton<Blind.SpkiPinRegistry>();
        // Pinned TLS keys (plan 4.4) for the scheduler's client on every host that syncs — server,
        // CLI and the phone's foreground service alike: a peer whose whitelist row carries tls_spki
        // (a blind node with its self-signed certificate) is accepted on that key and nothing else.
        services.AddHttpClient(SyncScheduler.HttpClientName).UsePinnedSyncHandler();

        // The recovery sets a node receives, validates and serves (creating and opening recovery boxes is vault code).
        // A full Api node registers the peers' my-standing over this default (PeerOwnStanding).
        services.TryAddSingleton<IOwnStandingProvider, UnknownOwnStanding>();
        services.AddScoped<RecoverySetBuilder>();

        return services;
    }

    /// <summary>
    /// Adds the background sync scheduler. Registered as itself as well as a hosted service: a flow that is about to delete this
    /// node's tables asks the loop to stand down first (SyncScheduler.PauseAsync — the wipe and the reseed cutover both do), and
    /// it cannot ask a type the container does not hand out.
    /// </summary>
    public static IServiceCollection AddNodeSyncScheduler(this IServiceCollection services, TimeSpan? interval = null, Func<IServiceProvider, Action?>? periodicCleanupFactory = null)
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

    /// <summary>Adds the background periodic cleanup service.</summary>
    public static IServiceCollection AddNodeCleanupService(this IServiceCollection services, TimeSpan? interval = null)
    {
        services.AddHostedService(sp =>
            new CleanupService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<CleanupService>>(),
                interval));
        return services;
    }
}
