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
    public const string ConceptTagRename = "concept_tag_rename";
    public const string ConceptTagMerge = "concept_tag_merge";
    public const string ConceptTagDelete = "concept_tag_delete";
    public const string MediaLink = "media_link";
    public const string HardDelete = "hard_delete";
    public const string SnapshotCheckpoint = "snapshot_checkpoint";
    public const string RestoreNetwork = "restore_network";
    public const string DekRotationProposed = "dek_rotation_proposed";
    public const string DekRotationCommit = "dek_rotation_commit";
}

/// <summary>Payload for network-wide snapshot restore feature.</summary>
public record RestoreNetworkEventPayload(
    [property: JsonPropertyName("snapshot_hash")]    string SnapshotHash,
    [property: JsonPropertyName("restore_point_ts")] string RestorePointTs,
    [property: JsonPropertyName("file_size_bytes")]  long FileSizeBytes,
    [property: JsonPropertyName("expires_at")]       string ExpiresAt,
    [property: JsonPropertyName("source_url")]       string SourceUrl,
    [property: JsonPropertyName("filter_secrets")]   bool FilterSecrets
);

/// <summary>
/// Phase 1 of DEK rotation: initiator broadcasts a proposal. Other peers record it as Pending
/// in tbl_dek_rotation_state but do NOT start rotating yet — they wait for a matching
/// DekRotationCommit. This split closes the cross-node split-brain window where two concurrent
/// rotates would both go destructive before noticing each other.
/// </summary>
public record DekRotationProposedPayload(
    [property: JsonPropertyName("encrypted_new_dek")] string EncryptedNewDek,
    [property: JsonPropertyName("iv")]                string Iv,
    [property: JsonPropertyName("new_dek_epoch")]     int NewDekEpoch,
    [property: JsonPropertyName("rotation_ts")]       string RotationTs,
    [property: JsonPropertyName("expires_at")]        string ExpiresAt,
    [property: JsonPropertyName("originator_node_id")] string OriginatorNodeId
);

/// <summary>
/// Phase 2 of DEK rotation: initiator confirms the proposal won the cross-node tiebreaker
/// and wants peers to actually apply the rotation. Carries a reference back to the matching
/// proposed event so receivers can match COMMIT with PROPOSED.
/// </summary>
public record DekRotationCommitPayload(
    [property: JsonPropertyName("proposed_event_id")] string ProposedEventId,
    [property: JsonPropertyName("encrypted_new_dek")] string EncryptedNewDek,
    [property: JsonPropertyName("iv")]                string Iv,
    [property: JsonPropertyName("new_dek_epoch")]     int NewDekEpoch,
    [property: JsonPropertyName("rotation_ts")]       string RotationTs,
    [property: JsonPropertyName("originator_node_id")] string OriginatorNodeId
);

/// <summary>Payload for physical/hard deletion of articles or folders.</summary>
public record HardDeleteEventPayload(
    [property: JsonPropertyName("entity_type")]       string EntityType,
    [property: JsonPropertyName("entity_identifier")] string EntityIdentifier,
    [property: JsonPropertyName("deleted_at")]        DateTime DeletedAt
);

/// <summary>Payload for creating and updating an article.</summary>
public record ArticleEventPayload(
    [property: JsonPropertyName("title")]         string Title,
    [property: JsonPropertyName("tree_path")]     string TreePath,
    [property: JsonPropertyName("concept_tags")]  string[]? ConceptTags,
    [property: JsonPropertyName("ciphertext")]    string CiphertextB64,
    [property: JsonPropertyName("iv")]            string IvB64,
    [property: JsonPropertyName("encrypted_dek")] string EncryptedDekB64,
    [property: JsonPropertyName("dek_iv")]        string DekIvB64,
    [property: JsonPropertyName("status")]        string Status,
    [property: JsonPropertyName("created_at")]    DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")]    DateTime UpdatedAt,
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
    [property: JsonPropertyName("can_generate_embeddings")] bool CanGenerateEmbeddings,
    // Default false for forward-compat: pre-2026-05-01 senders omit the
    // field entirely and JSON deserialization fills false. Without this
    // field a 3+ node cluster lost the IsSuperadmin bit on every sync —
    // the receiving node would create the entry as non-superadmin and
    // then reject the new peer's hard_delete / restore_network /
    // whitelist_add events forever (cluster split-brain).
    [property: JsonPropertyName("is_superadmin")]    bool IsSuperadmin = false
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
    [property: JsonPropertyName("encrypted")]      bool Encrypted = false,
    [property: JsonPropertyName("dek_epoch")]      int DekEpoch = 1
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
    [property: JsonPropertyName("created_at")]      DateTime CreatedAt,
    [property: JsonPropertyName("dek_epoch")]       int DekEpoch = 1);

public record MediaDeletePayload(
    [property: JsonPropertyName("media_id")]   Guid MediaId,
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt);

public record ConceptTagRenamePayload(
    [property: JsonPropertyName("old_name")] string OldName,
    [property: JsonPropertyName("new_name")] string NewName
);

public record ConceptTagMergePayload(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target
);

public record ConceptTagDeletePayload(
    [property: JsonPropertyName("name")] string Name
);

public record MediaLinkEventPayload(
    [property: JsonPropertyName("media_id")] Guid MediaId,
    [property: JsonPropertyName("article_id")] Guid ArticleId
);

public record SnapshotCheckpointPayload(
    [property: JsonPropertyName("cp_seq")]                 long CpSeq,
    [property: JsonPropertyName("events_removed")]         int EventsRemoved,
    [property: JsonPropertyName("snapshot_file_name")]     string SnapshotFileName,
    [property: JsonPropertyName("snapshot_sha256")]        string SnapshotSha256,
    [property: JsonPropertyName("prev_checkpoint_sha256")] string? PrevCheckpointSha256,
    [property: JsonPropertyName("produced_at")]            DateTime ProducedAt
);
