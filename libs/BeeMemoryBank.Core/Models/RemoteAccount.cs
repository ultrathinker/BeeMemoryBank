namespace BeeMemoryBank.Core.Models;

/// <summary>
/// A configured pointer to another BMB node from which this node mirrors
/// folders read-only. Credentials are stored as a long-lived bearer token,
/// wrapped with this node's master DEK.
/// </summary>
public class RemoteAccount
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string RemoteUsername { get; set; } = "";
    public byte[] EncryptedToken { get; set; } = [];
    public byte[] TokenIv { get; set; } = [];
    public DateTime? TokenExpiresAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single mirrored folder under a remote account. Subscription is per-device:
/// the same Remote Account on this user's laptop and phone independently picks
/// which folders to mirror.
/// </summary>
public class RemoteSubscription
{
    public Guid Id { get; set; }
    public Guid RemoteAccountId { get; set; }
    public string RemoteFolderId { get; set; } = "";    // GUID on owner-node
    public string RemoteFolderPath { get; set; } = "";   // path on owner-node for UI
    public string MountPath { get; set; } = "";          // local mount point path
    public string? SyncCursor { get; set; }
    public DateTime? LastFullSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Owner-side: a long-lived bearer token a remote user can use to read
/// folders they have ACL access to. Token plaintext is hashed (SHA-256)
/// before storage; the original is shown once at issuance.
/// </summary>
public class RemoteApiToken
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = "";   // SHA-256 hex
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
