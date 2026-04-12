using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync;

public static class EventTypes
{
    public const string ArticleCreate = "article_create";
    public const string ArticleUpdate = "article_update";
    public const string ArticleDelete = "article_delete";
    public const string WhitelistAdd = "whitelist_add";
    public const string WhitelistRevoke = "whitelist_revoke";
    public const string WhitelistUpdate = "whitelist_update";
    public const string CommentCreate = "comment_create";
    public const string CommentDelete = "comment_delete";
    public const string FolderCreate = "folder_create";
    public const string FolderRename = "folder_rename";
    public const string FolderDelete = "folder_delete";
    public const string MediaCreate = "media_create";
    public const string MediaDelete = "media_delete";
}

/// <summary>Payload for creating and updating an article.</summary>
public record ArticleEventPayload(
    [property: JsonPropertyName("title")]         string Title,
    [property: JsonPropertyName("tree_path")]     string TreePath,
    [property: JsonPropertyName("tags")]          string[] Tags,
    [property: JsonPropertyName("ciphertext")]    string CiphertextB64,
    [property: JsonPropertyName("iv")]            string IvB64,
    [property: JsonPropertyName("encrypted_dek")] string EncryptedDekB64,
    [property: JsonPropertyName("dek_iv")]        string DekIvB64,
    [property: JsonPropertyName("status")]        string Status,
    [property: JsonPropertyName("created_at")]    DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")]    DateTime UpdatedAt,
    // Reserved for future master DEK rotation (MASTER_DEK_ROTATION_DESIGN.md).
    // Always 1 until rotation is implemented.
    [property: JsonPropertyName("dek_epoch")]     int DekEpoch = 1
);

/// <summary>Payload for soft-deleting an article.</summary>
public record ArticleDeletePayload(
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt
);

/// <summary>Payload for adding a node to the whitelist.</summary>
public record WhitelistAddPayload(
    [property: JsonPropertyName("node_id")]          Guid NodeId,
    [property: JsonPropertyName("display_name")]     string DisplayName,
    [property: JsonPropertyName("public_key")]       string PublicKeyB64,
    [property: JsonPropertyName("api_address")]      string? ApiAddress,
    [property: JsonPropertyName("can_generate_embeddings")] bool CanGenerateEmbeddings
);

/// <summary>Payload for revoking a node from the whitelist.</summary>
public record WhitelistRevokePayload(
    [property: JsonPropertyName("node_id")] Guid NodeId
);

/// <summary>Payload for updating a node in the whitelist (e.g. URL change).</summary>
public record WhitelistUpdatePayload(
    [property: JsonPropertyName("node_id")]      Guid NodeId,
    [property: JsonPropertyName("api_address")]  string? ApiAddress,
    [property: JsonPropertyName("display_name")] string? DisplayName
);

/// <summary>Payload for comment creation (supports both plaintext and encrypted).</summary>
public record CommentEventPayload(
    [property: JsonPropertyName("comment_id")]     Guid CommentId,
    [property: JsonPropertyName("article_id")]     Guid ArticleId,
    [property: JsonPropertyName("text")]           string Text,
    [property: JsonPropertyName("created_at")]     DateTime CreatedAt,
    [property: JsonPropertyName("ciphertext_b64")] string? CiphertextB64 = null,
    [property: JsonPropertyName("iv_b64")]         string? IvB64 = null,
    [property: JsonPropertyName("encrypted")]      bool Encrypted = false
);

/// <summary>Payload for deleting a comment.</summary>
public record CommentDeletePayload(
    [property: JsonPropertyName("comment_id")] Guid CommentId
);

/// <summary>Payload for creating a folder.</summary>
public record FolderCreatePayload(
    [property: JsonPropertyName("folder_id")]   Guid    FolderId,
    [property: JsonPropertyName("path")]        string  Path,
    [property: JsonPropertyName("name")]        string  Name,
    [property: JsonPropertyName("parent_path")] string? ParentPath,
    [property: JsonPropertyName("created_at")]  DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")]  DateTime UpdatedAt
);

/// <summary>Payload for renaming a folder.</summary>
public record FolderRenamePayload(
    [property: JsonPropertyName("folder_id")]       Guid    FolderId,
    [property: JsonPropertyName("old_path")]        string  OldPath,
    [property: JsonPropertyName("new_path")]        string  NewPath,
    [property: JsonPropertyName("new_name")]        string  NewName,
    [property: JsonPropertyName("new_parent_path")] string? NewParentPath,
    [property: JsonPropertyName("updated_at")]      DateTime UpdatedAt
);

/// <summary>Payload for deleting a folder.</summary>
public record FolderDeletePayload(
    [property: JsonPropertyName("folder_id")]  Guid     FolderId,
    [property: JsonPropertyName("path")]       string   Path,
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt
);

public record MediaEventPayload(
    [property: JsonPropertyName("media_id")]        Guid MediaId,
    [property: JsonPropertyName("article_id")]      Guid? ArticleId,
    [property: JsonPropertyName("file_name")]       string FileName,
    [property: JsonPropertyName("content_type")]    string ContentType,
    [property: JsonPropertyName("file_size")]       long FileSize,
    [property: JsonPropertyName("ciphertext")]      string CiphertextB64,
    [property: JsonPropertyName("iv")]              string IvB64,
    [property: JsonPropertyName("encrypted_dek")]   string EncryptedDekB64,
    [property: JsonPropertyName("dek_iv")]          string DekIvB64,
    [property: JsonPropertyName("created_at")]      DateTime CreatedAt);

public record MediaDeletePayload(
    [property: JsonPropertyName("media_id")]   Guid MediaId,
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt);
