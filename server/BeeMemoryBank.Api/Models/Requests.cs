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
    string? Hint = null);

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

public record SemanticSearchRequest(string Query, int TopK = 10);

/// <summary>
/// WP-16: <c>Mode</c> is a string (not the <c>SearchMode</c> enum directly) so an unrecognized value
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

/// <summary>Null or blank clears the override and restores the built-in product name.</summary>
public record BrandingRequest(string? Name);

/// <summary>Direction for a one-step favorite move: "up" or "down".</summary>
public record FavoriteMoveRequest(string Direction);

