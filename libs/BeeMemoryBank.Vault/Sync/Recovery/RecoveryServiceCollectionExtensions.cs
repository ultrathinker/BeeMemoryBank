using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Recovery services every full host needs — server, desktop node, phone and CLI all go
/// through <see cref="DependencyInjection.AddSync"/>, and every one of them writes key slots. They create and open recovery
/// boxes with the master key. The part every node takes (the recovery set builder, the default "my standing") is
/// <c>AddNodeSync</c> in the shared Sync.
/// </summary>
public static class RecoveryServiceCollectionExtensions
{
    public static IServiceCollection AddRecovery(this IServiceCollection services)
    {
        services.AddScoped<RecoveryEventPublisher>();
        // Replaces Core's NullRecoveryBoxPublisher: from here on a slot change is announced.
        services.AddScoped<IRecoveryBoxPublisher, DeviceBoxPublisher>();
        services.AddScoped<IRecoveryReconciler, RecoveryReconciler>();
        services.AddScoped<SealedSecretService>();
        services.AddScoped<StateAnchorService>();
        return services;
    }
}
