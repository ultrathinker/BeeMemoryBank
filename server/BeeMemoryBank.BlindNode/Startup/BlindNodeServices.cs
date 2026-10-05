using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.BlindNode.Startup;

/// <summary>
/// Everything a blind node registers in the container: the blind half of BeeMemoryBank.Api's ApiServices.cs,
/// written out on its own. A line that exists in the Api only for a full node (the web UI's session, MCP, the
/// embedding model, chat, update, DEK rotation, mDNS) has no counterpart here, so it cannot be reached by a
/// request or a startup task - the role is the type of the host, not a switch read at runtime.
/// </summary>
public static class BlindNodeServices
{
    public static void AddBlindNodeServices(this WebApplicationBuilder builder, string dataPath)
    {
        var services = builder.Services;

        // The role is a fact about this process; libraries ask for it (a blind node never authors events).
        services.AddSingleton<INodeRole>(new EnvironmentNodeRole("blind"));

        // The library half: the shared modules every node registers - storage, the services of the replicated data model, the
        // sync machinery - and the pieces that only a blind node has. The libraries it references are the shared ones: nothing of the
        // vault (BeeMemoryBank.Vault) is in this host's closure, so nothing of it can be registered by accident.
        services.AddNodeStorage(dataPath);
        services.AddNodeCore();
        services.AddNodeSync();
        // A blind node stores the ciphertext of vectors and never computes one; nothing here can hold a model.
        services.AddSingleton<IEmbeddingGenerator, BlindEmbeddingGenerator>();
        // A blind node authors nothing: its event logger refuses (the full EventLogger signs with the master DEK and is vault code).
        services.AddScoped<IEventLogger, BlindEventLogger>();
        // Signs with the external (v=2) identity key from IExternalNodeKey; never derives a key through the master DEK.
        services.TryAddScoped<INodeAuthSigner, ExternalKeyNodeAuthSigner>();
        services.AddMemoryCache();
        AddBlindRecovery(services);
        services.AddSingleton<SyncTokenStore>();
        // Per-node, not per-process: see SyncChallengeRateLimiter.
        services.AddSingleton<SyncEndpoints.SyncChallengeRateLimiter>();
        services.AddSingleton<IPublicHostValidator, DnsPublicHostValidator>();

        // BMB_SYNC_INTERVAL_SECONDS: override of the scheduler tick (default 60 s); tests set it low.
        TimeSpan? syncInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_SYNC_INTERVAL_SECONDS"), out var s) && s >= 1
            ? TimeSpan.FromSeconds(s) : null;
        services.AddNodeSyncScheduler(interval: syncInterval, periodicCleanupFactory: sp =>
            sp.GetRequiredService<SyncTokenStore>().CleanupExpired);
        services.AddNodeCleanupService();

        services.AddHttpClient();
        services.AddTransient<HttpClient>(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        // Outbound calls to a caller-supplied URL must not follow redirects (probe-relay): a host that passes the
        // check can otherwise 302 the request onto loopback.
        services.AddHttpClient(SyncEndpoints.NoRedirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpContextAccessor();

        // The key a blind node signs its packages with; it holds no master DEK and encrypts nothing.
        BlindRoleServices.AddSnapshotService(services, dataPath,
            sp => new ExternalKeySnapshotKeyOperations(sp.GetRequiredService<IExternalNodeKey>()));

        // Blind node (BMB-54): /api/blind/status base fields, backups, jobs/CPU modes, console login, wipe.
        services.AddBlindNodeServices(dataPath);
        services.AddSingleton<SnapshotJoinCache>();
        BlindRoleServices.AddMediaStorage(services, dataPath);
        BlindRoleServices.AddLargeBodyLimits(services);
        BlindRoleServices.AddBlindPackageServices(services);

        BlindRoleServices.AddBlindRoleServices(services, dataPath);
        BlindRoleServices.UseBlindHttps(builder);

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            // Enums as strings, as every endpoint of the family answers.
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
    }

    /// <summary>
    /// The blind side of recovery (plan 5.5 and 6): the recovery host, the standing of its peers, the anchor and box
    /// sections of /api/blind/status, the recovery set its backups write, and the restore codes it hands out. The
    /// OTHER side of a restore - the new device that claims a package (RecoveryRestoreService, BlindRestoreClient,
    /// the Android backup file source, RestoreProgress) is a full node being restored and is not part of this host.
    /// </summary>
    private static void AddBlindRecovery(IServiceCollection services)
    {
        // The peers' my-standing, over the default "not known to be a superadmin" of AddRecovery.
        services.RemoveAll<IOwnStandingProvider>();
        services.AddSingleton<IOwnStandingProvider, PeerOwnStanding>();
        services.AddScoped<RecoveryBoxQueries>();
        // No current-key source: a blind node holds no DEK, so "the current key" is the one the newest anchor vouches for.
        services.AddScoped<RecoveryStatusService>();
        // Not registered here since the vault split (BMB-99): the strong-box service, the recovery cleanup / triggers / reconcile
        // watcher and the state-anchor scheduler. Each of them returned at once behind "the session is unlocked", which a blind
        // node never is; they create and open recovery boxes with the master key and are full-node code.

        // The recovery sections of /api/blind/status, and the recovery set the blind node's backups write.
        services.AddSingleton<RecoveryAnchorStatusCache>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryAnchorStatusContributor>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryBoxesStatusContributor>();
        services.AddSingleton<IRecoverySetSource, RecoverySetSource>();

        // Restore codes: the blind side of a restore.
        services.AddScoped<BlindRestoreCodeService>();
    }
}
