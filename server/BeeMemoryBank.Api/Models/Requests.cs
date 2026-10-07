namespace BeeMemoryBank.Api.Models;

public record UnlockRequest(string Password);

public record CreateArticleRequest(
    string Title,
    string TreePath,
    string Content,
    List<string>? ConceptTags = null,
    // Create the article ALREADY protected: the body is wrapped before the first save, so the
    // plaintext never reaches the event log / sync. Omit for a normal (plaintext) article.
    string? Passphrase = null,
    string? Hint = null,
    // File attachments uploaded (unlinked) while the article was still being written; linked to
    // the new article once it exists. Not allowed together with Passphrase.
    List<Guid>? AttachmentIds = null);

public record UpdateArticleRequest(
    string? Title = null,
    string? TreePath = null,
    List<string>? ConceptTags = null,
    string? Content = null,
    // Required only when editing the CONTENT of a protected article — used to re-wrap the new body
    // under the same passphrase (verified against the existing body first).
    string? Passphrase = null);

// Second-layer ("protected article") requests.
public record ProtectArticleRequest(string Passphrase, string? Hint = null);
public record UnprotectArticleRequest(string Passphrase);
public record ChangeArticlePassphraseRequest(string OldPassphrase, string NewPassphrase, string? Hint = null);
public record UnlockArticleRequest(string Passphrase);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

public record UpdateWhitelistEntryRequest(
    string? DisplayName = null,
    string? ApiAddress = null,
    bool? CanGenerateEmbeddings = null);

public record ChangeNodeAddressRequest(string NewApiAddress, string Password);

/// <summary>
/// Let blind copies call a node, or stop. See PUT /api/whitelist/{nodeId}/hub. <c>Trust</c> is <c>pin</c>,
/// <c>public-ca</c> or <c>off</c>; <c>Address</c> (an https origin) defaults to the node's current one;
/// <c>ExpectedPin</c> (pin mode) is the key the administrator already knows the node by.
/// </summary>
public record SetHubRequest(string Trust, string? Address, string Password, string? ExpectedPin = null);

/// <summary>Promote or demote a peer. See PUT /api/whitelist/{nodeId}/superadmin.</summary>
public record SetPeerSuperadminRequest(bool IsSuperadmin);

public record SemanticSearchRequest(string Query, int TopK = 10);

/// <summary>
/// <c>Mode</c> is a string (not the <c>SearchMode</c> enum directly) so an unrecognized value
/// produces a clean 400 from the endpoint's own parsing rather than an ASP.NET model-binding error
/// whose message leaks enum member names. "hybrid" is the default: RRF-combined keyword + semantic
/// ranking, the mode most search callers actually want.
/// </summary>
public record HybridSearchRequest(string Query, string Mode = "hybrid", int TopK = 20);

public record MoveArticleRequest(string NewPath);

public record CreateFolderRequest(string Path);

public record RenameFolderRequest(string NewPath);

public record MoveFolderRequest(string NewParentPath);

public record AddCommentRequest(Guid ArticleId, string Text);

public record CreateAgentRequest(string Name, string? Description);

public record JoinRequest(
    string MasterPassword,
    Guid NodeId,
    string DisplayName,
    string Ed25519PublicKeyB64,
    string? ApiAddress = null);

public record LoginRequest(string Username, string Password);

/// <param name="ClientIp">The browser's address as the Web layer saw it; for the throttle and the audit log only.</param>
public record RecoverAccessRequest(string Username, string RecoveryKey, string NewPassword, string? ClientIp = null);

public record SessionSettingsRequest(int ExpireHours, bool SlidingExpiration);

public record CreateUserRequest(string Username, string DisplayName, string Password, string Role, bool ChatAccess = true);

public record UpdateUserRequest(string DisplayName, string? Role = null, string? Password = null, bool? ChatAccess = null);

public record ChangeUserPasswordRequest(string NewPassword);

public record AddAclEntryRequest(Guid FolderId, string Effect, bool IsReadOnly = false);

public record UpdateAclReadOnlyRequest(bool IsReadOnly);

// BasePolicy has no default on purpose. It decides what "this role has no allow rows" means, so
// a caller that omits it would silently pick a visibility policy — an absent value must fail
// validation loudly instead.
public record CreateRoleRequest(string Name, string DisplayName, string? Description, string BasePolicy);

public record UpdateRoleRequest(string DisplayName, string? Description, string BasePolicy);

public record CopyArticleRequest(string TargetFolderPath);

public record CopyFolderRequest(string TargetParentPath);

public record RemoteTokenIssueRequest(string Username, string Password, string? Label = null);

public record CreateRemoteAccountRequest(string DisplayName, string BaseUrl, string Username, string Password);

