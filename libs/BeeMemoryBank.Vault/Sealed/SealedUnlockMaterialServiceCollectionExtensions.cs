using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

public static class SealedUnlockMaterialServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database-key service, the unlock-material mirror (as <see cref="IUnlockMaterialMirror"/> and itself) and its watcher
    /// (docs/design/step-b-spec.md 3, 4.3). Needs <see cref="BootStoreServiceCollectionExtensions.AddBootStore"/>, the vault's
    /// <see cref="IDbConnectionFactory"/> and <see cref="SessionService"/>; <see cref="IVaultGate"/> and <see cref="ISealedVaultKeys"/>
    /// (track 3) are optional: without a gate the mirror does nothing, which is a plain node. Called by the sealed-vault composition
    /// (track 3) after <c>AddBootStore</c>.
    /// </summary>
    public static IServiceCollection AddSealedUnlockMaterial(this IServiceCollection services)
    {
        services.TryAddSingleton<IDatabaseKeyService>(sp => new DatabaseKeyService(
            sp.GetRequiredService<IBootStore>(), sp.GetRequiredService<IBootDbKeyStore>(), sp.GetService<ILogger<DatabaseKeyService>>()));
        services.TryAddSingleton<UnlockMaterialMirror>(sp => new UnlockMaterialMirror(
            sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<BootStore>(),
            sp.GetRequiredService<BootDoorStore>(),
            sp.GetRequiredService<BootDbKeyStore>(),
            sp.GetRequiredService<BootStoreWriter>(),
            sp.GetService<IVaultGate>(),
            sp.GetService<ISealedVaultKeys>(),
            // The session is resolved when a rewrite needs it, not at construction: the services that write doors (UserService and the
            // like) take the mirror, and the session takes them back through the post-unlock work.
            () => sp.GetRequiredService<SessionService>().GetCandidateDeks(),
            sp.GetService<ILogger<UnlockMaterialMirror>>()));
        services.TryAddSingleton<IUnlockMaterialMirror>(sp => sp.GetRequiredService<UnlockMaterialMirror>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, UnlockMaterialWatcher>());
        return services;
    }
}
