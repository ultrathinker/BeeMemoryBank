using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// The registrations that BeeMemoryBank.Api (BMB_ROLE=blind) and the blind node's own host (BeeMemoryBank.BlindNode) make identically: one
/// definition, so the two hosts cannot drift. The two hosts stay separate by design (a blind node must never have the Vault in its assembly
/// closure): this file is part of the Api project and is LINKED into the blind host like <see cref="BlindRoleStartup"/> and the blind services
/// it registers (tools/blind-link, linked-api-files.props), so it names no type of the Vault.
///
/// <para>What differs stays in each host: the snapshot's key operations (the Api's are session-backed, the blind host's are the external key),
/// the full node's other registrations, and the order around these calls.</para>
/// </summary>
public static class BlindRoleServices
{
    /// <summary>
    /// What a blind node has instead of the DEK-bound services (plan 3.4, 3.5): its identity key in a file, a rotation applier with nothing
    /// to re-wrap, and a restore initiator that only asks for a reseed.
    /// </summary>
    public static void AddBlindRoleServices(IServiceCollection services, string dataPath)
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
    public static void UseBlindHttps(WebApplicationBuilder builder)
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

    /// <summary>
    /// The package engine of a node. Every argument but the key operations is the same in both hosts; the Api passes the session-backed
    /// operations (and says why it must not resolve them softly), the blind host the external key's.
    /// </summary>
    public static void AddSnapshotService(IServiceCollection services, string dataPath,
        Func<IServiceProvider, ISnapshotKeyOperations> keyOperations)
    {
        services.AddSingleton(sp =>
            new SnapshotService(dataPath, sp.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<ILamportClock>(),
                sp.GetRequiredService<ILogger<SnapshotService>>(),
                sp.GetRequiredService<IRestoreReplayShieldRepository>(),
                sp.GetRequiredService<IWhitelistRepository>(),
                keyOperations(sp)));
    }

    /// <summary>The media folder next to the database (created now) and its options.</summary>
    public static void AddMediaStorage(IServiceCollection services, string dataPath)
    {
        var mediaDir = Path.Combine(dataPath, "media");
        Directory.CreateDirectory(mediaDir);
        services.AddSingleton(new MediaStorageOptions(mediaDir));
    }

    /// <summary>Seed packages and replicas are large; the body limits are the full node's (500 MB).</summary>
    public static void AddLargeBodyLimits(IServiceCollection services)
    {
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = 500L * 1024 * 1024;
        });
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
        {
            o.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
        });
    }

    /// <summary>The blind package (CONTRACTS section 2): a full node builds it to seed, reseed and hand out replicas; a blind node builds it for an Android blind node.</summary>
    public static void AddBlindPackageServices(IServiceCollection services)
    {
        services.AddScoped<BlindPackageBuilder>();
        services.AddSingleton<BlindReplicaPackageCache>();
        services.AddHostedService(sp => sp.GetRequiredService<BlindReplicaPackageCache>());
        services.TryAddSingleton(TimeProvider.System);
    }
}
