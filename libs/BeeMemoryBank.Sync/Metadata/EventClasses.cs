namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// Which event types carry human-authored metadata and therefore travel sealed after activation (docs/design/step-a-spec.md 3.1).
/// Every constant of <see cref="EventTypes"/> is in exactly one of <see cref="Sealable"/>, <see cref="Control"/> or is <see cref="Sealed"/>.
/// </summary>
public static class EventClasses
{
    /// <summary>The outer type of every protocol-4 content event.</summary>
    public const string Sealed = "sealed";

    /// <summary>The two new control types (spec 4.3, 5.4). EventTypes declares aliases of these three constants.</summary>
    public const string MetadataKeySet = "metadata_key_set";
    public const string MetadataActivation = "metadata_activation";

    /// <summary>Protocol-4 event version: content sealed, control clear.</summary>
    public const int SealedProtocol = 4;

    public static readonly IReadOnlySet<string> Sealable = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypes.ArticleCreate, EventTypes.ArticleUpdate, EventTypes.ArticleDelete,
        EventTypes.CommentCreate, EventTypes.CommentDelete,
        EventTypes.FolderCreate, EventTypes.FolderRename, EventTypes.FolderDelete,
        EventTypes.MediaCreate, EventTypes.MediaDelete, EventTypes.MediaLink,
        EventTypes.ConceptTagRename, EventTypes.ConceptTagMerge, EventTypes.ConceptTagDelete,
        EventTypes.HardDelete,
    };

    public static readonly IReadOnlySet<string> Control = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypes.WhitelistAdd, EventTypes.WhitelistRevoke, EventTypes.WhitelistUpdate,
        EventTypes.SnapshotCheckpoint, EventTypes.RestoreNetwork,
        EventTypes.DekRotationProposed, EventTypes.DekRotationCommit, EventTypes.MasterPasswordChanged,
        EventTypes.RecoveryBoxSet, EventTypes.RecoveryBoxRetire, EventTypes.RetiredLinkSet,
        EventTypes.StateAnchor, EventTypes.SealedSecretSet,
        MetadataKeySet, MetadataActivation,
        // Clear on purpose (decision D3): only private and VPN addresses and public keys; a locked node can read it, and ADR 0009 already accepts
        // peer addresses and TLS pins in clear. It reaches only nodes that advertise peer-endpoints-v1.
        EventTypes.NodeEndpoints,
        // Control metadata only (no user/vault secrets); blind and full nodes both author their own.
        EventTypes.NodeState,
        // Public keys, counters, event ids and signatures only; every node, a locked or blind one included, must verify with the new key at once.
        EventTypes.NodeKeyRotated, EventTypes.NodeKeyConfirm,
        // Accounts home and encrypted superadmin doors list (only shared full nodes receive list envelopes).
        EventTypes.AccountsHomeSet,
        EventTypes.SuperadminDoorsSet,
        // The removal of a superadmin (release 2.12.0): ids, an outcome and times, no names; full nodes must read them while locked too.
        EventTypes.PersonRemoved,
        EventTypes.OffboardingState,
        EventTypes.NodeRekeyRequest,
    };

    public static bool IsSealable(string eventType) => Sealable.Contains(eventType);

    /// <summary>A protocol-1..3 event whose type is a content type: never served to a blind peer after activation.</summary>
    public static bool IsClearContent(int protocolVersion, string eventType) => protocolVersion < SealedProtocol && Sealable.Contains(eventType);
}
