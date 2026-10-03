using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Sync;
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

        // The library half: storage, the services of the replicated data model, the sync machinery - only what a
        // blind node uses (libs/BeeMemoryBank.Blind/BlindComposition.cs).
        services.AddBlindComposition(dataPath);
        services.AddMemoryCache();
        AddBlindRecovery(services);
        services.AddSingleton<SyncTokenStore>();
        // Per-node, not per-process: see SyncChallengeRateLimiter.
        services.AddSingleton<SyncEndpoints.SyncChallengeRateLimiter>();
        services.AddSingleton<IPublicHostValidator, DnsPublicHostValidator>();

        // BMB_SYNC_INTERVAL_SECONDS: override of the scheduler tick (default 60 s); tests set it low.
        TimeSpan? syncInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_SYNC_INTERVAL_SECONDS"), out var s) && s >= 1
            ? TimeSpan.FromSeconds(s) : null;
        services.AddBlindSyncScheduler(interval: syncInterval, periodicCleanupFactory: sp =>
            sp.GetRequiredService<SyncTokenStore>().CleanupExpired);
        services.AddBlindCleanupService();

        services.AddHttpClient();
        services.AddTransient<HttpClient>(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        // Outbound calls to a caller-supplied URL must not follow redirects (probe-relay): a host that passes the
        // check can otherwise 302 the request onto loopback.
        services.AddHttpClient(SyncEndpoints.NoRedirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpContextAccessor();

        services.AddSingleton(sp =>
            new SnapshotService(dataPath, sp.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>(),
                sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<ILamportClock>(),
                sp.GetRequiredService<ILogger<SnapshotService>>(),
                sp.GetRequiredService<IRestoreReplayShieldRepository>(),
                sp.GetRequiredService<IWhitelistRepository>(),
                sp.GetRequiredService<SessionService>(),
                // The key a blind node signs its packages with.
                sp.GetService<IExternalNodeKey>()));
        services.AddHostedService<AuditLogPruningHostedService>();

        // Blind node (BMB-54): /api/blind/status base fields, backups, jobs/CPU modes, console login, wipe.
        services.AddBlindNodeServices(dataPath);
        services.AddSingleton<SnapshotJoinCache>();
        var mediaDir = Path.Combine(dataPath, "media");
        Directory.CreateDirectory(mediaDir);
        services.AddSingleton(new MediaStorageOptions(mediaDir));

        // Seed packages and replicas are large; the body limits are the full node's.
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = 500L * 1024 * 1024;
        });
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
        {
            o.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
        });

        // The blind package (CONTRACTS section 2): a blind node builds it for an Android blind node.
        services.AddScoped<BlindPackageBuilder>();
        services.AddSingleton<BlindReplicaPackageCache>();
        services.AddHostedService(sp => sp.GetRequiredService<BlindReplicaPackageCache>());
        services.TryAddSingleton(TimeProvider.System);

        AddBlindRoleServices(services, dataPath);
        UseBlindHttps(builder);

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
        services.AddSingleton<IRecoveryHost, CurrentRecoveryHost>();
        // The peers' my-standing, over the default "not known to be a superadmin" of AddRecovery.
        services.RemoveAll<IOwnStandingProvider>();
        services.AddSingleton<IOwnStandingProvider, PeerOwnStanding>();
        services.AddScoped<RecoveryBoxQueries>();
        services.AddScoped<RecoveryStatusService>();
        services.AddSingleton<StrongBoxService>();
        services.AddSingleton<RecoveryCleanupService>();
        services.AddSingleton<RecoveryTriggers>();
        services.AddHostedService<RecoveryReconcileWatcher>();
        services.AddSingleton<StateAnchorScheduler>();

        // The recovery sections of /api/blind/status, and the recovery set the blind node's backups write.
        services.AddSingleton<RecoveryAnchorStatusCache>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryAnchorStatusContributor>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryBoxesStatusContributor>();
        services.AddSingleton<IRecoverySetSource, RecoverySetSource>();
        services.AddHostedService(sp => sp.GetRequiredService<StateAnchorScheduler>());

        // Restore codes: the blind side of a restore.
        services.AddScoped<BlindRestoreCodeService>();
    }

    /// <summary>
    /// What a blind node has instead of the DEK-bound services (plan 3.4, 3.5): its identity key in a file, a
    /// rotation applier with nothing to re-wrap, and a restore initiator that only asks for a reseed.
    /// </summary>
    private static void AddBlindRoleServices(IServiceCollection services, string dataPath)
    {
        services.AddSingleton(new FileNodeKey(Path.Combine(dataPath, FileNodeKey.FileName)));
        services.AddSingleton<IExternalNodeKey>(sp => sp.GetRequiredService<FileNodeKey>());
        services.AddSingleton<BeeMemoryBank.Sync.Blind.BlindState>();
        services.AddSingleton<BeeMemoryBank.Sync.Blind.BlindRestoreInitiator>();
        services.AddSingleton<IRestoreInitiator>(sp => sp.GetRequiredService<BeeMemoryBank.Sync.Blind.BlindRestoreInitiator>());
        services.AddSingleton<IRestoreRetrier>(sp => sp.GetRequiredService<BeeMemoryBank.Sync.Blind.BlindRestoreInitiator>());
        services.AddScoped<IDekRotationApplier, BeeMemoryBank.Sync.Blind.BlindDekRotationApplier>();

        // Pairing and seed (plan 4.1-4.4): the self-signed certificate in the data volume, the pair code, and the
        // receiver of the package that makes this node's database.
        services.AddSingleton(new BlindTlsIdentity(BlindTlsCertificate.LoadOrCreate(dataPath)));
        services.AddSingleton<BlindPairing>();
        services.AddSingleton<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, PairingBlindStatusContributor>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<BlindSeedService>(sp, dataPath));

        // Log trimming without an event (plan 5.4) - a blind node has no compaction of its own.
        services.AddSingleton<BlindLogTrimmer>();
        services.AddHostedService(sp => sp.GetRequiredService<BlindLogTrimmer>());
    }

    /// <summary>
    /// A blind node is dialled by every full device (plan 4.4), over HTTPS with its self-signed certificate.
    /// BMB_BLIND_HTTPS_PORT opens that listener on all interfaces; BMB_BLIND_LOCAL_PORT keeps a loopback HTTP
    /// port for the console next to it (internal key). Without the variable the process listens wherever
    /// ASPNETCORE_URLS says, as every node does.
    /// </summary>
    private static void UseBlindHttps(WebApplicationBuilder builder)
    {
        if (!int.TryParse(builder.Configuration["BMB_BLIND_HTTPS_PORT"], out var httpsPort) || httpsPort <= 0)
            return;
        var localPort = int.TryParse(builder.Configuration["BMB_BLIND_LOCAL_PORT"], out var p) && p > 0 ? p : 5612;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var certificate = kestrel.ApplicationServices.GetRequiredService<BlindTlsIdentity>().Certificate;
            kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
            kestrel.ListenLocalhost(localPort);
        });
    }
}
