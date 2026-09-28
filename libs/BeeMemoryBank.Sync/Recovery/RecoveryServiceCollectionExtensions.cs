using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Blind-node recovery services every full host needs — server, desktop node, phone and CLI all go
/// through <see cref="DependencyInjection.AddSync"/>, and every one of them writes key slots.
/// </summary>
public static class RecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddRecovery(this IServiceCollection services)
    {
        // A full Api node registers the peers' my-standing over this default (PeerOwnStanding).
        services.TryAddSingleton<IOwnStandingProvider, UnknownOwnStanding>();
        services.AddScoped<RecoveryEventPublisher>();
        // Replaces Core's NullRecoveryBoxPublisher: from here on a slot change is announced.
        services.AddScoped<IRecoveryBoxPublisher, DeviceBoxPublisher>();
        services.AddScoped<IRecoveryReconciler, RecoveryReconciler>();
        services.AddScoped<SealedSecretService>();
        services.AddScoped<StateAnchorService>();
        services.AddScoped<RecoverySetBuilder>();
        return services;
    }
}
