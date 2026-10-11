using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BeeMemoryBank.Storage.Sealed;

namespace BeeMemoryBank.Storage.Boot;

public static class BootStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the boot store of the data directory <paramref name="dataPath"/> (docs/design/step-b-spec.md 2.3): the connection factory
    /// (never an <c>IDbConnectionFactory</c>), the file, and the four stores. Registering them creates nothing on disk; a plain node never
    /// calls <see cref="IBootStore.EnsureCreatedAsync"/> and so never has the file. Called by the sealed-vault composition (track 3).
    /// </summary>
    public static IServiceCollection AddBootStore(this IServiceCollection services, string dataPath)
    {
        var path = Path.Combine(dataPath, VaultStoreFiles.Boot);
        services.TryAddSingleton(new BootStoreConnectionFactory(path));
        services.TryAddSingleton<BootStore>(sp => new BootStore(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<IBootStore>(sp => sp.GetRequiredService<BootStore>());
        services.TryAddSingleton<BootDoorStore>(sp => new BootDoorStore(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<IBootDoorStore>(sp => sp.GetRequiredService<BootDoorStore>());
        services.TryAddSingleton<BootDbKeyStore>(sp => new BootDbKeyStore(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<IBootDbKeyStore>(sp => sp.GetRequiredService<BootDbKeyStore>());
        services.TryAddSingleton<BootMirrorStore>(sp => new BootMirrorStore(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<IBootMirrorStore>(sp => sp.GetRequiredService<BootMirrorStore>());
        services.TryAddSingleton<BootStoreWriter>(sp => new BootStoreWriter(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<RelayInbox>(sp => new RelayInbox(sp.GetRequiredService<BootStoreConnectionFactory>()));
        services.TryAddSingleton<IRelayInbox>(sp => sp.GetRequiredService<RelayInbox>());
        return services;
    }
}
