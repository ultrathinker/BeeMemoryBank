using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Cli;

public static class CliServiceProvider
{
    /// <summary>
    /// The Windows installer ships the CLI in cli\ next to api\ without its own model.onnx; point the
    /// resolver at the API's copy. An explicit BMB_ONNX_MODEL_PATH or a bundled model always wins.
    /// </summary>
    internal static void UseSiblingApiModelIfNotBundled()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ModelManager.ModelPathEnvironmentVariable))) return;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "model.onnx"))) return;
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "api", "model.onnx"));
        if (File.Exists(sibling))
            Environment.SetEnvironmentVariable(ModelManager.ModelPathEnvironmentVariable, sibling);
    }

    /// <summary>
    /// Creates a DI container for CLI commands, runs migrations and initializes Lamport clock.
    /// The caller is responsible for calling Dispose() on the returned ServiceProvider.
    /// </summary>
    /// <param name="openSealed">False: a sealed folder is left closed (the caller opens it itself, for instance with a recovery key).</param>
    public static async Task<ServiceProvider> CreateAsync(string dataPath, bool openSealed = true)
    {
        // The vault gate (review release-b R1-2): refused while a re-key runs, an interrupted swap finished first, and
        // vault.lease held shared as long as the provider lives, so the re-key refuses while this command runs.
        var (resolution, vaultLease) = BeeMemoryBank.AppPaths.VaultStartup.Enter(dataPath);
        dataPath = resolution.DataDir;

        // A folder that cannot start (docs/design/step-b-spec.md 2.5, 10.6) is refused by name, as the node refuses it: nothing is opened or created.
        // A boot store beside a plain database is the one broken state a command line can work in: the database is plain and stays plain.
        if (BrokenStart.For(dataPath, resolution.Seal) is { } broken && broken.Reason != "boot_beside_plain_db")
        {
            vaultLease.Dispose();
            throw new CliVaultException(broken.Message);
        }
        var sealedDir = CliSealed.IsSealed(dataPath);
        Directory.CreateDirectory(dataPath);
        UseSiblingApiModelIfNotBundled();

        var services = Compose(dataPath, sealedDir)
            // By a factory, so disposing the provider releases the lease; resolved at once so it is held from here.
            .AddSingleton<BeeMemoryBank.AppPaths.VaultLease>(_ => vaultLease)
            .BuildServiceProvider();
        services.GetRequiredService<BeeMemoryBank.AppPaths.VaultLease>();

        var opened = !sealedDir;
        if (sealedDir && openSealed)
        {
            // No migration at construction (spec 5.9): the node made the schema; the password opens the database for this command.
            try { await CliSealed.OpenAsync(services); }
            catch
            {
                await services.DisposeAsync();
                throw;
            }
            opened = true;
        }

        if (opened)
        {
            using var scope = services.CreateScope();
            if (!sealedDir) await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

            // Restore Lamport clock from DB
            var maxTs = await scope.ServiceProvider
                .GetRequiredService<IEventLogRepository>()
                .GetMaxLamportTimestampAsync();
            services.GetRequiredService<LamportClock>().Initialize(maxTs);
        }

        return services;
    }

    /// <summary>
    /// The services of a sealed folder for <c>bmb rekey</c> (docs/design/step-b-spec.md 13.2): no lease (the re-key takes the exclusive one itself),
    /// no migration, no unlock. Null when the folder is not sealed.
    /// </summary>
    public static ServiceProvider? CreateSealedForRekey(string dataPath) =>
        CliSealed.IsSealed(dataPath) ? Compose(dataPath, sealedDir: true).BuildServiceProvider() : null;

    private static IServiceCollection Compose(string dataPath, bool sealedDir)
    {
        var collection = new ServiceCollection();
        if (sealedDir) collection.AddSealedCli(dataPath);   // the gate is registered first: the connection factories ask it for their key
        return collection
            // Console-backed logging at Warning+ level. Surfaces lazy-rewrap warnings,
            // signature mismatches, and other security-relevant signals directly to the
            // operator running the CLI — never a null sink, which would swallow exactly
            // those intentional warnings. Info+ would be too chatty for a CLI.
            .AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                              .SetMinimumLevel(LogLevel.Warning))
            .AddStorage(dataPath)
            .AddCore()
            .AddOnnxEmbeddings(dataPath)
            .AddSync()
            .AddSingleton<IActorProvider>(new CliActorProvider())
            // Where the API keeps media (its AddMediaStorage: {data}/media). Not used by any CLI verb itself; it
            // is here so that services which take it (HardDeleteService, behind the recovery-box publisher that
            // UserService and KeyManagementService take) can be built at all — without it
            // `bmb user reset-password` could not resolve UserService. The folder is not created.
            .AddSingleton(new BeeMemoryBank.Core.Services.MediaStorageOptions(Path.Combine(dataPath, "media")))
            // `bmb init reset` shares Core's NodeResetService with the API endpoint so the two
            // cannot drift on what "wipe" means. No INodeResetHook here — chat.db is an Api-side
            // concern and does not exist for CLI-only deployments.
            .AddScoped(sp => ActivatorUtilities.CreateInstance<Core.Services.NodeResetService>(sp, dataPath));
    }
}
