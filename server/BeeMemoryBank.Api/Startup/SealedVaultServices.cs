using BeeMemoryBank.Api.Services;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// The composition of a full node's sealed-database support (docs/design/step-b-spec.md 5, 17.1): the gate, the boot store and its pieces, the
/// relay of a closed node, the open steps and the keyed chat factory. Called by <c>Program.cs</c> right after <c>AddBeeApiServices</c>; not on a blind
/// host. On a plain node the gate is a plain gate (every member answers as if the database were always open, the factories are the factories of
/// 2.5), no boot store file is created, and nothing here runs until the owner starts "Encrypt this node".
/// </summary>
public static class SealedVaultServices
{
    public static void AddSealedVault(this WebApplicationBuilder builder, string dataPath, SealedStartState start)
    {
        if (new EnvironmentNodeRole(builder.Configuration["BMB_ROLE"]).IsBlind) return;
        var services = builder.Services;

        services.AddSingleton(start);
        services.AddBootStore(dataPath);
        services.AddSealedUnlockMaterial();
        services.AddSealedRelay();
        services.AddSealedAutoUnlockHost();
        // The server checkbox is the owner's, off by default and read once here at start; a plain server may use it too (spec 11.2).
        services.AddServerAutoUnlock(dataPath, startAtHostStart: false);

        // The gate is the page-key source of both keyed factories. It asks for the factories, the steps, the relay and the mirror only when it
        // needs them (they depend on the gate in turn), so the cycle is a call, not a construction.
        services.AddSingleton(sp =>
        {
            var gate = new VaultGate(
                sp.GetRequiredService<BootStore>(),
                sp.GetRequiredService<IDatabaseKeyService>(),
                () => SealedFiles(sp, dataPath),
                () => sp.GetServices<IVaultOpenStep>().ToList(),
                () => sp.GetService<ISealedRelayLifecycle>(),
                () => sp.GetService<IUnlockMaterialMirror>(),
                sp.GetService<ILogger<VaultGate>>(),
                start.Mode == VaultStoreMode.Sealed ? VaultStoreMode.Sealed : VaultStoreMode.Plain);
            // No unlock opens a node while a replaced plain file is readable; each unlock tries to erase it first (review round 1, H1).
            gate.PlainLeftovers = () => SealSwapResolver.Leftovers(dataPath).Count == 0 ? [] : sp.GetRequiredService<SealMigrationService>().ClearLeftovers();
            return gate;
        });
        services.AddSingleton<IVaultGate>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<IVaultKeySource>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedVaultKeys>(sp => sp.GetRequiredService<VaultGate>());
        services.AddSingleton<ISealedUnlockDiagnostics>(sp => sp.GetRequiredService<VaultGate>());

        // The chat database is keyed by the same gate; the registration of AddBeeApiServices built it without a key source.
        services.Replace(ServiceDescriptor.Singleton(sp => new ChatDbConnectionFactory(dataPath, sp.GetRequiredService<IVaultKeySource>())));

        // What the auto-unlock services call to open a sealed node: the session's own unlock with a master DEK (spec 4.7).
        services.AddSingleton<SealedDekUnlock>(sp => (dek, door, ct) => sp.GetRequiredService<SessionService>().TryUnlockWithDekAsync(dek, door, ct));

        // "Encrypt this node" (spec 10): the migration and what the Api adds to it. A plain node runs it on the owner's word or, when it is being set
        // up, on the setup's own (FreshNodeSealing); a sealed one only reports "already_sealed" from its preflight.
        services.AddSingleton<ISealMigrationHost>(sp => new ApiSealMigrationHost(sp));
        var killAfter = builder.Environment.IsEnvironment("Testing") ? builder.Configuration["BMB_SEAL_TEST_KILL_AFTER"] : null;
        services.AddSingleton(sp =>
        {
            var seal = new SealMigrationService(dataPath, sp.GetRequiredService<VaultGate>(), sp.GetRequiredService<SessionService>(),
                sp.GetRequiredService<IDatabaseKeyService>(), sp.GetRequiredService<BootStore>(), sp.GetRequiredService<IUnlockMaterialMirror>(),
                sp.GetRequiredService<ISealMigrationHost>(), () => SealedFiles(sp, dataPath), sp.GetRequiredService<MaintenanceModeService>(),
                sp.GetService<SyncScheduler>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<SealMigrationService>>());
            if (!string.IsNullOrEmpty(killAfter))
            {
                // The three real process kills of the kill-point tests (spec 15.3.7): the Testing environment only, never configured in a product.
                void Die(string step) { if (step == killAfter) System.Diagnostics.Process.GetCurrentProcess().Kill(); }
                seal.StepHook = Die;
                seal.SwapStepHook = Die;
            }
            return seal;
        });

        // What every setup ends with (BMB-306): the new node's metadata encryption on and its database encrypted, under the master password just given.
        services.AddScoped<FreshNodeSealing>();
        // The marker a setup, join or restore writes before its first write in the open and FreshNodeSealing removes when the node is encrypted: a start
        // that finds it still there wipes what was written (VaultStartup.Enter -> SealIntent.Resolve).
        services.AddSingleton<ISealIntents>(sp => new SealIntents(dataPath, () => SealIntents.HoldsNoNode(sp.GetRequiredService<DbConnectionFactory>())));

        // The start tasks that read the database, as work done at every open (spec 5.3 table 2).
        services.AddSingleton<IVaultOpenStep>(sp => new MigrationsOpenStep(sp));
        services.AddSingleton<IVaultOpenStep>(sp => new FolderBootstrapOpenStep(sp));
        services.AddSingleton<IVaultOpenStep>(sp => new ChatSchemaOpenStep(sp));
        services.AddSingleton<IVaultOpenStep>(sp => new LamportClockOpenStep(sp));
        services.AddSingleton<IVaultOpenStep>(sp => new CrashSweepsOpenStep(sp, dataPath));
        services.AddSingleton<IVaultOpenStep>(sp => new MediaBackfillOpenStep(sp));
        services.AddSingleton<IVaultOpenStep>(sp => new ConceptTagBackfillOpenStep(sp));
    }

