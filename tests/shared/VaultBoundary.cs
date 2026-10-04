namespace BeeMemoryBank.Boundary;

/// <summary>
/// What a node that cannot read must not contain, as names. The type list is the vault itself: every type BeeMemoryBank.Vault defines
/// (docs/vault-split/vault-types.txt, regenerated and checked by VaultBoundaryListTests so it cannot drift), plus a short list of names
/// that must be forbidden even if that file were empty or stale. A type name here is the full name WITHOUT the assembly, because the
/// vault types kept the namespaces they had.
/// </summary>
internal static class VaultBoundary
{
    /// <summary>Repo-relative path of the generated list.</summary>
    public const string ListFile = "docs/vault-split/vault-types.txt";

    /// <summary>Forbidden whatever the generated list says: the master key, the session and the content crypto.</summary>
    public static readonly string[] ExplicitTypes =
    [
        "BeeMemoryBank.Core.Services.SessionService",
        "BeeMemoryBank.Core.Services.CommentService",
        "BeeMemoryBank.Core.Services.MediaService",
        "BeeMemoryBank.Core.Services.RemoteAccountService",
        "BeeMemoryBank.Core.Services.NodeDataKeyEnvelope",
        "BeeMemoryBank.Core.Services.ArticleService",
        "BeeMemoryBank.Core.Services.KeyManagementService",
        "BeeMemoryBank.Core.Services.InitializationService",
        "BeeMemoryBank.Crypto.MasterKeyManager",
        "BeeMemoryBank.Crypto.DekManager",
        "BeeMemoryBank.Crypto.ArticleEncryptor",
        "BeeMemoryBank.Crypto.MediaEncryptor",
        "BeeMemoryBank.Crypto.ProtectedContentCodec",
        "BeeMemoryBank.Crypto.EnvelopeFraming",
        "BeeMemoryBank.Crypto.RecoveryBoxCrypto",
        "BeeMemoryBank.Crypto.NodeIdentityVault",
        "BeeMemoryBank.Sync.EventLogger",
        "BeeMemoryBank.Sync.SessionNodeAuthSigner",
        "BeeMemoryBank.Sync.RemoteSentinelVerifier",
        "BeeMemoryBank.Sync.LazySlotRewrapService",
        "BeeMemoryBank.Sync.Recovery.RecoveryReconciler",
        "BeeMemoryBank.Sync.Recovery.DeviceBoxPublisher",
        "BeeMemoryBank.Sync.Recovery.StateAnchorService",
        "BeeMemoryBank.Sync.Recovery.SealedSecretService",
        "BeeMemoryBank.Sync.DekRotation.PeerDekRotationApplier",
        "BeeMemoryBank.Storage.Sqlite.RetiredMasterDekStore",
        "BeeMemoryBank.Api.Services.SessionSnapshotKeyOperations",
        "BeeMemoryBank.Api.Services.Recovery.StrongBoxService",
        "BeeMemoryBank.Api.Services.BlindNodeManager",
        "BeeMemoryBank.Api.Services.BlindPreflight",
        // The contract, not the generated list: these must stay out of every blind binary even if someone moves them out of the Vault
        // and regenerates docs/vault-split/vault-types.txt (a name that does not resolve to a real type fails VaultBoundaryNamesTests).
        "BeeMemoryBank.Crypto.AesGcmHelper",
        "BeeMemoryBank.Crypto.StateAnchorCrypto",
        "BeeMemoryBank.Crypto.SealedSecretCrypto",
        "BeeMemoryBank.Crypto.DekEnvelope",
        "BeeMemoryBank.Core.Services.RestoreService",
        "BeeMemoryBank.Core.Services.SearchService",
        "BeeMemoryBank.Core.Services.FolderService",
        "BeeMemoryBank.Core.Services.UserService",
        "BeeMemoryBank.Core.Services.RoleService",
        "BeeMemoryBank.Core.Services.RemoteEventApplier",
        "BeeMemoryBank.Sync.SnapshotJoinClient",
        "BeeMemoryBank.Sync.Recovery.RecoveryKeyResolver",
        "BeeMemoryBank.Sync.Recovery.RecoveryEventPublisher",
        "BeeMemoryBank.Sync.DekRotation.DekRewrapper",
        "BeeMemoryBank.Sync.DekRotation.DekRotationMaterial",
        "BeeMemoryBank.Search.Indexing.IndexBuilder",
        "BeeMemoryBank.Storage.Search.EncryptedSegmentStore"
    ];

    /// <summary>Member names that mean "hand out or try the master data key": no shared type may define or call one.</summary>
    public static readonly string[] Members = ["GetMasterDek", "GetCandidateDeks", "TryUnwrapWithCandidates", "VerifySentinel"];

    public static readonly string[] Assemblies = ["BeeMemoryBank.Vault"];

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the tests run from inside the repository");
    }

    /// <summary>The generated list of Vault types (empty lines and # comments skipped).</summary>
    public static IReadOnlyList<string> ListedVaultTypes() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), ListFile.Replace('/', Path.DirectorySeparatorChar)))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    public static AssemblyBoundaryScanner.Forbidden Forbidden() =>
        new(ListedVaultTypes().Concat(ExplicitTypes).ToHashSet(StringComparer.Ordinal),
            Members.ToHashSet(StringComparer.Ordinal),
            Assemblies.ToHashSet(StringComparer.Ordinal));
}
