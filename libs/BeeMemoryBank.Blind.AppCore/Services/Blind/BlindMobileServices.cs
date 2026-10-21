using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Composes the minimal service collection for the Android blind node.
/// Invariant: the vault SERVICES (SessionService, ArticleService, KeyManagementService, TreeService,
/// SearchService, MediaService, RestoreService) and the key-slot, retired-DEK and search-index
/// repositories are NOT registered.
/// Exception, and the only one: the receive-only block below registers the replicated-row repositories
/// (article, article body, blob, tombstone, conflict version, comment, folder, media, concept tag, ...)
/// because <c>EventApplier</c> cannot be constructed without them. Each one is listed, with why and who
/// consumes it, in <c>ReceiveOnlyTypes</c> (tests/BeeMemoryBank.BlindMobile.Tests), and the boundary
/// tests fail on any other type of the app that mentions one. It goes away with BMB-91 (a receive-only
/// applier in its own project).
/// </summary>
public static partial class BlindMobileServices
{
    public static IServiceCollection ConfigureServices(IServiceCollection services, string dataDir, string dbPath)
        => AddBlindAppCore(services, new BlindAppOptions(dataDir, dbPath));

    public static IServiceCollection AddBlindAppCore(IServiceCollection services, BlindAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dataDir = options.DataDirectory;
        var dbPath = options.DatabasePath;
        DapperConfig.Configure();
        services.TryAddSingleton(options.TimeProvider ?? TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IBlindPaths>(new BlindAppPaths(dataDir, dbPath));
        // One long job at a time on the device: the host's gate when it runs several accounts, otherwise this composition's own.
        services.TryAddSingleton(options.DeviceGate ?? new BlindDeviceGate());
        // What the copy is doing now (sync, first load, backup): the wipe stops it and waits for it (BlindPhoneReset).
        services.TryAddSingleton<BlindActivity>();

        // Limits and metering (every request of the copy is counted, and a limit or a switch can pause it). A composition with no registry has no policy: nothing is ever paused,
        // but what the copy uses is still counted (its usage file is in its own folder). The host's account registry supplies the policy, the device-wide usage and the
        // device's conditions before this runs; TryAdd keeps theirs.
        services.TryAddSingleton<IBlindAccountPolicySource>(NoLimitsPolicySource.Instance);
        services.TryAddSingleton<IBlindDeviceConditions>(UnknownBlindDeviceConditions.Instance);
        services.TryAddSingleton(sp => new BlindDeviceUsage(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new BlindUsageLedger(BlindPaths.Usage(dataDir), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new BlindStoredBytes(() => StoredRoots(dataDir, dbPath), sp.GetRequiredService<TimeProvider>(), dataDir, null));
        services.AddSingleton(sp => new BlindLimitGuard(
            sp.GetRequiredService<IBlindAccountPolicySource>(), sp.GetRequiredService<BlindUsageLedger>(), sp.GetRequiredService<BlindStoredBytes>(),
            sp.GetRequiredService<BlindDeviceUsage>(), sp.GetRequiredService<IBlindDeviceConditions>()));
        services.AddSingleton(sp => new BlindMeteringTap(sp.GetRequiredService<BlindUsageLedger>(), sp.GetRequiredService<BlindLimitGuard>()));

        // Minimal SQLite infrastructure and blind-specific repositories:
        services.AddSingleton(_ => new DbConnectionFactory(dbPath));
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<DbConnectionFactory>());
        services.AddSingleton<MigrationRunner>();
        // The one place that opens the database: migrations, then the journal pragmas. The app's start-up, the
        // page and the background workers all wait on it (BlindStartupGateTests).
        services.AddSingleton(sp => new BlindStartup(async ct =>
        {
            await sp.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
            using var conn = sp.GetRequiredService<DbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
        }));

        services.AddSingleton<INodeIdentityRepository, NodeIdentityRepository>();
        services.AddSingleton<IWhitelistRepository, WhitelistRepository>();
        services.AddScoped<IEventLogRepository, EventLogRepository>();

        // Narrow receive-only sync composition. This deliberately does not call AddStorage() or
        // AddSync(): those wire vault/session, search-index, scheduler and cleanup subsystems that
        // cannot exist on a blind phone. These are only EventApplier's replicated-row dependencies.
        services.AddScoped<ICallerScopeStore, InstanceCallerScopeStore>();
        services.AddScoped<CallerScopeHolder>();
        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
        services.AddScoped<IBlobRepository, BlobRepository>();
        services.AddScoped<ISyncPositionRepository, SyncPositionRepository>();
        services.AddScoped<ITombstoneRepository, TombstoneRepository>();
        services.AddScoped<IConflictVersionRepository, ConflictVersionRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IConceptTagRepository, ConceptTagRepository>();
        services.AddSingleton<IRestoreReplayShieldRepository, RestoreReplayShieldRepository>();
        services.AddScoped<IRestoreEventStateRepository, RestoreEventStateRepository>();
        services.AddScoped<IDekRotationStateRepository, DekRotationStateRepository>();
        services.AddScoped<ISyncQuarantineRepository, SyncQuarantineRepository>();
        services.AddSingleton(new MediaStorageOptions(Path.Combine(dataDir, "media")));
        services.AddSingleton<LamportClock>();
        services.AddSingleton<ILamportClock>(sp => sp.GetRequiredService<LamportClock>());
        services.AddScoped<IEventLogger, NullEventLogger>();
        services.AddScoped<HardDeleteService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<FolderAccessService>();
        services.AddSingleton<BlindState>();
        services.AddSingleton<BlindRestoreInitiator>();
        services.AddSingleton<IRestoreInitiator>(sp => sp.GetRequiredService<BlindRestoreInitiator>());
        services.AddScoped<IDekRotationApplier, BlindDekRotationApplier>();
        services.AddSingleton<IEmbeddingGenerator, BlindEmbeddingGenerator>();
        // Step A (docs/design/step-a-spec.md 9.4, 15.2): the protocol this copy declares, the relay opener (a blind copy
        // stores and relays sealed events and never opens one), no held events, the blind store (hook H1), then the
        // phone's own metadata services (hook H5, track 3).
        services.AddBlindMetadataHost(dataDir);
        AddPhoneMetadataServices(services, dataDir);
        services.AddScoped<EventApplier>();

        services.AddLogging();

        // Pinned HTTP client and maintenance detection
        services.AddTransient<MaintenanceDetectingHandler>();
        services.AddTransient<BlindHttpHandler>();
        services.AddSingleton(sp => new BlindHttpClientProvider(
            sp.GetRequiredService<BlindPhoneState>(),
            host => BlindRunReport.Record(sp.GetRequiredService<BlindPhoneLog>(), "pin",
                $"Refused {host}: it answered with another key than the one pinned when this phone was paired. Nothing was sent."),
            host => BlindRunReport.Record(sp.GetRequiredService<BlindPhoneLog>(), "certificate",
                $"Refused {host}: its certificate is not valid (expired, issued for another name, self-signed or from an authority this device does not trust). Nothing was sent."),
            primaryHandlerOverride: options.PrimaryHandlerFactory) { Metering = sp.GetRequiredService<BlindMeteringTap>() });
        services.AddSingleton<IHttpClientFactory>(sp => sp.GetRequiredService<BlindHttpClientProvider>());
        services.AddTransient<HttpClient>(sp => sp.GetRequiredService<BlindHttpClientProvider>().GetClient());

        // Blind node state, keys, pairing, and pending sources
        services
            // AppCore asks hosts for its named seams. The older PhoneClient contracts below are
            // one-way compatibility forwards, never fallback registrations for those seams.
            .AddSingleton<IBlindPhoneStore>(sp => sp.GetRequiredService<IBlindStateStore>())
            .AddSingleton<IBlindPhoneKeys>(sp => sp.GetRequiredService<IBlindSecretStore>())
            .AddSingleton<BlindPhoneState>()
            .AddSingleton(sp => new BlindPhoneLog(BlindPaths.Log(sp.GetRequiredService<IBlindPaths>().DataDirectory), sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<SqliteBlindIdentityRecorder>()
            .AddSingleton<IBlindIdentityRecorder>(sp => sp.GetRequiredService<SqliteBlindIdentityRecorder>())
            .AddSingleton<INodeAuthSigner, BlindKeystoreNodeAuthSigner>()
            .AddSingleton(sp => new BlindPhoneReplicaClient(
                sp.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<INodeIdentityRepository>(),
                sp.GetRequiredService<INodeAuthSigner>(), sp.GetRequiredService<IBlindPaths>().DataDirectory, sp.GetRequiredService<IBlindPaths>().DatabasePath,
                sp.GetRequiredService<ILogger<BlindPhoneReplicaClient>>(), scopes: sp.GetRequiredService<IServiceScopeFactory>())
            {
                // A first load or a baseline update: the archive, its extraction and the installed copy are on disk together at the peak (3 times the archive).
                BeforeArchiveDownload = length => sp.GetRequiredService<BlindLimitGuard>().AdmitPackage(length, BlindPackageFootprint.Install),
                AfterBatch = () => sp.GetRequiredService<BlindStoredBytes>().Invalidate(),
            })
            .AddSingleton<IBlindReplicaSource, BlindReplicaSource>()
            .AddSingleton(sp => new BlindPhonePullClient(
                sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<INodeAuthSigner>(), sp.GetRequiredService<ILogger<BlindPhonePullClient>>())
            {
                // Every persisted batch (a blob, a hundred events, the end of a page) tells the stored-bytes meter: the next admission walks the folders again.
                AfterBatch = () => sp.GetRequiredService<BlindStoredBytes>().Invalidate(),
            })
            .AddSingleton<IBlindPhonePullClient>(sp => sp.GetRequiredService<BlindPhonePullClient>())
            .AddSingleton<BlindPauseReporter>()
            .AddSingleton<IBlindPhoneSync>(sp => new BlindPhoneSync(
                sp.GetRequiredService<IBlindPhonePullClient>(), sp.GetRequiredService<BlindHttpClientProvider>(), sp.GetRequiredService<BlindPhoneState>())
            {
                Guard = sp.GetRequiredService<BlindLimitGuard>(),
                Reporter = sp.GetRequiredService<BlindPauseReporter>(),
                Usage = sp.GetRequiredService<BlindUsageLedger>(),
                Stored = sp.GetRequiredService<BlindStoredBytes>(),
            })
            // A backup's body: the listening node's current signed package (fetched pinned and verified, not
            // installed), its signature and the phone's own logged events. Its header: the recovery set of
            // the phone's database. Both are the libraries' code; nothing of the format is rebuilt here.
            .AddSingleton<IBlindVerifiedPackageFetcher>(sp => new BlindPackageFetcher(
                sp.GetRequiredService<BlindPhoneReplicaClient>(),
                sp.GetRequiredService<BlindHttpClientProvider>(),
                sp.GetRequiredService<BlindPhoneState>(),
                BlindPaths.Replica(sp.GetRequiredService<IBlindPaths>().DataDirectory))
            {
                // A backup: the archive, the extraction that checks it, the body and the encrypted file (4 times the archive: the same factor the package source asks the volume for).
                BeforeArchive = length => sp.GetRequiredService<BlindLimitGuard>().AdmitPackage(length, BlindPackageFootprint.Backup),
            })
            .AddSingleton<IBlindPackageSource>(sp => new BlindPhonePackageSource(
                sp.GetRequiredService<IBlindVerifiedPackageFetcher>(),
                sp.GetRequiredService<IServiceScopeFactory>()))
            .AddSingleton<IRecoverySetJsonSource>(sp => new BlindPhoneRecoverySetSource(
                sp.GetRequiredService<IDbConnectionFactory>()))
            .AddSingleton<BlindMobilePairing>()
            .AddSingleton(sp => new BlindPhoneBackupRunner(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindPhoneKeys>(),
                sp.GetRequiredService<IBlindPackageSource>(),
                sp.GetRequiredService<IRecoverySetJsonSource>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Backups(sp.GetRequiredService<IBlindPaths>().DataDirectory), sp.GetRequiredService<TimeProvider>())
            {
                AfterStep = () => sp.GetRequiredService<BlindStoredBytes>().Invalidate(),
            })
            .AddSingleton(sp => new BlindHeavyWork(
                sp.GetRequiredService<BlindPhoneState>(),
                sp.GetRequiredService<IBlindReplicaSource>(),
                sp.GetRequiredService<BlindPhoneBackupRunner>(),
                sp.GetRequiredService<BlindPhoneLog>(),
                BlindPaths.Replica(sp.GetRequiredService<IBlindPaths>().DataDirectory), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<BlindActivity>())
            {
                DeviceGate = sp.GetRequiredService<BlindDeviceGate>(),
                GateOwnerId = options.DeviceOwnerId,
                GateOwnerName = options.DeviceOwnerName,
                Guard = sp.GetRequiredService<BlindLimitGuard>(),
                Reporter = sp.GetRequiredService<BlindPauseReporter>(),
                Usage = sp.GetRequiredService<BlindUsageLedger>(),
                Stored = sp.GetRequiredService<BlindStoredBytes>(),
                RemovalSink = sp.GetService<IBlindSourceRemovalSink>(),
            });

        services.AddSingleton<BlindAppController>();
        services.AddSingleton<IBlindAppController>(sp => sp.GetRequiredService<BlindAppController>());
        return services;
    }

    /// <summary>
    /// What the copy owns on disk, for its stored bytes: the files and folders a wipe would delete (backups, replica work folder with a partial download, media, log, baselines, usage file,
    /// the database with its sidecars and the replica candidates), and nothing else. The data folder of the account that lives in the app's root also holds the other accounts
    /// (<c>accounts/</c>) and things that are not ours.
    /// </summary>
    internal static IEnumerable<string> StoredRoots(string dataDir, string dbPath)
    {
        foreach (var path in BlindPhoneReset.OwnedPaths(dataDir)) yield return path;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) yield return dbPath + suffix;
    }

    /// <summary>Hook H5 (spec 15.2), track 3: <c>BlindMobileServices.Metadata.cs</c> (activation observer, store stats).</summary>
    static partial void AddPhoneMetadataServices(IServiceCollection services, string dataDir);
}
