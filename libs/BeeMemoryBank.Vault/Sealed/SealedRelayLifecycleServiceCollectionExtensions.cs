using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

public static class SealedRelayLifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the relay's lock, the importer (as <see cref="ISealedRelayLifecycle"/> and itself), the adapter over <c>EventApplier</c> it
    /// replays through, and the maintenance timers (docs/design/step-b-spec.md 6.3, 6.6, 6.8). Needs <c>AddBootStore</c> and the node's
    /// scoped services (<c>EventApplier</c> and the repositories); <see cref="IVaultGate"/> and <see cref="IUnlockMaterialMirror"/> are
    /// optional. Called by the sealed-vault composition (track 3).
    /// </summary>
    public static IServiceCollection AddSealedRelayLifecycle(this IServiceCollection services)
    {
        services.TryAddSingleton<RelayWriteLock>();
        services.TryAddScoped<IRelayEventApplier, EventApplierRelayAdapter>();
        services.TryAddSingleton<RelayInboxImporter>(sp => new RelayInboxImporter(
            sp.GetRequiredService<IBootStore>(),
            sp.GetRequiredService<IRelayInbox>(),
            sp.GetRequiredService<RelayWriteLock>(),
            sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IUnlockMaterialMirror>(),
            sp.GetService<IVaultGate>(),
            sp.GetService<ILogger<RelayInboxImporter>>()));
        services.TryAddSingleton<ISealedRelayLifecycle>(sp => sp.GetRequiredService<RelayInboxImporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RelayMaintenance>());
        return services;
    }
}
