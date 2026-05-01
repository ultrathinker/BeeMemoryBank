namespace BeeMemoryBank.Api.Models;

public record UnlockRequest(string Password);

public record CreateArticleRequest(
    string Title,
    string TreePath,
    string Content,
    List<string>? ConceptTags = null);

public record UpdateArticleRequest(
    string? Title = null,
    string? TreePath = null,
    List<string>? ConceptTags = null,
    string? Content = null);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

public record UpdateWhitelistEntryRequest(
    string? DisplayName = null,
    string? ApiAddress = null,
    bool? CanGenerateEmbeddings = null);

public record ChangeNodeAddressRequest(string NewApiAddress, string Password);

public record SemanticSearchRequest(string Query, int TopK = 10);

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

public record CreateUserRequest(string Username, string DisplayName, string Password, string Role);

public record UpdateUserRequest(string DisplayName, string? Role = null, string? Password = null);

public record ChangeUserPasswordRequest(string NewPassword);

public record AddAclEntryRequest(Guid FolderId, string Effect);

public record RestoreSnapshotRequest(string FileName, string MasterPassword, bool CreateBackupFirst = true, bool StandaloneMode = false);

public record InitStandaloneRequest(string AdminUsername, string DisplayName, string Password);

public record InitJoinRequest(string AdminUsername, string DisplayName, string RemoteUrl, string Password);

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
    string? ForeignMasterPassword  // только для standalone из foreign network
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
