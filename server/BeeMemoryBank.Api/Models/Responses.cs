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
    DateTime UpdatedAt)
{
    public static ArticleResponse From(BeeMemoryBank.Core.Models.Article a, List<string> conceptTags) => new(
        a.Id, a.Title, a.TreePath, conceptTags, a.EmbeddingPending, a.Status, a.CreatedAt, a.UpdatedAt);
}

public record ArticleContentResponse(Guid Id, string Content);

public record SessionStatusResponse(bool IsUnlocked);

public record UnlockResponse(bool IsUnlocked, string? MigratedSyntheticUsername);

public record RecoveryKeyResponse(string RecoveryKey);

public record ErrorResponse(string Error);

public record FolderInfoResponse(Guid Id, string Path, string Name, int ArticleCount, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static FolderInfoResponse From(BeeMemoryBank.Core.Models.Folder f, int articleCount = 0) =>
        new(f.Id, f.Path, f.Name, articleCount, f.CreatedAt, f.UpdatedAt);
}

public record SearchResponse(
    List<FolderInfoResponse> Folders,
    List<ArticleResponse> Articles);

public record TreeChildrenResponse(
    string Path,
    List<FolderInfoResponse> Folders,
    List<ArticleResponse> Articles);

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

public record JoinRemoteIdentity(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

public record JoinWhitelistEntry(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress, bool IsSuperadmin = false);

public record JoinKeySlot(
    string EncryptedMasterDekB64,
    string IvB64,
    string SaltB64,
    int ArgonMemory,
    int ArgonIterations,
    int ArgonParallelism);

public record LoginResponse(int UserId, string Username, string DisplayName, string Role, bool IsUnlocked, string? MigratedSyntheticUsername = null);

public record UserListItemResponse(int Id, string Username, string DisplayName, string Role, DateTime CreatedAt, DateTime? LastLoginAt);

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

public enum DekRotationFlowStep
{
    Idle,
    Proposing,
    AwaitingQuorum,
    Committing,
    SessionsClosing,
    PreRotationBackup,
    ReWrappingPerItem,
    ReEncryptingDirect,
    InvalidatingSlots,
    InvalidatingAgents,
    Finalizing,
    Completed,
    Failed,
    NeedsAdminDecision,
    NeedsNewRecoveryKey
}

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