public record AddRemoteSubscriptionRequest(Guid RemoteAccountId, Guid RemoteFolderId, string RemoteFolderPath, string MountPath);

/// <param name="StandaloneMode">Older clients: true = <see cref="SnapshotRestoreModes.Standalone"/>, false = <see cref="SnapshotRestoreModes.KeepIdentity"/>. Ignored when <paramref name="Mode"/> is given.</param>
/// <param name="Mode">One of <see cref="SnapshotRestoreModes"/>; names what the restore does to this node's identity.</param>
public record RestoreSnapshotRequest(string FileName, string MasterPassword, bool CreateBackupFirst = true, bool StandaloneMode = false, string? Mode = null);

/// <summary>
/// The ways a snapshot can be restored, named as they are shown and as they are written to the audit log.
/// <see cref="Standalone"/> and <see cref="KeepIdentity"/> are served by <c>POST /api/snapshots/restore</c>;
/// <see cref="Network"/> by <c>POST /api/snapshots/restore-network</c>.
/// </summary>
public static class SnapshotRestoreModes
{
    /// <summary>This node only: it leaves the network and becomes a new node (new id, new key pair, no trusted nodes, blind copies or sync history).</summary>
    public const string Standalone = "standalone";

    /// <summary>The snapshot's database replaces this one as it is, identity and trusted nodes included. Only a snapshot of this very node leaves the network coherent.</summary>
    public const string KeepIdentity = "keep-identity";

    /// <summary>The whole network: every trusted node is told to restore the same snapshot (the originator keeps its identity).</summary>
    public const string Network = "network";

    /// <summary>
    /// The mode a <c>/restore</c> request asks for, or why it cannot be served. <paramref name="legacyStandalone"/> is read only when
    /// <paramref name="mode"/> is absent.
    /// </summary>
    public static bool TryResolveLocal(string? mode, bool legacyStandalone, out string resolved, out string? refusal)
    {
        refusal = null;
        resolved = mode is null or "" ? (legacyStandalone ? Standalone : KeepIdentity) : mode.Trim().ToLowerInvariant();
        switch (resolved)
        {
            case Standalone:
            case KeepIdentity:
                return true;
            case Network:
                refusal = "Restoring the whole network is started with /api/snapshots/restore-network, not here.";
                return false;
            default:
                refusal = $"Unknown restore mode. Use \"{Standalone}\" or \"{KeepIdentity}\".";
                return false;
        }
    }
}

public record InitStandaloneRequest(string AdminUsername, string DisplayName, string Password);

/// <param name="JoinCode">
/// The join code another computer's "Connect a device" card shows (address, one-time token, certificate pin). When given, the
/// code's address is used instead of <paramref name="RemoteUrl"/> (which may then be empty), every request of the join goes only to a
/// server holding the pinned key, and <c>/api/join</c> carries the code's token. Null: the address and the ordinary certificate check.
/// </param>
public record InitJoinRequest(string AdminUsername, string DisplayName, string RemoteUrl, string Password, string? JoinCode = null);

public record ResetRequest(string MasterPassword);

public record PreviewFolderRequest(string Path);

public record HardDeleteFolderRequest(string Path);

public enum RestoreMode
{
    NetworkWide,
    Standalone
}

public record RestoreInitiationRequest(
    Guid SnapshotFileId,
    RestoreMode Mode,
    string? ForeignMasterPassword,  // node-only restore: the master password the snapshot was made under
    // The master password, re-entered: when given it must open this node's vault or the request is refused. The Admin page always sends it
    // (a restore for the whole network is the most destructive thing it offers); the CLI runs on the node itself and may leave it out.
    string? MasterPassword = null,
    // The snapshot by its file name, for the ones a node made itself (their names carry no id); SnapshotFileId finds uploaded ones.
    string? FileName = null
);

public record RestoreContinueWithoutBackupRequest(
    Guid EventId,
    string MasterPassword
);

public record SetAutoAcceptRestoreRequest(bool AutoAccept);

public record SetAutoAcceptDekRotationRequest(bool AutoAccept);

public record InitiateDekRotationRequest(string MasterPassword);

public record DekRotationCancelRequest(string EventId);

public record DekRotationAcceptRequest(string CommitEventId);

public record ProposeDekRotationRequest(string MasterPassword);

public record AcceptDekRotationRequest(string CommitEventId, string MasterPassword);

/// <summary>Null or blank clears the override and restores the built-in product name.</summary>
public record BrandingRequest(string? Name);

/// <summary>Direction for a one-step favorite move: "up" or "down".</summary>
public record FavoriteMoveRequest(string Direction);

