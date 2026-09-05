namespace BeeMemoryBank.Web.Models;

// Responses from the internal API

public record ArticleDto(
    Guid Id,
    string Title,
    string TreePath,
    bool EmbeddingPending,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<string>? ConceptTags = null,
    int RelatedCount = 0,
    int RelatedStrength = 0,
    bool Protected = false,
    string? ProtectionHint = null);

public record ArticleContentDto(Guid Id, string Content);

public record EditContentDto(Guid Id, bool Protected, bool Unlocked, string? Content);

public record SessionStatusDto(bool IsUnlocked);

public record SessionSettingsDto(int ExpireHours, bool SlidingExpiration);

public record FolderInfoDto(Guid Id, string Path, string Name, int ArticleCount, DateTime CreatedAt, DateTime UpdatedAt, bool IsSystem = false);

public record TreeChildrenDto(
    string Path,
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles,
    bool IsReadOnly = false,
    bool IsSystem = false);

public record FolderPermissionsDto(string Path, bool CanRead, bool CanWrite, bool IsReadOnly);

public record ReadOnlyPathsDto(string[] Paths);

public record SearchResponseDto(
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles,
    int Page = 1,
    int PageSize = 0,
    int Total = 0,
    bool HasMore = false);

public record ConceptTagDto(string Name, int ArticleCount);

public record ConceptGraphEdgeDto(string Source, string Target, int Weight);

public record RelatedArticleDto(
    Guid Id,
    string Title,
    string TreePath,
    List<string> SharedConcepts,
    int Strength);

public record SnapshotDto(Guid? FileId, string FileName, long SizeBytes, DateTime CreatedAt);

public record ActivityItemDto(
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

public record ActivityResponseDto(
    List<ActivityItemDto> Items,
    int Total,
    int Offset,
    int Limit);

public record CommentDto(int Id, Guid ArticleId, string Text, DateTime CreatedAt);

public record WhitelistEntryDto(
    Guid NodeId,
    string DisplayName,
    string Ed25519PublicKeyB64,
    string? ApiAddress,
    bool CanGenerateEmbeddings,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AutoAcceptRestore = false,
    bool AutoAcceptDekRotation = false,
    bool IsSuperadmin = false);

/// <summary>Whether another node changed the master password while this one kept the old slot.</summary>
public record MasterPasswordNoticeDto(bool IsStale, DateTime? ChangedAt, string? ChangedByNode);

/// <summary>Reply from POST /api/keys/change-password. <c>Message</c> is operator-facing prose
/// naming how many peers are still on the old password; it is shown verbatim.</summary>
public record ChangeMasterPasswordDto(int PeerCount, string Message);

public record NodeIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

// CanAutoUnlock: true only for an agent owned by a superadmin (see AGENTS.md H6 fix). Such a key
// can wake a locked node by itself; an ordinary user's agent cannot -- it only ever works while
// someone else has already unlocked the vault. Rendered in Admin/Profile so this is visible.
public record AgentDto(int Id, string Name, string? Description, string KeyPrefix, DateTime CreatedAt, DateTime? LastAccessedAt, long RequestCount, int OwnerUserId = 0, string? OwnerName = null, bool CanAutoUnlock = false);

public record AgentCreatedDto(int Id, string Name, string ApiKey, bool CanAutoUnlock = false);

// GET /api/session/lock-impact — what can put the master DEK back after a Lock. Agents are the
// superadmin-owned keys that re-unlock the process on their next request; OsAutoUnlockEnabled is
// the separate startup-only mechanism, which undoes a restart rather than a Lock.
public record LockImpactDto(
    List<AutoUnlockAgentDto> Agents,
    bool OsAutoUnlockEnabled,
    bool OsAutoUnlockSupported);

public record AutoUnlockAgentDto(int Id, string Name, int OwnerUserId, string? OwnerName);

public record ErrorDto(string Error);

public record LoginResult(
    bool Success,
    string? Error,
    bool IsLocked,
    string? Username,
    string? DisplayName,
    string? Role,
    string? UserId,
    string? MigratedSyntheticUsername,
    string? SecurityStamp);

public record LoginResponse(int UserId, string Username, string DisplayName, string Role, bool IsUnlocked, string? MigratedSyntheticUsername = null, string? SecurityStamp = null);

public record UserDto(int Id, string Username, string DisplayName, string Role, DateTime CreatedAt, DateTime? LastLoginAt, bool ChatAccess);

public record AclEntryDto(int Id, Guid FolderId, string FolderPath, string Effect, DateTime CreatedAt, bool IsReadOnly = false);

public record RoleDto(
    string Name, string DisplayName, string? Description, bool IsSystem, string BasePolicy,
    int UserCount, int RuleCount, DateTime CreatedAt, DateTime UpdatedAt);

// No Id: role rules are keyed by (role, folder, effect), not a surrogate row id.
public record RoleAclEntryDto(
    string RoleName, Guid FolderId, string FolderPath, string Effect, bool IsReadOnly, DateTime CreatedAt);

public record CreateRoleProxyRequest(string Name, string DisplayName, string? Description, string BasePolicy);

public record UpdateRoleProxyRequest(string DisplayName, string? Description, string BasePolicy);

public record ArticleVersionDto(
    Guid Id,
    int VersionNumber,
    string Title,
    string TreePath,
    string? UpdatedBy,
    DateTime CreatedAt);

public record ArticleVersionContentDto(
    Guid Id,
    int VersionNumber,
    string Title,
    string TreePath,
    string Content,
    DateTime CreatedAt);

public record MediaDto(Guid Id, string FileName, string ContentType, long FileSize, string Kind = "image", DateTime? CreatedAt = null);

public record MediaDownloadResult { public byte[] Data { get; init; } = []; public string ContentType { get; init; } = "application/octet-stream"; public string FileName { get; init; } = ""; }

public record InitStatusDto(bool Initialized);

public record CompactionPreviewDto(
    long HeadSeq, long MinSeq, int TotalEvents, int ActivePeerCount,
    long ProposedCp, bool CanCompact, string Reason,
    List<string> Warnings, List<PeerPositionDto> PeerPositions,
    int EventsToDelete, int EventsRemaining);

public record PeerPositionDto(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt);
public record CompactionResultDto(long CpAfter, int EventsDeleted, string SnapshotFileName);

public record SnapshotCheckpointDto(
    long SequenceNum, Guid NodeId, DateTime CreatedAt,
    System.Text.Json.JsonElement Payload);

public record PeerPendingDekRotationDto(
    string EventId,
    string OriginatorNodeId,
    string OriginatorDisplayName,
    string RotationTs);

// ─── Favorites / branding ────────────────────────────────────────────────────

public record FavoriteItemDto(Guid Id, string Title, string TreePath, bool Protected);

/// <summary><c>ManualOrder</c> false means the list is in automatic alphabetical order.</summary>
public record FavoriteListDto(List<FavoriteItemDto> Items, bool ManualOrder);

/// <summary><c>IsCustom</c> false means <c>Name</c> is the built-in default, not a node override.</summary>
public record BrandingDto(string Name, bool IsCustom, string DefaultName);

