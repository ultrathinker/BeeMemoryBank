namespace BeeMemoryBank.Web.Models;

// Responses from the internal API

public record ArticleDto(
    Guid Id,
    string Title,
    string TreePath,
    List<string> Tags,
    bool EmbeddingPending,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ArticleContentDto(Guid Id, string Content);

public record SessionStatusDto(bool IsUnlocked);

public record FolderInfoDto(Guid Id, string Path, string Name, int ArticleCount);

public record TreeChildrenDto(
    string Path,
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles);

public record SearchResponseDto(
    List<FolderInfoDto> Folders,
    List<ArticleDto> Articles);

public record TagDto(
string Name, int ArticleCount);

public record SnapshotDto(string FileName, long SizeBytes, DateTime CreatedAt);

public record ActivityItemDto(
    string EventType,
    Guid? ArticleId,
    string? ArticleTitle,
    string? TreePath,
    DateTime Timestamp,
    Guid NodeId,
    string? NodeName,
    string? ActorType,
    string? ActorName);

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
    DateTime UpdatedAt);

public record NodeIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

public record AgentDto(int Id, string Name, string? Description, string KeyPrefix, DateTime CreatedAt, DateTime? LastAccessedAt, long RequestCount);

public record AgentCreatedDto(int Id, string Name, string ApiKey);

public record ErrorDto(string Error);

public record LoginResult(
    bool Success,
    string? Error,
    bool IsLocked,
    string? Username,
    string? DisplayName,
    string? Role,
    string? UserId);

public record LoginResponse(int UserId, string Username, string DisplayName, string Role, bool IsUnlocked);

public record UserDto(int Id, string Username, string DisplayName, string Role, DateTime CreatedAt, DateTime? LastLoginAt);

public record RestrictionInfoDto(int Id, Guid FolderId, string FolderPath, DateTime CreatedAt);

public record ArticleVersionDto(
    Guid Id,
    int VersionNumber,
    string Title,
    List<string> Tags,
    string TreePath,
    string? UpdatedBy,
    DateTime CreatedAt);

public record ArticleVersionContentDto(
    Guid Id,
    int VersionNumber,
    string Title,
    List<string> Tags,
    string TreePath,
    string Content,
    DateTime CreatedAt);

public record MediaDto(Guid Id, string FileName, string ContentType, long FileSize);

public record MediaDownloadResult { public byte[] Data { get; init; } = []; public string ContentType { get; init; } = "application/octet-stream"; public string FileName { get; init; } = ""; }
