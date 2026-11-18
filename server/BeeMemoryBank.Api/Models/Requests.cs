namespace BeeMemoryBank.Api.Models;

public record UnlockRequest(string Password);

public record SetAccountsHomeRequest(Guid NodeId);

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

public record ChangePasswordRequest(string OldPassword, string NewPassword, string? PasswordHint = null);

/// <summary>POST /api/keys/add-recovery: the key belongs to the signed-in superadmin who issues it; only a label is the caller's to give.</summary>
public record AddRecoveryKeyRequest(string? Label = null);

/// <summary>POST /api/keys/recovery-revoke: which recovery key (its slot) to revoke.</summary>
public record RevokeRecoveryKeyRequest(int SlotId);

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

/// <summary>Set device ownership (shared infrastructure or personal to an active user's person_id). See PUT /api/whitelist/{nodeId}/ownership.</summary>
public record SetPeerOwnershipRequest(string Ownership, Guid? OwnerPersonId = null);

public record ReviewOwnershipItem(Guid NodeId, string Ownership, Guid? OwnerPersonId = null);

/// <summary>Batch ownership review confirmation or explicit per-device decisions. See POST /api/whitelist/review-ownership.</summary>
public record ReviewOwnershipRequest(List<ReviewOwnershipItem>? Items = null, bool ConfirmAll = false);

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

/// <param name="ProtocolVersion">The sync protocol the joiner declares (2.6 and later send it; docs/design/step-a-spec.md 5.1, 11).</param>
/// <param name="Capabilities">What the joiner advertises (<c>metadata-envelope-v1</c>); a 2.5 joiner sends neither.</param>
/// <param name="Endpoints">Where the joiner's LAN listener will serve its sync routes (docs/design/lan-peer-sync.md 8); absent from older joiners.</param>
/// <param name="TlsLeafSpki">The pin of the joiner's TLS leaf key; absent from older joiners.</param>
/// <param name="TlsCaKey">The joiner's local CA public key (base64url SubjectPublicKeyInfo); absent from older joiners.</param>
/// <param name="LanPort">The joiner's LAN port; absent from older joiners.</param>
public record JoinRequest(
    string MasterPassword,
    Guid NodeId,
    string DisplayName,
    string Ed25519PublicKeyB64,
    string? ApiAddress = null,
    int? ProtocolVersion = null,
    string[]? Capabilities = null,
    List<BeeMemoryBank.Sync.NodeEndpointEntry>? Endpoints = null,
    string? TlsLeafSpki = null,
    string? TlsCaKey = null,
    int? LanPort = null,
    string? Login = null,
    string? Ownership = null);

/// <summary>
/// <c>POST /api/join/abort</c>: the joiner of <see cref="NodeId"/> takes back the row its join made. The same proof as the join (the master
/// password), and the key the join used, so the request names the attempt and nothing else.
/// </summary>
public record JoinAbortRequest(string MasterPassword, Guid NodeId, string Ed25519PublicKeyB64);

public record LoginRequest(string Username, string Password);

/// <param name="ClientIp">The browser's address as the Web layer saw it; for the throttle and the audit log only.</param>
public record RecoverAccessRequest(string Username, string RecoveryKey, string NewPassword, string? ClientIp = null, string? PasswordHint = null);

/// <param name="ClientIp">The browser's address as the Web layer saw it; for the per-client throttle only.</param>
/// <summary>POST /api/session/password-hint: the sign-in page's "Show my password hint", anonymous.</summary>
public record PasswordHintRequest(string Username, string? ClientIp = null);

/// <summary>POST /api/users/me/password-hint: the signed-in person adds or replaces their own hint.</summary>
public record SetPasswordHintRequest(string PasswordHint);

public record SessionSettingsRequest(int ExpireHours, bool SlidingExpiration);

public record CreateUserRequest(string Username, string DisplayName, string Password, string Role, bool ChatAccess = true, string? PasswordHint = null);

/// <param name="AllowOpenRole">Only for taking superadmin rights away: the caller says out loud that the person may see everything an open role sees. Never the default.</param>
/// <param name="FolderRules">Only for taking superadmin rights away: the folders (per-user rules, built-in 'user' role only) the person may see afterwards.</param>
/// <param name="AdminPassword">Taking superadmin rights away is a removal process (release 2.12.0): it starts only with the administrator's own password.</param>
public record UpdateUserRequest(string DisplayName, string? Role = null, string? Password = null, bool? ChatAccess = null, string? PasswordHint = null,
    bool? AllowOpenRole = null, List<AddAclEntryRequest>? FolderRules = null, string? AdminPassword = null);

/// <summary>
/// Starts the removal of a superadmin (release 2.12.0, plan E1/E3). <paramref name="Target"/> is <c>delete</c> or <c>demote</c>; a demotion names the role
/// and says what the person may see (folders, a role that is not open, or <paramref name="AllowOpenRole"/>). <paramref name="Password"/> is the
/// administrator's own. <paramref name="ActingUser"/>: a machine-local caller (the CLI), which names no signed-in person, says who acts.
/// </summary>
public record StartOffboardingRequest(int UserId, string Target, string? Role = null, List<AddAclEntryRequest>? FolderRules = null,
    bool AllowOpenRole = false, string Password = "", string? ActingUser = null);

