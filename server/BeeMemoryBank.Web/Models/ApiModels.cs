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
    int RelatedStrength = 0);

public record ArticleContentDto(Guid Id, string Content);

public record SessionStatusDto(bool IsUnlocked);

public record FolderInfoDto(Guid Id, string Path, string Name, int ArticleCount, DateTime CreatedAt, DateTime UpdatedAt);

public record TreeChildrenDto(
    string Path,
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles);

public record SearchResponseDto(
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles);

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
    bool AutoAcceptDekRotation = false);

public record NodeIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

public record AgentDto(int Id, string Name, string? Description, string KeyPrefix, DateTime CreatedAt, DateTime? LastAccessedAt, long RequestCount, int OwnerUserId = 0, string? OwnerName = null);

public record AgentCreatedDto(int Id, string Name, string ApiKey);

public record ErrorDto(string Error);

public record LoginResult(
    bool Success,
    string? Error,
    bool IsLocked,
    string? Username,
    string? DisplayName,
    string? Role,
    string? UserId,
    string? MigratedSyntheticUsername);

public record LoginResponse(int UserId, string Username, string DisplayName, string Role, bool IsUnlocked, string? MigratedSyntheticUsername = null);

public record UserDto(int Id, string Username, string DisplayName, string Role, DateTime CreatedAt, DateTime? LastLoginAt);

public record AclEntryDto(int Id, Guid FolderId, string FolderPath, string Effect, DateTime CreatedAt);

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

public record MediaDto(Guid Id, string FileName, string ContentType, long FileSize);

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