    private static List<SealedFile> SealedFiles(IServiceProvider sp, string dataPath)
    {
        var vault = sp.GetRequiredService<DbConnectionFactory>();
        var chat = sp.GetRequiredService<ChatDbConnectionFactory>();
        return
        [
            new SealedFile(DbPurposes.Vault, Path.Combine(dataPath, VaultStoreFiles.Database), vault, vault.CreateConnection),
            new SealedFile(DbPurposes.Chat, Path.Combine(dataPath, VaultStoreFiles.Chat), chat, chat.CreateConnection),
        ];
    }

    /// <summary>
    /// The pieces that cannot take the gate in their constructors (they are built by hand in <c>AddBeeApiServices</c>): the auto-unlock services
    /// learn how a sealed node opens, and the snapshot service gets the boot-store hooks of a restore (spec 4.7, 8.4). Called once, at start.
    /// </summary>
    public static void AttachSealedNode(this WebApplication app)
    {
        var services = app.Services;
        if (services.GetService<IVaultGate>() is null) return;
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var mirror = services.GetService<IUnlockMaterialMirror>();
        var host = services.GetService<ISealedAutoUnlockHost>();
        services.GetService<BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService>()?.AttachSealedNode(factory, mirror, host);
        services.GetService<BeeMemoryBank.Infrastructure.OsAutoUnlock.UpdateUnlockHandoff>()?.AttachSealedNode(host);
        if (mirror is not null && services.GetService<SnapshotService>() is { } snapshots)
            snapshots.SealedHooks ??= new MirrorRestoreHooks(mirror);
    }
}