/// <summary>"Continue" a removal; the password only when its rotation (S4) needs it.</summary>
public record ContinueOffboardingRequest(string? Password = null, string? ActingUser = null);

/// <summary>One decision of the S3 review; removing another superadmin says what happens to their account.</summary>
public record OffboardingReviewDecisionRequest(string ItemId, string Decision, string? Reason = null, string? Target = null, string? Role = null,
    List<AddAclEntryRequest>? FolderRules = null, bool AllowOpenRole = false);

/// <summary>The decisions of S3; <paramref name="AcceptDefaults"/> takes the pre-selected choice for every item still open.</summary>
public record ResolveOffboardingReviewRequest(List<OffboardingReviewDecisionRequest>? Decisions = null, bool AcceptDefaults = false,
    string? Password = null, string? ActingUser = null);

/// <summary>Take a removal over on a shared full node (the node where it started was revoked).</summary>
public record TakeOverOffboardingRequest(string Password = "", string? ActingUser = null);

/// <summary>Deleting a superadmin is a removal process (release 2.12.0): it starts only with the administrator's own password.</summary>
public record DeleteUserRequest(string? AdminPassword = null, string? ActingUser = null);

public record ChangeUserPasswordRequest(string NewPassword, string? PasswordHint = null);

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

/// <param name="StandaloneMode">Older clients: true = <see cref="SnapshotRestoreModes.Standalone"/>, false = <see cref="SnapshotRestoreModes.KeepIdentity"/> (refused since release 2.12). Ignored when <paramref name="Mode"/> is given.</param>
/// <param name="Mode">One of <see cref="SnapshotRestoreModes"/>; names what the restore does to this node's identity.</param>
public record RestoreSnapshotRequest(string FileName, string MasterPassword, bool CreateBackupFirst = true, bool StandaloneMode = false, string? Mode = null);

/// <summary>
/// The ways a snapshot can be restored, named as they are shown and as they are written to the audit log.
/// <see cref="Standalone"/> is served by <c>POST /api/snapshots/restore</c>; <see cref="Network"/> by
/// <c>POST /api/snapshots/restore-network</c>. <see cref="KeepIdentity"/> is refused (release 2.12, plan R2).
/// </summary>
public static class SnapshotRestoreModes
{
    /// <summary>This node only: it leaves the network and becomes a new node (new id, new key pair, no trusted nodes, blind copies or sync history).</summary>
    public const string Standalone = "standalone";

    /// <summary>
    /// Was: the snapshot's database replaced this one as it was, identity and trusted nodes included. Refused since release 2.12
    /// (plan R2): it would put back an identity key this node may have retired, which anybody holding an old backup can sign with.
    /// The name stays only so that the refusal can say why.
    /// </summary>
    public const string KeepIdentity = "keep-identity";

    /// <summary>What a request for <see cref="KeepIdentity"/> (or an older client's <c>standaloneMode: false</c>) is told.</summary>
    public const string KeepIdentityRefusal =
        "Restoring a snapshot over this node with the identity it had then is no longer offered: it could bring back an identity key " +
        "this node has retired. Use \"standalone\" (this node becomes a new node with the snapshot's data), or restore the whole network.";

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
                return true;
            case KeepIdentity:
                refusal = KeepIdentityRefusal;
                return false;
            case Network:
                refusal = "Restoring the whole network is started with /api/snapshots/restore-network, not here.";
                return false;
            default:
                refusal = $"Unknown restore mode. Use \"{Standalone}\".";
                return false;
        }
    }
}

public record InitStandaloneRequest(string AdminUsername, string DisplayName, string Password, string? PasswordHint = null);

/// <param name="JoinCode">
/// The join code another computer's "Connect a device" card shows (address, one-time token, certificate pin). When given, the
/// code's address is used instead of <paramref name="RemoteUrl"/> (which may then be empty), every request of the join goes only to a
/// server holding the pinned key, and <c>/api/join</c> carries the code's token. Null: the address and the ordinary certificate check.
/// </param>
public record InitJoinRequest(string AdminUsername, string DisplayName, string RemoteUrl, string Password, string? JoinCode = null, string? PasswordHint = null,
    string? Login = null, bool Shared = false);

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

/// <param name="Reason">Null, or <c>offboarding:&lt;process id&gt;</c> for the rotation that ends a removal (2.12, applied by every node by itself).</param>
public record ProposeDekRotationRequest(string MasterPassword, string? Reason = null);

public record AcceptDekRotationRequest(string CommitEventId, string MasterPassword);

/// <summary>Null or blank clears the override and restores the built-in product name.</summary>
public record BrandingRequest(string? Name);

/// <summary>Direction for a one-step favorite move: "up" or "down".</summary>
public record FavoriteMoveRequest(string Direction);

