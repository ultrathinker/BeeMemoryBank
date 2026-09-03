using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Models;
namespace BeeMemoryBank.Api.Models;

public record AgentListItem(int Id, string Name, string? Description, string KeyPrefix, DateTime CreatedAt, DateTime? LastAccessedAt, long RequestCount, int OwnerUserId = 0, string? OwnerName = null);

public record AgentCreatedResponse(int Id, string Name, string ApiKey);

public record ArticleResponse(
    Guid Id,
    string Title,
    string TreePath,
    List<string> ConceptTags,
    bool EmbeddingPending,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Protected = false,
    string? ProtectionHint = null)
{
    public static ArticleResponse From(BeeMemoryBank.Core.Models.Article a, List<string> conceptTags) => new(
        a.Id, a.Title, a.TreePath, conceptTags, a.EmbeddingPending, a.Status, a.CreatedAt, a.UpdatedAt,
        a.Protected, a.ProtectionHint);
}

public record ArticleContentResponse(Guid Id, string Content);

// Edit-load helper for protected articles: Unlocked=true means Content holds the plaintext (either
// a non-protected body or one unlocked from the recent-unlock cache). Unlocked=false → Edit shows the
// passphrase gate (Content is null).
public record EditContentResponse(Guid Id, bool Protected, bool Unlocked, string? Content);

public record SessionStatusResponse(bool IsUnlocked);

public record SessionSettingsResponse(int ExpireHours, bool SlidingExpiration);

public record UnlockResponse(bool IsUnlocked, string? MigratedSyntheticUsername);

public record RecoveryKeyResponse(string RecoveryKey);

/// <summary>Response for GET /api/keys/auto-unlock/status and the enable/disable endpoints.</summary>
/// <param name="Enabled">Whether the os_auto_unlock slot is currently active.</param>
/// <param name="Supported">Whether the current platform supports OS auto-unlock (Windows only).</param>
public record AutoUnlockStatusResponse(bool Enabled, bool Supported);

public record ErrorResponse(string Error);

public record FolderInfoResponse(Guid Id, string Path, string Name, int ArticleCount, DateTime CreatedAt, DateTime UpdatedAt, bool IsSystem = false, bool IsRemote = false)
{
    public static FolderInfoResponse From(BeeMemoryBank.Core.Models.Folder f, int articleCount = 0) =>
        new(f.Id, f.Path, f.Name, articleCount, f.CreatedAt, f.UpdatedAt, f.IsSystem, f.RemoteSubscriptionId.HasValue);
}

public record SearchResponse(
    List<FolderInfoResponse> Folders,
    List<ArticleResponse> Articles);

public record TreeChildrenResponse(
    string Path,
    List<FolderInfoResponse> Folders,
    List<ArticleResponse> Articles,
    bool IsReadOnly = false,
    bool IsSystem = false);

public record MoveArticleResponse(Guid Id, string NewPath);

public record FolderCreateResult(Guid Id, string Path, string Name);

public record FolderRenameResult(string OldPath, string NewPath, int ArticlesMoved);

public record FolderDeleteResult(string Path, int ArticlesDeleted);

public record FolderMoveResult(string OldPath, string NewPath, int ArticlesMoved);

public record SnapshotInfo(string FileName, long SizeBytes, DateTime CreatedAt, long? CpSequenceNum = null, Guid? ProducerNodeId = null, bool Signed = false);

public record ActivityItem(
    string EventType,
    Guid? ArticleId,
    string? ArticleTitle,
    string? TreePath,
    DateTime Timestamp,
    Guid NodeId,
    string? NodeName,
    string? ActorType,
    string? ActorName,
    string? ViaAgentName);

public record CommentResponse(int Id, Guid ArticleId, string Text, DateTime CreatedAt);

public record ActivityResponse(
    List<ActivityItem> Items,
    int Total,
    int Offset,
    int Limit);

public record JoinResponse(JoinRemoteIdentity RemoteNode, JoinKeySlot KeySlot, List<JoinWhitelistEntry> Whitelist);

public record JoinRemoteIdentity(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion);

public record JoinWhitelistEntry(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress, bool IsSuperadmin = false);

public record JoinKeySlot(
    string EncryptedMasterDekB64,
    string IvB64,
    string SaltB64,
    int ArgonMemory,
    int ArgonIterations,
    int ArgonParallelism);

public record LoginResponse(int UserId, string Username, string DisplayName, string Role, bool IsUnlocked, string? MigratedSyntheticUsername = null, string? SecurityStamp = null);

public record UserListItemResponse(int Id, string Username, string DisplayName, string Role, DateTime CreatedAt, DateTime? LastLoginAt, bool ChatAccess);

/// <param name="BasePolicy">What "this role has no allow rows" means — "open" (whole vault minus
/// deny rows) or "closed" (nothing). See BeeMemoryBank.Core.Models.RoleBasePolicy.</param>
/// <param name="UserCount">Active users holding this role. 0 on single-role reads, which do not
/// run the count query.</param>
/// <param name="RuleCount">Folder rules attached to this role. 0 on single-role reads.</param>
public record RoleResponse(
    string Name, string DisplayName, string? Description, bool IsSystem, string BasePolicy,
    int UserCount, int RuleCount, DateTime CreatedAt, DateTime UpdatedAt);

public record WhitelistEntryResponse(
    Guid NodeId,
    string DisplayName,
    string Ed25519PublicKeyB64,
    string? ApiAddress,
    bool CanGenerateEmbeddings,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AutoAcceptRestore,
    bool AutoAcceptDekRotation)
{
    public static WhitelistEntryResponse From(BeeMemoryBank.Core.Models.WhitelistEntry e) => new(
        e.NodeId,
        e.DisplayName,
        Convert.ToBase64String(e.Ed25519PublicKey),
        e.ApiAddress,
        e.CanGenerateEmbeddings,
        e.Status,
        e.CreatedAt,
        e.UpdatedAt,
        e.AutoAcceptRestore,
        e.AutoAcceptDekRotation);
}

