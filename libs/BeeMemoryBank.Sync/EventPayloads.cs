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
    public const string MasterPasswordChanged = "master_password_changed";
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

/// <summary>
/// Payload for creating and updating an article.
///
/// The body travels by reference: <see cref="CiphertextSha256"/> names a blob in tbl_blob that the
/// pusher ships ahead of the event (or the puller fetches before applying). The hash sits inside
/// the Ed25519-signed payload, and the receiver stores incoming bytes only under what they hash to,
/// so the signature still binds the body even though the body is no longer in the signed bytes.
/// <see cref="CiphertextB64"/> is the pre-protocol-2 form — the whole body inline as base64 — and
/// is still accepted on read because the event log holds such events and a peer that has not
/// upgraded yet still emits them. Exactly one of the two is set; the applier prefers the inline
/// form when both are present, since that is what the signature covers directly.
/// </summary>
public record ArticleEventPayload(
    [property: JsonPropertyName("title")]         string Title,
    [property: JsonPropertyName("tree_path")]     string TreePath,
    [property: JsonPropertyName("concept_tags")]  string[]? ConceptTags,
    [property: JsonPropertyName("ciphertext")]    string? CiphertextB64,
    [property: JsonPropertyName("iv")]            string IvB64,
    [property: JsonPropertyName("encrypted_dek")] string EncryptedDekB64,
    [property: JsonPropertyName("dek_iv")]        string DekIvB64,
    [property: JsonPropertyName("status")]        string Status,
    [property: JsonPropertyName("created_at")]    DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")]    DateTime UpdatedAt,
    // Which master-DEK generation the wrapped DEK above belongs to. Defaulted rather than
    // required, because a pre-2026-09 sender omits it and its articles really are from epoch 1 —
    // that release hardcoded the literal 1 here regardless of the sender's actual epoch, so the
    // field was present and always wrong. Reading it is only safe on events new enough to have
    // meant it.
    [property: JsonPropertyName("dek_epoch")]     int DekEpoch = 1,
    // Forward-compat: second-layer "protected" article metadata. The body ciphertext already
    // carries the BMBENC1 blob verbatim; these are the plaintext metadata a receiver needs to
    // render the lock badge/hint without decrypting.
    // CRITICAL: Protected is bool? (NOT bool=false). A pre-2026-06 sender omits the JSON property,
    // which must deserialize to null = "I don't know about this flag" — NOT false. If it defaulted
    // to false, an old node editing a protected article's title would ship protected=false and the
    // receiver would strip the lock from a body that is still a BMBENC1 ciphertext, exposing it to
    // accidental overwrite. EventApplier therefore only writes the flag when it HasValue.
    [property: JsonPropertyName("protected")]       bool? Protected = null,
    [property: JsonPropertyName("protection_hint")] string? ProtectionHint = null,
    [property: JsonPropertyName("ciphertext_sha256")] string? CiphertextSha256 = null
);

/// <summary>Payload for soft-deleting an article.</summary>
public record ArticleDeletePayload(
    [property: JsonPropertyName("deleted_at")] DateTime DeletedAt
);

/// <summary>
/// Announces that the master password was changed on the originating node — and carries NOTHING
/// else, deliberately.
///
/// <para>Key slots are node-local, so this event cannot rewrap anything on the receiver: doing that
/// would need a slot wrapped under a KEK derived from the new password, i.e. key material on the
/// wire. The choice was made the other way round. What travels is the fact and the time, and an
/// admin then enters the new password on each node by hand — the only way a local slot gets
/// rewrapped without the password ever leaving the machine it was typed on.</para>
///
/// <para>Until they do, that node still accepts the old password, including at its own /api/join.
/// That is exactly why silence was the wrong default.</para>
/// </summary>
public record MasterPasswordChangedPayload(
    [property: JsonPropertyName("changed_at")]   DateTime ChangedAt,
    [property: JsonPropertyName("node_name")]    string NodeName
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

/// <summary>Payload for updating a node in the whitelist (e.g. URL change, demotion).</summary>
public record WhitelistUpdatePayload(
    [property: JsonPropertyName("node_id")]      Guid NodeId,
    [property: JsonPropertyName("api_address")]  string? ApiAddress,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    // NULLABLE, unlike the bool on WhitelistAddPayload, and for the mirror image of the reason
    // documented there. Here null means "this event says nothing about the flag", so a sender that
    // predates demotion — and omits the field entirely — leaves it untouched instead of silently
    // demoting every peer it renames. Only an explicit true/false changes anything.
    [property: JsonPropertyName("is_superadmin")] bool? IsSuperadmin = null
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

// CiphertextB64 / CiphertextSha256: same by-reference scheme as ArticleEventPayload — see there.
// Media is where it matters most for transport: a single media_create used to carry up to ~27MB
// of base64 in one event, which is what forced the per-request size caps on the sync endpoints.
public record MediaEventPayload(
    [property: JsonPropertyName("media_id")]        Guid MediaId,
    [property: JsonPropertyName("article_id")]      Guid? ArticleId,
    [property: JsonPropertyName("file_name")]       string FileName,
    [property: JsonPropertyName("content_type")]    string ContentType,
    [property: JsonPropertyName("file_size")]       long FileSize,
    [property: JsonPropertyName("ciphertext")]      string? CiphertextB64,
    [property: JsonPropertyName("iv")]              string IvB64,
    [property: JsonPropertyName("encrypted_dek")]   string EncryptedDekB64,
    [property: JsonPropertyName("dek_iv")]          string DekIvB64,
    [property: JsonPropertyName("created_at")]      DateTime CreatedAt,
    // Defaults to "image" so events serialized before this field existed still deserialize
    // correctly — all of them predate the "attachment" kind, so the default is also the truth.
    [property: JsonPropertyName("kind")]            string Kind = "image",
    [property: JsonPropertyName("dek_epoch")]       int DekEpoch = 1,
    [property: JsonPropertyName("ciphertext_sha256")] string? CiphertextSha256 = null);

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
