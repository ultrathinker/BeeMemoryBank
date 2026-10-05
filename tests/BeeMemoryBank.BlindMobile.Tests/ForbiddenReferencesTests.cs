using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

public class ForbiddenReferencesTests
{
    private static readonly string[] ForbiddenNames =
    [
        "BeeMemoryBank.Embeddings",
        "BeeMemoryBank.Media",
        // macOS only (Keychain, LaunchAgent): not in the Android app's graph, csproj or output
        "BeeMemoryBank.Platforms.Apple",
        "BeeMemoryBank.BlindDesktop",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.ML.Tokenizers",
        "Markdig",
        "Indiko.Maui.Controls.Markdown",
        "SixLabors.ImageSharp"
    ];

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(current, "BeeMemoryBank.sln")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void BlindMobile_ProjectAssetsJson_ContainsNoForbiddenReferences()
    {
        var repoRoot = FindRepoRoot();
        var assetsJsonPath = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "obj", "project.assets.json");

        File.Exists(assetsJsonPath).Should().BeTrue($"project.assets.json must exist at {assetsJsonPath} (run dotnet restore first)");

        var jsonContent = File.ReadAllText(assetsJsonPath);
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        var violations = new List<string>();

        // Check targets (resolved packages and project references per framework)
        if (root.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var lib in target.Value.EnumerateObject())
                {
                    var libName = lib.Name;
                    foreach (var forbidden in ForbiddenNames)
                    {
                        if (libName.StartsWith(forbidden + "/", StringComparison.OrdinalIgnoreCase) ||
                            libName.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add($"Target '{target.Name}' contains forbidden reference '{libName}'");
                        }
                    }
                }
            }
        }

        // Check libraries (all resolved package/project libraries in the graph)
        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateObject())
            {
                var libName = lib.Name;
                foreach (var forbidden in ForbiddenNames)
                {
                    if (libName.StartsWith(forbidden + "/", StringComparison.OrdinalIgnoreCase) ||
                        libName.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"Libraries contains forbidden reference '{libName}'");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "BeeMemoryBank.BlindMobile resolved graph must not contain any forbidden references: " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// The structural claim for the phone (BMB-91, kept by the vault split BMB-99): of the application's own libraries the blind app
    /// carries the shared libraries (Core, Crypto, Search, Storage, Sync) and the phone's client - and not BeeMemoryBank.Vault, which
    /// holds the master-key, session and content-crypto code of a full node, nor the full node's other libraries.
    /// </summary>
    [Fact]
    public void BlindMobile_Output_HoldsOnlyTheSharedAssembliesOfTheApplicationLibraries()
    {
        var dir = Path.GetDirectoryName(FindAppDll())!;
        var own = Directory.GetFiles(dir, "BeeMemoryBank.*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
        own.Should().BeEquivalentTo(new[]
            {
                "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
                "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.Blind.AppCore", "BeeMemoryBank.BlindMobile"
            },
            "the app may carry its own assembly, the shared libraries and the phone's client, and no other BeeMemoryBank library (Vault least of all)");
    }

    [Fact]
    public void BlindMobile_Csproj_ReferencesOnlyTheSharedLibrariesAndThePhoneClient()
    {
        var csproj = File.ReadAllText(Path.Combine(FindRepoRoot(), "mobile", "BeeMemoryBank.BlindMobile", "BeeMemoryBank.BlindMobile.csproj"));
        var references = System.Text.RegularExpressions.Regex.Matches(csproj, "<ProjectReference Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'))).ToList();
        references.Should().BeEquivalentTo(new[]
        {
            "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
            "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.Blind.AppCore"
        });
    }

    [Fact]
    public void BlindMobile_Csproj_ContainsNoForbiddenProjectOrPackageReferences()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "BeeMemoryBank.BlindMobile.csproj");

        File.Exists(csprojPath).Should().BeTrue($"csproj must exist at {csprojPath}");

        var content = File.ReadAllText(csprojPath);

        foreach (var forbidden in ForbiddenNames)
        {
            content.Should().NotContain(forbidden,
                $"BeeMemoryBank.BlindMobile.csproj must not directly reference {forbidden}");
        }
    }

    private static string FindAppDll()
    {
        var repoRoot = FindRepoRoot();
        var debugDll = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "bin", "Debug", "net10.0-android", "BeeMemoryBank.BlindMobile.dll");
        var releaseDll = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "bin", "Release", "net10.0-android", "BeeMemoryBank.BlindMobile.dll");

        var dllPath = File.Exists(debugDll) ? debugDll : releaseDll;
        File.Exists(dllPath).Should().BeTrue($"BeeMemoryBank.BlindMobile.dll must exist at {dllPath}. Build the mobile project first.");
        return dllPath;
    }

    // BlindMobileServices is the one composition class: it registers the receive-only types and so
    // has to name them. Every other type of the app is held to ReceiveOnlyTypes.RestrictedNames.
    private static readonly string CompositionClass = typeof(BlindMobileServices).FullName!;

    /// <summary>
    /// The sources of Services/ are linked into this test assembly, so this scan always sees the
    /// current text of those types (the built Android assembly below can be older than the sources).
    /// </summary>
    [Fact]
    public void BlindMobileServices_OutsideTheCompositionClass_NeverMentionReceiveOnlyTypes()
    {
        var findings = BoundaryScanner.Scan(
            typeof(BlindMobileServices).Assembly.Location,
            owner => owner.StartsWith("BeeMemoryBank.BlindMobile.", StringComparison.Ordinal)
                     && !owner.StartsWith("BeeMemoryBank.BlindMobile.Tests.", StringComparison.Ordinal)
                     && owner != CompositionClass,
            ReceiveOnlyTypes.RestrictedNames);

        findings.Should().BeEmpty(
            "only BlindMobileServices may name the receive-only repositories and services (BMB-91):\n" +
            string.Join("\n", findings));
    }

    /// <summary>
    /// The built app assembly: pages, Android platform classes, MauiProgram, App and Services. Read as
    /// metadata, so Maui/Android types need no loading.
    /// </summary>
    [Fact]
    public void BlindMobileAssembly_OutsideTheCompositionClass_NeverMentionsReceiveOnlyTypes()
    {
        var appFindings = BoundaryScanner.Scan(FindAppDll(), owner => owner != CompositionClass, ReceiveOnlyTypes.RestrictedNames);
        var coreFindings = BoundaryScanner.Scan(typeof(BlindMobileServices).Assembly.Location, owner => owner != CompositionClass, ReceiveOnlyTypes.RestrictedNames);
        var findings = appFindings.Concat(coreFindings).ToList();

        findings.Should().BeEmpty(
            "pages, platform classes and services must not take, hold, resolve or construct a repository " +
            "or vault service; only BlindMobileServices composes them for EventApplier (BMB-91):\n" +
            string.Join("\n", findings));
    }

    [Fact]
    public void ReceiveOnlyTable_RowsAreRealConstructorParametersOfTheirConsumers()
    {
        foreach (var row in ReceiveOnlyTypes.Rows.Where(r => !r.ViaLocator))
        {
            var parameterTypes = row.Consumer.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
            parameterTypes.Should().Contain(row.Service,
                $"{row.Service.Name} is justified by {row.Consumer.Name}'s constructor ({row.Why}); a type nobody takes does not belong in the table");
        }
    }

    /// <summary>
    /// Requirement 1(a) (CRITICAL finding): The blind mobile app's DI composition must NOT register
    /// or resolve any vault service (SessionService, ArticleService, KeyManagementService, TreeService,
    /// SearchService, MediaService, RestoreService) or any vault repository (key-slot, retired-DEK,
    /// article, media, search index).
    /// </summary>
    [Fact]
    public void BlindMobileServices_DoesNotRegisterOrResolveVaultServices()
    {
        var services = new ServiceCollection();
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-di-test-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(tempDir, "test.db");

        // Use the exact composition method that MauiProgram uses:
        BlindMobileServices.ConfigureServices(services, tempDir, dbPath);

        // Platform dependencies needed for blind background services
        var dummyKeys = new DummyKeys();
        services.AddSingleton<IBlindStateStore>(new PreferencesBlindStore(_ => null, (_, _) => { }, _ => { }));
        services.AddSingleton<IBlindNodeKeys>(dummyKeys);
        services.AddSingleton<IBlindSecretStore>(dummyKeys);
        services.AddSingleton<IBlindLifecycle, DummyLifecycle>();

        // Full names, not typeof(): most of these types are no longer IN the blind assemblies at all (the libraries are
        // linked into BeeMemoryBank.Blind only as far as a blind node uses them), and a type that does not exist can
        // neither be registered nor resolved. Those that do exist (the session, for instance, is a dependency of the
        // sync code) must still never be registered or resolve.
        var forbiddenNames = new[]
        {
            "BeeMemoryBank.Core.Services.SessionService",
            "BeeMemoryBank.Core.Services.ArticleService",
            "BeeMemoryBank.Core.Services.ArticleDiffService",
            "BeeMemoryBank.Core.Services.KeyManagementService",
            "BeeMemoryBank.Core.Services.TreeService",
            "BeeMemoryBank.Core.Services.SearchService",
            "BeeMemoryBank.Core.Services.FolderService",
            "BeeMemoryBank.Core.Services.CopyService",
            "BeeMemoryBank.Core.Services.CommentService",
            "BeeMemoryBank.Core.Services.MediaService",
            "BeeMemoryBank.Core.Services.MediaBlobBackfillService",
            "BeeMemoryBank.Core.Services.RestoreService",
            "BeeMemoryBank.Core.Services.LegacyPasswordSlotMigrationService",
            // Vault Repositories & Caches
            "BeeMemoryBank.Core.Interfaces.IKeySlotRepository",
            "BeeMemoryBank.Storage.Sqlite.KeySlotRepository",
            "BeeMemoryBank.Core.Interfaces.IRetiredMasterDekStore",
            "BeeMemoryBank.Storage.Sqlite.RetiredMasterDekStore",
            "BeeMemoryBank.Core.Interfaces.IArticleChunkEmbeddingRepository",
            "BeeMemoryBank.Storage.Sqlite.ArticleChunkEmbeddingRepository",
            "BeeMemoryBank.Storage.Search.SegmentManifestRepository",
            "BeeMemoryBank.Storage.Search.SegmentTombstoneRepository",
            "BeeMemoryBank.Storage.Search.EncryptedSegmentStore",
            "BeeMemoryBank.Storage.Sqlite.EmbeddingVectorCache",
            "BeeMemoryBank.Storage.Sqlite.ChunkEmbeddingVectorCache",
            "BeeMemoryBank.Core.Services.SearchQueryCache",
            "BeeMemoryBank.Core.Services.SearchMetrics",
            "BeeMemoryBank.Core.Interfaces.IProjectionMatrixRepository",
            "BeeMemoryBank.Storage.Sqlite.ProjectionMatrixRepository",
            "BeeMemoryBank.Core.Interfaces.ISyncPushPositionRepository",
            "BeeMemoryBank.Storage.Sqlite.SyncPushPositionRepository"
        };
        // Every assembly the product has that this project can see: the shared libraries, the phone client and the vault (the tests reference it to play the PC).
        var blindAssemblies = new[]
        {
            typeof(DbConnectionFactory).Assembly, typeof(BeeMemoryBank.Sync.Blind.BlindPhoneReplicaClient).Assembly,
            typeof(BeeMemoryBank.Core.Models.NodeIdentity).Assembly, typeof(BeeMemoryBank.Crypto.Ed25519Signer).Assembly,
            typeof(BeeMemoryBank.Sync.EventApplier).Assembly, typeof(BeeMemoryBank.Search.DefaultTokenizer).Assembly,
            typeof(BeeMemoryBank.Core.Services.SessionService).Assembly
        };
        Type? Find(string fullName) => blindAssemblies.Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(t => t is not null);
        var forbiddenTypes = forbiddenNames.Select(Find).Where(t => t is not null).Select(t => t!).ToArray();

        // 1. None of the forbidden types should appear as ServiceType or ImplementationType
        foreach (var desc in services)
        {
            forbiddenNames.Should().NotContain(desc.ServiceType.FullName!,
                $"ServiceType '{desc.ServiceType.FullName}' must not be registered in the blind app DI container");
            if (desc.ImplementationType is not null)
            {
                forbiddenNames.Should().NotContain(desc.ImplementationType.FullName!,
                    $"ImplementationType '{desc.ImplementationType.FullName}' must not be registered in the blind app DI container");
            }
        }

        // 1b. A receive-only type may be registered only as its table row says (ReceiveOnlyTypes.Rows):
        // anything else of that family is a new vault-shaped dependency nobody justified.
        var rows = ReceiveOnlyTypes.Rows.ToDictionary(r => r.Service);
        foreach (var desc in services.Where(d => ReceiveOnlyTypes.RestrictedNames.Contains(d.ServiceType.FullName!)
                                                 && d.ServiceType != typeof(BeeMemoryBank.Sync.EventApplier)))
        {
            rows.Should().ContainKey(desc.ServiceType,
                $"'{desc.ServiceType.FullName}' is receive-only: it needs a justified row in ReceiveOnlyTypes");
            desc.ImplementationType.Should().Be(rows[desc.ServiceType].Implementation);
        }
        foreach (var row in rows.Values)
            services.Should().Contain(d => d.ServiceType == row.Service,
                $"'{row.Service.FullName}' has a table row but is not registered; drop the stale row");

        // 2. Build provider and assert none resolve
        var provider = services.BuildServiceProvider();
        foreach (var forbiddenType in forbiddenTypes)
        {
            var resolved = provider.GetService(forbiddenType);
            resolved.Should().BeNull(
                $"Forbidden vault type '{forbiddenType.FullName}' must resolve to null in the blind app DI container");
        }

        // 3. Positive assertions: essential blind services and allowed repositories DO resolve
        provider.GetRequiredService<BlindMobilePairing>().Should().NotBeNull();
        provider.GetRequiredService<SqliteBlindIdentityRecorder>().Should().NotBeNull();
        provider.GetRequiredService<BlindPhoneState>().Should().NotBeNull();
        provider.GetRequiredService<DbConnectionFactory>().Should().NotBeNull();
        provider.GetRequiredService<MigrationRunner>().Should().NotBeNull();
        provider.GetRequiredService<INodeIdentityRepository>().Should().NotBeNull();
        provider.GetRequiredService<IWhitelistRepository>().Should().NotBeNull();
        provider.GetRequiredService<IEventLogRepository>().Should().NotBeNull();
        provider.GetRequiredService<IArticleRepository>().Should().BeOfType<ArticleRepository>();
        provider.GetRequiredService<IBlobRepository>().Should().BeOfType<BlobRepository>();
        provider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().Should().NotBeNull();
    }

    /// <summary>
    /// Requirement 1(b) (CRITICAL finding): The compiled BeeMemoryBank.BlindMobile assembly must NOT
    /// reference any Core, Storage, Crypto, or Search types outside an explicit allow-list.
    /// Uses System.Reflection.Metadata / PEReader to inspect type references directly from the PE file.
    /// </summary>
    [Fact]
    public void BlindMobileAssembly_ContainsNoForbiddenCoreStorageCryptoTypeReferences()
    {
        using var stream = File.OpenRead(FindAppDll());
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var violations = new List<string>();

        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var scope = typeRef.ResolutionScope;

            var typeNs = reader.GetString(typeRef.Namespace);
            var typeName = reader.GetString(typeRef.Name);

            // Handle nested type references
            while (scope.Kind == HandleKind.TypeReference)
            {
                var parent = reader.GetTypeReference((TypeReferenceHandle)scope);
                var parentNs = reader.GetString(parent.Namespace);
                if (!string.IsNullOrEmpty(parentNs))
                {
                    typeNs = parentNs;
                }
                typeName = $"{reader.GetString(parent.Name)}.{typeName}";
                scope = parent.ResolutionScope;
            }

            if (scope.Kind != HandleKind.AssemblyReference) continue;

            var asmRef = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
            var assemblyName = reader.GetString(asmRef.Name);

            if (!assemblyName.StartsWith("BeeMemoryBank.", StringComparison.OrdinalIgnoreCase))
                continue;

            // Disallow Search, Embeddings and Media entirely. Sync is limited to receive-only types.
            if (assemblyName.Equals("BeeMemoryBank.Search", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Equals("BeeMemoryBank.Embeddings", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Equals("BeeMemoryBank.Media", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Forbidden assembly reference '{assemblyName}': {typeNs}.{typeName}");
                continue;
            }

            if (!IsAllowedTypeReference(assemblyName, typeNs, typeName))
            {
                violations.Add($"Unallowed type reference from '{assemblyName}': {typeNs}.{typeName}");
            }
        }

        violations.Should().BeEmpty(
            "BeeMemoryBank.BlindMobile must only reference allowed Core/Storage/Crypto types:\n" +
            string.Join("\n", violations));
    }

    private static readonly string[] AllowedLibraryAssemblies =
    [
        "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
        "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.Blind.AppCore"
    ];

    /// <summary>
    /// The blind app reaches the libraries only through the shared ones (Core, Crypto, Search, Storage, Sync - shared with the Linux
    /// node) and BeeMemoryBank.Blind.PhoneClient (the phone's own client). The allow-list is keyed by NAMESPACE: the same rule whichever
    /// of those assemblies holds the type. A reference into any other BeeMemoryBank assembly (BeeMemoryBank.Vault above all) is refused.
    /// </summary>
    private static bool IsAllowedTypeReference(string assemblyName, string typeNs, string typeName)
    {
        if (!AllowedLibraryAssemblies.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
            return false;

        if (assemblyName.Equals("BeeMemoryBank.Blind.AppCore", StringComparison.OrdinalIgnoreCase))
            return typeNs.Equals("BeeMemoryBank.BlindMobile.Services.Blind", StringComparison.Ordinal) && typeName is
                "BlindMobileServices" or "BlindAppController" or "BlindStartup" or "BlindMobilePairing" or
                "BlindRunReport" or "BlindBackupExport" or "BlindPhoneReset" or "IBlindNodeKeys" or
                "IBlindSecretStore" or "IBlindStateStore" or "IBlindLifecycle" or
                "IBlindPaths" or "IBlindBackupExporter" or
                // the page shows the shared controller's status (BlindHomeView) and asks it, like the desktop hosts do
                "IBlindAppController" or "BlindAppStatus" or "BlindAppBackup" or "BlindAppOptions" or "BlindHomeView" or "BlindPaths"
                || typeNs.Equals("BeeMemoryBank.Core.Services.BlindPhone", StringComparison.Ordinal) && typeName is
                "BlindPhoneState" or "BlindPhoneLog" or "BlindPhoneLog.Entry" or "BlindHeavyWork" or
                "BlindBackupSchedule" or "BlindPhoneBackupRunner" or
                // what the sync worker, the backup service and the Keystore blobs register in / read through
                "BlindActivity" or "BlindOperation" or "BlindKeystoreBlobLayout" or "BlindSingleRun";

        if (typeNs.StartsWith("BeeMemoryBank.Core", StringComparison.Ordinal))
        {
            // All types in BlindPhone seam are allowed (including nested types like BlindPhoneLog.Entry)
            if (typeNs.Equals("BeeMemoryBank.Core.Services.BlindPhone", StringComparison.Ordinal) ||
                typeName.StartsWith("BlindPhoneLog.", StringComparison.Ordinal))
                return true;

            // Models allowed for blind node operation
            if (typeNs.Equals("BeeMemoryBank.Core.Models", StringComparison.Ordinal))
            {
                return typeName is "BlindCallCode" or "BlindNodeId" or "BlindPairingSecret" or "BlindPhoneCode" or "NodeIdentity" or "SyncPosition";
            }

            // Minimal repository and factory interfaces
            if (typeNs.Equals("BeeMemoryBank.Core.Interfaces", StringComparison.Ordinal))
            {
                return typeName is "IDbConnectionFactory" or "INodeIdentityRepository" or "IWhitelistRepository" or "IEventLogRepository" or "INodeAuthSigner" or "IArticleRepository" or "IArticleBodyRepository" or "IBlobRepository" or "ISyncPositionRepository" or "ITombstoneRepository" or "IConflictVersionRepository" or "ICommentRepository" or "IFolderRepository" or "IMediaRepository" or "IConceptTagRepository" or "IRestoreReplayShieldRepository" or "IRestoreEventStateRepository" or "IDekRotationStateRepository" or "ISyncQuarantineRepository" or "IEventLogger" or "ILamportClock" or "IRestoreInitiator" or "IDekRotationApplier" or "IEmbeddingGenerator" or "ICallerScopeStore";
            }

            if (typeNs.Equals("BeeMemoryBank.Core.Services", StringComparison.Ordinal))
            {
                return typeName is "ICallerScopeStore" or "InstanceCallerScopeStore" or "CallerScopeHolder" or "MediaStorageOptions" or "NullEventLogger" or "ConceptTagService" or "FolderAccessService";
            }

            return false;
        }

        if (typeNs.StartsWith("BeeMemoryBank.Storage", StringComparison.Ordinal))
        {
            // SQLite connection, migrations, blind repositories, dapper config
            if (typeNs.Equals("BeeMemoryBank.Storage.Sqlite", StringComparison.Ordinal))
            {
                return typeName is "DbConnectionFactory" or "MigrationRunner" or "NodeIdentityRepository" or "WhitelistRepository" or "EventLogRepository" or "DapperConfig" or "ArticleRepository" or "ArticleBodyRepository" or "BlobRepository" or "SyncPositionRepository" or "TombstoneRepository" or "ConflictVersionRepository" or "CommentRepository" or "FolderRepository" or "MediaRepository" or "ConceptTagRepository" or "RestoreReplayShieldRepository" or "RestoreEventStateRepository" or "DekRotationStateRepository" or "SyncQuarantineRepository";
            }

            if (typeNs.Equals("BeeMemoryBank.Core.Interfaces", StringComparison.Ordinal))
            {
                return typeName is "INodeIdentityRepository" or "IWhitelistRepository" or "IEventLogRepository";
            }

            return false;
        }

        if (typeNs.StartsWith("BeeMemoryBank.Crypto", StringComparison.Ordinal))
        {
            // Ed25519 signer, node identity crypto, SPKI pinning, and blind pairing secret
            if (typeNs.Equals("BeeMemoryBank.Crypto", StringComparison.Ordinal))
            {
                return typeName is "Ed25519Signer" or "NodeIdentityCrypto" or "SpkiPin" or "BlindPairingSecret";
            }

            return false;
        }

        if (typeNs.StartsWith("BeeMemoryBank.Sync", StringComparison.Ordinal))
        {
            if (typeNs.Equals("BeeMemoryBank.Sync.Blind", StringComparison.Ordinal))
                // Stage 4 adds the four of the backup: its package source and fetcher contract, the package it fetches, the recovery-set source.
                return typeName is "BlindPhoneReplicaClient" or "BlindPhonePullClient" or "IBlindPhonePullClient" or "BlindEmbeddingGenerator" or "BlindState" or "BlindRestoreInitiator" or "BlindDekRotationApplier"
                    or "BlindPhonePackageSource" or "IBlindVerifiedPackageFetcher" or "VerifiedReplicaPackage" or "BlindPhoneRecoverySetSource";
            if (typeNs.Equals("BeeMemoryBank.Sync", StringComparison.Ordinal))
                return typeName is "IRestoreInitiator" or "LamportClock" or "HardDeleteService" or "EventApplier" or "SnapshotRequiredException";
            return false;
        }

        return false;
    }

    private sealed class DummyKeys : IBlindNodeKeys
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => null;
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

    private sealed class DummyLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }
}