public enum RestoreFlowStep
{
    Idle,
    SessionsClosing,
    PreRestoreBackup,
    DownloadingSnapshot,
    ApplyingSnapshot,
    ResettingSyncState,
    UpdatingReplayShield,
    Finalizing,
    Completed,
    Failed,
    NeedsAdminDecision
}

public record RestoreProgressResponse(
    Guid? EventId,
    RestoreFlowStep CurrentStep,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage,
    bool RequiresMasterPassword
);

public record SnapshotUploadResponse(
    Guid FileId,
    string FileName,
    long FileSizeBytes,
    string OriginatorNodeId,
    string SnapshotHash,
    string CreatedAt,
    bool NetworkRestoreAllowed,
    string? DekMismatchReason
);

public record DekRotationProgressResponse(
    Guid? EventId,
    DekRotationFlowStep CurrentStep,
    int PercentageComplete,
    string? StatusMessage,
    string? ErrorMessage
);

public record DekRotationInitiationResponse(
    Guid ProposedEventId,
    string Message
);

// ─── Reachability self-test: probe (superplan §5 Ярус 2, Этап 5) ──────────

/// <summary>
/// Request body for <c>POST /api/sync/probe</c> — the candidate public URL the user wants
/// to verify is reachable from outside their LAN. Originates from the local Web UI wizard
/// (gated by <c>RequireInternalKey</c>), not from a peer.
/// </summary>
public record SyncProbeRequest(string Url);

/// <summary>
/// Request body for <c>POST /api/sync/probe-relay</c> — sent peer-to-peer by the probing
/// node to one of its whitelisted peers, asking that peer to fetch the target URL's
/// <c>/api/sync/ping</c> and report whether it got a response.
/// </summary>
public record SyncProbeRelayRequest(string Url);

/// <summary>
/// Outcome categories for a probe. Drives the wizard UI's branching messages
/// (CGNAT hint, success, no-peers, etc.) without hardcoding UI text here.
/// </summary>
public enum SyncProbeOutcome
{
    /// <summary>A peer confirmed the target URL responded — port forwarding works.</summary>
    Reachable,

    /// <summary>
    /// A peer tried but got NO response at all (connection refused / timeout / DNS failure).
    /// This is the signal that lets a later wizard suggest a CGNAT diagnosis.
    /// </summary>
    Unreachable,

    /// <summary>No active whitelisted peers with a reachable <c>ApiAddress</c> exist to relay through.</summary>
    NoPeersAvailable,

    /// <summary>The selected peer(s) themselves couldn't be reached (offline/unreachable).</summary>
    PeerUnreachable,

    /// <summary>Authentication to the selected peer(s) failed.</summary>
    PeerAuthFailed,

    /// <summary>The supplied URL was invalid.</summary>
    InvalidUrl,
}

/// <summary>
/// Granular error category when the target was unreachable, so the wizard can distinguish
/// "nothing came back at all" (CGNAT / stealth-drop candidate) from other failures.
/// </summary>
public enum SyncProbeErrorCategory
{
    /// <summary>No error (target was reachable).</summary>
    None,

    /// <summary>TCP connection refused — port closed / not forwarded.</summary>
    ConnectionRefused,

    /// <summary>Connection timed out — packet dropped silently (firewall stealth / CGNAT).</summary>
    Timeout,

    /// <summary>DNS resolution failed — hostname wrong or not resolvable.</summary>
    DnsFailure,

    /// <summary>TLS handshake failed — cert problem, not a reachability issue.</summary>
    TlsError,

    /// <summary>An unexpected error occurred.</summary>
    Unknown,
}

/// <summary>
/// Response from <c>POST /api/sync/probe</c>. Carries enough detail for a later wizard UI
/// to show a sensible, honest message — including the CGNAT-specific hint when the target
/// was completely unreachable (i.e. <see cref="Outcome"/> is <see cref="SyncProbeOutcome.Unreachable"/>
/// with an error category indicating no response at all).
/// </summary>
public record SyncProbeResponse(
    SyncProbeOutcome Outcome,
    Guid? PeerNodeId,
    string? PeerDisplayName,
    int? TargetHttpStatusCode,
    SyncProbeErrorCategory ErrorCategory,
    string? Message);

/// <summary>
/// Response from <c>POST /api/sync/probe-relay</c> (peer-to-peer). Reports whether the
/// relay peer got any HTTP response from the target URL. ANY HTTP status (even 401/403/503)
/// counts as reachable — it proves the server is listening and the port forward works.
/// </summary>
public record SyncProbeRelayResponse(
    bool Reachable,
    int? HttpStatusCode,
    SyncProbeErrorCategory ErrorCategory,
    string? ErrorDetail);

/// <summary>Current product name plus whether it is a node override or the built-in default.</summary>
public record BrandingResponse(string Name, bool IsCustom, string DefaultName);

/// <summary>
/// One starred article as the sidebar renders it. Ordered by the endpoint, never by the client:
/// alphabetical until the user moves something, then by their manual order.
/// </summary>
public record FavoriteItem(Guid Id, string Title, string TreePath, bool Protected);

/// <summary>
/// <paramref name="ManualOrder"/> tells the UI whether a "back to A-Z" action makes sense
/// (false = already alphabetical).
/// </summary>
public record FavoriteListResponse(List<FavoriteItem> Items, bool ManualOrder);

