using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Services.Sealed;

public static class SealedRelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the relay of a closed node (<see cref="ISealedRelay"/>, docs/design/step-b-spec.md 6.1) with its limits, and the lifecycle
    /// the gate calls at open and close (<see cref="AddSealedRelayLifecycle"/>). Needs <c>AddBootStore</c> and <see cref="SyncTokenStore"/>.
    /// Called by the sealed-vault composition (track 3), which maps the request set of <see cref="ISealedRelay.Handles"/> to it.
    /// </summary>
    public static IServiceCollection AddSealedRelay(this IServiceCollection services)
    {
        services.AddSealedRelayLifecycle();
        services.TryAddSingleton<SealedRelayLimits>(_ => new SealedRelayLimits());
        services.TryAddSingleton<ISealedRelay, SealedRelay>();
        return services;
    }
}
