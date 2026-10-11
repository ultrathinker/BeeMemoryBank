using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>Where the node lives in the app's container: the database and the media folder, both inside <see cref="DataDirectory"/>.</summary>
public sealed record FullNodePaths(string DataDirectory)
{
    public string Database => Path.Combine(DataDirectory, "beememorybank.db");

    public string Media => Path.Combine(DataDirectory, "media");
}

/// <summary>
/// The full node of the iPhone app, in process: the registrations the Android ordinary app makes (storage, core, sync with the pinned
/// default client, the join by code), without the model and without a listening server. Platform-free, so the tests build the very same
/// composition on any OS.
/// </summary>
public static class FullNodeServices
{
    public static IServiceCollection AddFullNode(this IServiceCollection services, FullNodePaths paths)
    {
        Directory.CreateDirectory(paths.Media);

        // No model on the phone (docs/full-node/IOS.md): every caller takes its "no embeddings" path, as on a node whose model is missing.
        services.AddSingleton<IEmbeddingGenerator, NoModelEmbeddingGenerator>();

        services
            .AddStorage(paths.Database)
            .AddCore()
            .AddSync()
            .AddSingleton(new MediaStorageOptions(paths.Media))
            .AddLogging();

        // Every sync caller takes this default client: peers with a pinned key on that key only, HTTPS only, no redirects - the same
        // composition as the Android app (MobileSyncPinningGuardTests holds that one).
        services.AddHttpClient(string.Empty).UsePinnedSyncHandler();

        // The join by code downloads its snapshot over the join's own key-pinned connection (NodeSetupService).
        services.AddTransient<Func<HttpClient, SnapshotJoinClient>>(sp => http =>
            new SnapshotJoinClient(
                http,
                sp.GetRequiredService<DbConnectionFactory>(),
                paths.DataDirectory,
                sp.GetRequiredService<ILogger<SnapshotJoinClient>>(),
                protocolPolicy: sp.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>()));
        services.AddScoped<NodeSetupService>();
        return services;
    }

    /// <summary>
    /// What every start does before a screen opens, as the Android app's: the schema, the system folders, the journal, and following a
    /// superadmin peer's master-key rotation (the phone has no admin screen to accept one). On a plain phone this is the whole start; on an
    /// encrypted one the first part runs here and the second at every open of the database (<see cref="AddOpenWork"/>, spec 5.8).
    /// </summary>
    public static async Task PrepareAsync(IServiceProvider services)
    {
        SweepStaging(services);
        await MigrateAsync(services);
        await BootstrapFoldersAsync(services);
        await PragmasAndClockAsync(services);
    }

    /// <summary>What a join killed half-way left in the staging folder (the downloaded archive, the extracted database): removed at the next start. Touches no database.</summary>
    public static void SweepStaging(IServiceProvider services) =>
        SnapshotStaging.Sweep(Path.GetDirectoryName(services.GetRequiredService<MediaStorageOptions>().MediaDir)!);

    /// <summary>
    /// The start work that reads the database, as the open steps the vault gate runs at every open of an encrypted phone (spec 5.3 table 2, 5.8):
    /// 100 the schema, 200 the system folders, 800 the pragmas, the rotation auto-arm and the Lamport clock.
    /// </summary>
    public static IServiceCollection AddOpenWork(this IServiceCollection services)
    {
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(100, (sp, _) => MigrateAsync(sp)));
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(200, (sp, _) => BootstrapFoldersAsync(sp)));
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(800, (sp, _) => PragmasAndClockAsync(sp)));
        return services;
    }

    private static Task MigrateAsync(IServiceProvider services) => services.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

    private static Task BootstrapFoldersAsync(IServiceProvider services) => services.GetRequiredService<FolderBootstrapper>().RunIfNeededAsync();

    private static async Task PragmasAndClockAsync(IServiceProvider services)
    {
        using (var conn = services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
            await PhoneRotationAutoArm.ArmAsync(conn);
        }

        // The Lamport clock continues from the newest event this node holds; a fresh process starts it at zero otherwise.
        using var scope = services.CreateScope();
        var newest = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
        services.GetRequiredService<LamportClock>().Initialize(newest);
    }
}

/// <summary>The embedding generator of a phone without the model: it answers like a node whose model is missing.</summary>
public sealed class NoModelEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 0;

    public string Version => "none (no model on this iPhone)";

    public float[] Generate(string text) =>
        throw new ModelUnavailableException("This iPhone has no model: search by meaning runs on the computers and servers.");
}
