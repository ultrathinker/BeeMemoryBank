using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
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
    public static async Task<ServiceProvider> CreateAsync(string dataPath)
    {
        // The vault gate (review release-b R1-2): refused while a re-key runs, an interrupted swap finished first, and
        // vault.lease held shared as long as the provider lives, so the re-key refuses while this command runs.
        var (resolution, vaultLease) = BeeMemoryBank.AppPaths.VaultStartup.Enter(dataPath);
        dataPath = resolution.DataDir;
        Directory.CreateDirectory(dataPath);
        UseSiblingApiModelIfNotBundled();

        var services = new ServiceCollection()
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
            .AddScoped(sp => ActivatorUtilities.CreateInstance<Core.Services.NodeResetService>(sp, dataPath))
            // By a factory, so disposing the provider releases the lease; resolved at once so it is held from here.
            .AddSingleton<BeeMemoryBank.AppPaths.VaultLease>(_ => vaultLease)
            .BuildServiceProvider();
        services.GetRequiredService<BeeMemoryBank.AppPaths.VaultLease>();

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

            // Restore Lamport clock from DB
            var maxTs = await scope.ServiceProvider
                .GetRequiredService<IEventLogRepository>()
                .GetMaxLamportTimestampAsync();
            services.GetRequiredService<LamportClock>().Initialize(maxTs);
        }

        return services;
    }
}
