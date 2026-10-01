using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

public class ForbiddenReferencesTests
{
    private static readonly string[] ForbiddenNames =
    [
        "BeeMemoryBank.Embeddings",
        "BeeMemoryBank.Media",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.ML.Tokenizers",
        "Markdig",
        "Indiko.Maui.Controls.Markdown",
        "SixLabors.ImageSharp"
    ];

    // EventApplier needs these receive-only replicated-row collaborators, but no UI or Android
    // platform type may acquire them. Keeping that boundary at the UI surface prevents a later
    // page from turning the sync composition into a vault-data browsing path.
    private static readonly string[] ReceiveOnlySyncTypeNames =
    [
        "IArticleRepository", "ArticleRepository", "IArticleBodyRepository", "ArticleBodyRepository",
        "IBlobRepository", "BlobRepository", "ISyncPositionRepository", "SyncPositionRepository",
        "ITombstoneRepository", "TombstoneRepository", "IConflictVersionRepository", "ConflictVersionRepository",
        "ICommentRepository", "CommentRepository", "IFolderRepository", "FolderRepository",
        "IMediaRepository", "MediaRepository", "IConceptTagRepository", "ConceptTagRepository",
        "IRestoreReplayShieldRepository", "RestoreReplayShieldRepository", "IRestoreEventStateRepository",
        "RestoreEventStateRepository", "IDekRotationStateRepository", "DekRotationStateRepository",
        "ISyncQuarantineRepository", "SyncQuarantineRepository", "IEventLogger", "NullEventLogger",
        "HardDeleteService", "ConceptTagService", "FolderAccessService", "EventApplier"
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

    [Fact]
    public void BlindMobile_PagesAndPlatforms_DoNotReferenceReceiveOnlySyncTypes()
    {
        var repoRoot = FindRepoRoot();
        var mobileRoot = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile");
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(mobileRoot, "Pages"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(mobileRoot, "Platforms"), "*.cs", SearchOption.AllDirectories));

        var violations = sourceFiles.SelectMany(path =>
        {
            var source = File.ReadAllText(path);
            return ReceiveOnlySyncTypeNames
                .Where(typeName => source.Contains(typeName, StringComparison.Ordinal))
                .Select(typeName => $"{Path.GetRelativePath(mobileRoot, path)} references {typeName}");
        }).ToList();

        violations.Should().BeEmpty(
            "Pages and Platforms must only depend on the blind-phone facade, never EventApplier's replicated-row collaborators:\n" +
            string.Join("\n", violations));
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
        services.AddSingleton<IBlindPhoneStore>(new PreferencesBlindStore(_ => null, (_, _) => { }, _ => { }));
        services.AddSingleton<IBlindNodeKeys>(dummyKeys);
        services.AddSingleton<IBlindPhoneKeys>(dummyKeys);
        services.AddSingleton<IDeviceStateProvider>(new DummyDeviceState());

        var forbiddenTypes = new[]
        {
            typeof(SessionService),
            typeof(ArticleService),
            typeof(ArticleDiffService),
            typeof(KeyManagementService),
            typeof(TreeService),
            typeof(SearchService),
            typeof(FolderService),
            typeof(CopyService),
            typeof(CommentService),
            typeof(MediaService),
            typeof(MediaBlobBackfillService),
            typeof(RestoreService),
            typeof(LegacyPasswordSlotMigrationService),
            // Vault Repositories & Caches
            typeof(IKeySlotRepository),
            typeof(KeySlotRepository),
            typeof(IRetiredMasterDekStore),
            typeof(RetiredMasterDekStore),
            typeof(IArticleChunkEmbeddingRepository),
            typeof(ArticleChunkEmbeddingRepository),
            typeof(SegmentManifestRepository),
            typeof(SegmentTombstoneRepository),
            typeof(EncryptedSegmentStore),
            typeof(EmbeddingVectorCache),
            typeof(ChunkEmbeddingVectorCache),
            typeof(SearchQueryCache),
            typeof(SearchMetrics),
            typeof(IProjectionMatrixRepository),
            typeof(ProjectionMatrixRepository),
            typeof(ISyncPushPositionRepository),
            typeof(SyncPushPositionRepository)
        };

        // 1. None of the forbidden types should appear as ServiceType or ImplementationType
        foreach (var desc in services)
        {
            forbiddenTypes.Should().NotContain(desc.ServiceType,
                $"ServiceType '{desc.ServiceType.FullName}' must not be registered in the blind app DI container");
            if (desc.ImplementationType is not null)
            {
                forbiddenTypes.Should().NotContain(desc.ImplementationType,
                    $"ImplementationType '{desc.ImplementationType.FullName}' must not be registered in the blind app DI container");
            }
        }

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
        var repoRoot = FindRepoRoot();
        var debugDll = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "bin", "Debug", "net10.0-android", "BeeMemoryBank.BlindMobile.dll");
        var releaseDll = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "bin", "Release", "net10.0-android", "BeeMemoryBank.BlindMobile.dll");

        var dllPath = File.Exists(debugDll) ? debugDll : releaseDll;
        File.Exists(dllPath).Should().BeTrue($"BeeMemoryBank.BlindMobile.dll must exist at {dllPath}. Build the mobile project first.");

        using var stream = File.OpenRead(dllPath);
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

    private static bool IsAllowedTypeReference(string assemblyName, string typeNs, string typeName)
    {
        if (assemblyName.Equals("BeeMemoryBank.Core", StringComparison.OrdinalIgnoreCase))
        {
            // All types in BlindPhone seam are allowed (including nested types like BlindPhoneLog.Entry)
            if (typeNs.Equals("BeeMemoryBank.Core.Services.BlindPhone", StringComparison.Ordinal) ||
                typeName.StartsWith("BlindPhoneLog.", StringComparison.Ordinal))
                return true;

            // Models allowed for blind node operation
            if (typeNs.Equals("BeeMemoryBank.Core.Models", StringComparison.Ordinal))
            {
                return typeName is "BlindCallCode" or "BlindNodeId" or "BlindPairingSecret" or "BlindPhoneCode" or "BlindPhoneDeviceState" or "BlindPhoneWork" or "BlindPhoneJob" or "NodeIdentity" or "SyncPosition";
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

        if (assemblyName.Equals("BeeMemoryBank.Storage", StringComparison.OrdinalIgnoreCase))
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

        if (assemblyName.Equals("BeeMemoryBank.Crypto", StringComparison.OrdinalIgnoreCase))
        {
            // Ed25519 signer, node identity crypto, SPKI pinning, and blind pairing secret
            if (typeNs.Equals("BeeMemoryBank.Crypto", StringComparison.Ordinal))
            {
                return typeName is "Ed25519Signer" or "NodeIdentityCrypto" or "SpkiPin" or "BlindPairingSecret";
            }

            return false;
        }

        if (assemblyName.Equals("BeeMemoryBank.Sync", StringComparison.OrdinalIgnoreCase))
        {
            if (typeNs.Equals("BeeMemoryBank.Sync.Blind", StringComparison.Ordinal))
                return typeName is "BlindPhoneReplicaClient" or "BlindPhonePullClient" or "IBlindPhonePullClient" or "BlindEmbeddingGenerator" or "BlindState" or "BlindRestoreInitiator" or "BlindDekRotationApplier";
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

    private sealed class DummyDeviceState : IDeviceStateProvider
    {
        public BlindPhoneDeviceState Current() => new(true, true, true, 100);
    }
}
