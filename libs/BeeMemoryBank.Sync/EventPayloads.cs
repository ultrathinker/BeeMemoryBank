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

    // Blind nodes (BMB-43): recovery material, see RecoveryPayloads.cs and migration 027.
    public const string RecoveryBoxSet = "recovery_box_set";
    public const string RecoveryBoxRetire = "recovery_box_retire";
    public const string RetiredLinkSet = "retired_link_set";
    public const string StateAnchor = "state_anchor";
    public const string SealedSecretSet = "sealed_secret_set";

    // Direct sync on the LAN (docs/design/lan-peer-sync.md section 8, ADR 0011): a node's signed record of where it listens and its TLS anchors.
    // Travels only between nodes that advertise peer-endpoints-v1 (Metadata.MetadataCapabilities.PeerEndpointsV1).
    public const string NodeEndpoints = "node_endpoints";

    // Self-authored node state (release 2.12.0): version, platform, runtime kind, key fingerprints, attention flags.
    public const string NodeState = "node_state";

    // Node identity re-key (release 2.12.0, plan WS-D): a node's own announcement of its new identity key, signed by the key it retires, and a
    // superadmin's settlement of two competing rotations of another node. See NodeKeyRotatedPayload and NodeKeyConfirmPayload.
    public const string NodeKeyRotated = "node_key_rotated";
    public const string NodeKeyConfirm = "node_key_confirm";
    // Shared-node accounts home and replicated superadmin doors (release 2.12.0).
    public const string AccountsHomeSet = "accounts_home_set";
    public const string SuperadminDoorsSet = "superadmin_doors_set";
    // The removal of a superadmin (release 2.12.0, plan WS-E and R1): the tombstone of a removed person (person id, outcome, time; no names)
    // and the state of a removal process, so shared full nodes see it and can finish it when the accounts node is lost.
    public const string PersonRemoved = "person_removed";
    public const string OffboardingState = "offboarding_state";
    // Step S1 of a removal (release 2.12.0): the driver asks the shared full nodes to give themselves a new identity key, once per process.
    public const string NodeRekeyRequest = "node_rekey_request";

    // Step A (docs/design/step-a-spec.md 3.1, 4.3, 5.4): the outer type of a protocol-4 content event and the two
    // control events of the metadata key. Aliases of Metadata.EventClasses, which classifies every type here.
    public const string Sealed = Metadata.EventClasses.Sealed;
    public const string MetadataKeySet = Metadata.EventClasses.MetadataKeySet;
    public const string MetadataActivation = Metadata.EventClasses.MetadataActivation;
}

/// <summary>
/// The one place that says who may originate each kind of sync event. <see cref="EventApplier"/>'s
/// authorization gate reads <see cref="RequiresSuperadmin"/> from here instead of hard-coding a
/// list, and <c>EventAuthorizationGuardTests</c> asserts that every constant in
/// <see cref="EventTypes"/> is classified into exactly one category below.
///
/// Why the guard matters: the applier's <c>switch</c> has a <c>default:</c> that silently ignores
/// unknown types, so a new cluster-state event left out of the superadmin gate would apply from
/// any whitelisted peer — exactly the "a single rogue peer can revoke the whole network" hole the
/// gate exists to close.
/// Forcing every new type into one of these sets turns "remember to update the gate" into a
/// compile-then-red-test, the same house pattern as the other guardrail tests.
/// </summary>
public static class EventAuthorization
{
    /// <summary>
    /// Cluster-state events. They apply immediately and irreversibly change who is trusted or what
    /// exists network-wide, so only a peer marked superadmin in the local whitelist may originate
    /// them. <see cref="MasterPasswordChanged"/> is here not because it is cluster-state but because
    /// it raises an admin-UI security notice a rogue peer could use to phish a password re-entry.
    /// </summary>
    public static readonly IReadOnlySet<string> SuperadminOnly = new HashSet<string>
    {
        EventTypes.WhitelistAdd,
        EventTypes.WhitelistRevoke,
        EventTypes.WhitelistUpdate,
        EventTypes.HardDelete,
        EventTypes.RestoreNetwork,
        EventTypes.MasterPasswordChanged,
        // Retiring another node's recovery box removes a way back into the vault; an integrity
        // anchor is what a restore trusts. Both are cluster-state decisions.
        EventTypes.RecoveryBoxRetire,
        EventTypes.StateAnchor,
        // The metadata key and the one-way activation (step-a-spec 3.1): a key record names which key every node
        // seals with, and the activation cuts 2.5 peers off; both are vault-wide decisions of a superadmin.
        EventTypes.MetadataKeySet,
        EventTypes.MetadataActivation,
        // Who signs for a node from now on (plan WS-D): a re-key changes the key every peer trusts for that node, and a confirm decides which
        // of two competing keys holds. Both are trust decisions of a superadmin node; the applier adds its own rules (a node re-keys only
        // itself, a confirm is never about its signer).
        EventTypes.NodeKeyRotated,
        EventTypes.NodeKeyConfirm,
        // Shared-node accounts home and sealed superadmin doors (release 2.12.0).
        EventTypes.AccountsHomeSet,
        EventTypes.SuperadminDoorsSet,
        // The removal of a superadmin (release 2.12.0): a tombstone deletes a person's account and doors wherever it applies, and the process
        // state is what a shared node may take over. Both are decisions only a superadmin makes.
        EventTypes.PersonRemoved,
        EventTypes.OffboardingState,
        EventTypes.NodeRekeyRequest,
    };

    /// <summary>
    /// Content and metadata events. Any whitelisted peer may originate them; conflicts are resolved
    /// by LWW, never by trust level.
    /// </summary>
    public static readonly IReadOnlySet<string> AnyPeer = new HashSet<string>
    {
        EventTypes.ArticleCreate,
        EventTypes.ArticleUpdate,
        EventTypes.ArticleDelete,
        EventTypes.CommentCreate,
        EventTypes.CommentDelete,
        EventTypes.FolderCreate,
        EventTypes.FolderRename,
        EventTypes.FolderDelete,
        EventTypes.MediaCreate,
        EventTypes.MediaDelete,
        EventTypes.MediaLink,
        EventTypes.ConceptTagRename,
        EventTypes.ConceptTagMerge,
        EventTypes.ConceptTagDelete,
        EventTypes.SnapshotCheckpoint,
        // A protocol-4 content event: the gate runs again on the opened view (step-a-spec 6.3), so a sealed hard
        // delete from a plain peer is refused after it is opened, exactly as a clear one is.
        EventTypes.Sealed,
        // Any full node may publish these: a box is always the author's own (checked by the
        // applier), and a forged link or sealed secret is harmless because a restore keeps only
        // what opens and verifies. Restricting links to superadmins would lose a link together
        // with the node that rotated (see RecoveryPayloads.cs).
        EventTypes.RecoveryBoxSet,
        EventTypes.RetiredLinkSet,
        EventTypes.SealedSecretSet,
        // A node's record of its own endpoints: always the SIGNER's own (checked by the applier, as for a recovery box), so any full node may
        // publish one; a forged one can only add candidates, which a peer accepts only after the TLS anchor and the node key match.
        EventTypes.NodeEndpoints,
        // Self-authored node state (author == subject checked by EventInvariants and EventApplier). Allowed from blind nodes too (without dek_fp).
        EventTypes.NodeState,
    };

    /// <summary>
    /// DEK rotation. A proposal or commit from any whitelisted peer is merely RECORDED as pending
    /// state; the destructive rewrap runs only behind a per-peer auto-accept flag or an explicit
    /// admin accept (see <c>EventApplier.Restore.cs</c>). That deferred-trust gate — not the
    /// immediate superadmin one — is the right gate for these, so they are classified apart:
    /// putting them under <see cref="SuperadminOnly"/> would wrongly reject a normal peer's
    /// proposal outright, and leaving them in <see cref="AnyPeer"/> would wrongly imply they carry
    /// no extra gate at all.
    /// </summary>
    public static readonly IReadOnlySet<string> RotationDeferredTrust = new HashSet<string>
    {
        EventTypes.DekRotationProposed,
        EventTypes.DekRotationCommit,
    };

    /// <summary>True if only a superadmin peer may originate this event type.</summary>
    public static bool RequiresSuperadmin(string eventType) => SuperadminOnly.Contains(eventType);
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
/// One recipient's sealed copy of the new master DEK in a confidential rotation (ADR 0006): the
/// AES-256-GCM ciphertext (with tag) and its nonce, both base64. Openable only by the node whose
/// derived X25519 private key was used to build it — see <see cref="DekEnvelope"/>.
/// </summary>
public record DekEnvelopeBox(
    [property: JsonPropertyName("wrapped")] string Wrapped,
    [property: JsonPropertyName("nonce")]   string Nonce
);

/// <summary>
/// The per-peer X25519 envelope set carried by a confidential DEK rotation (ADR 0006). Replaces the
/// single wrap-under-old-DEK <c>encrypted_new_dek</c>: the new DEK is sealed once per currently-active
/// peer, so a revoked node — enumerated out of the rotation — never receives an openable copy. The
/// map is keyed by UPPERCASE node id (the GUID-case trap); each node opens only <c>peers[myNodeId]</c>.
/// </summary>
public record DekEnvelopesPayload(
    [property: JsonPropertyName("ephemeral_pub")] string EphemeralPub,
    [property: JsonPropertyName("peers")]         Dictionary<string, DekEnvelopeBox> Peers
);

/// <summary>
/// Phase 1 of DEK rotation: initiator broadcasts a proposal. Other peers record it as Pending
/// in tbl_dek_rotation_state but do NOT start rotating yet — they wait for a matching
/// DekRotationCommit. This split closes the cross-node split-brain window where two concurrent
/// rotates would both go destructive before noticing each other.
///
/// <para>Confidential rotations (ADR 0006) carry <see cref="DekEnvelopes"/> and OMIT
/// <see cref="EncryptedNewDek"/> / <see cref="Iv"/> — both are therefore nullable. Legacy events
/// already in tbl_event still carry the old fields and no envelopes; the applier reads whichever is
/// present.</para>
/// </summary>
public record DekRotationProposedPayload(
    [property: JsonPropertyName("new_dek_epoch")]     int NewDekEpoch,
    [property: JsonPropertyName("rotation_ts")]       string RotationTs,
    [property: JsonPropertyName("expires_at")]        string ExpiresAt,
    [property: JsonPropertyName("originator_node_id")] string OriginatorNodeId,
    [property: JsonPropertyName("encrypted_new_dek")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EncryptedNewDek = null,
    [property: JsonPropertyName("iv")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]                string? Iv = null,
    [property: JsonPropertyName("dek_envelopes")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]     DekEnvelopesPayload? DekEnvelopes = null,
    [property: JsonPropertyName("reason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]            string? Reason = null
);

/// <summary>
/// Phase 2 of DEK rotation: initiator confirms the proposal won the cross-node tiebreaker
/// and wants peers to actually apply the rotation. Carries a reference back to the matching
/// proposed event so receivers can match COMMIT with PROPOSED.
///
/// <para>Confidential rotations (ADR 0006) carry <see cref="DekEnvelopes"/> and OMIT
/// <see cref="EncryptedNewDek"/> / <see cref="Iv"/> — see <see cref="DekRotationProposedPayload"/>.</para>
/// </summary>
public record DekRotationCommitPayload(
    [property: JsonPropertyName("proposed_event_id")] string ProposedEventId,
    [property: JsonPropertyName("new_dek_epoch")]     int NewDekEpoch,
    [property: JsonPropertyName("rotation_ts")]       string RotationTs,
    [property: JsonPropertyName("originator_node_id")] string OriginatorNodeId,
    [property: JsonPropertyName("encrypted_new_dek")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EncryptedNewDek = null,
    [property: JsonPropertyName("iv")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]                string? Iv = null,
    [property: JsonPropertyName("dek_envelopes")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]     DekEnvelopesPayload? DekEnvelopes = null,
    // 2.12 (plan WS-A5): "offboarding:<process id>" marks a removal rotation, which every unlocked full node applies by itself
    // (DekRotationReasons). Absent for every other rotation; an older node ignores it.
    [property: JsonPropertyName("reason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]            string? Reason = null
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
    // field entirely and JSON deserialization fills false. The field must
    // travel: without it a 3+ node cluster loses the IsSuperadmin bit on
    // every sync — the receiving node creates the entry as non-superadmin
    // and then rejects the new peer's hard_delete / restore_network /
    // whitelist_add events forever (cluster split-brain).
    [property: JsonPropertyName("is_superadmin")]    bool IsSuperadmin = false,
    // The pinned TLS key of a peer with a self-signed certificate (a blind node, plan 4.4). Absent
    // from older senders, which never add such a peer.
    [property: JsonPropertyName("tls_spki")]         string? TlsSpki = null,
    // How blind copies trust this node's TLS endpoint: "pin" or "public-ca" (BlindTrust, ADR 0007).
    // Optional and ignored by an older node, which then reads the row as it always did (a pin is a
    // pin; nothing else is callable) — sync protocol 3 is unchanged.
    [property: JsonPropertyName("tls_trust")]        string? TlsTrust = null,
    // Device ownership (release 2.12, offboarding): optional "shared" | "personal" | "unknown", owner person_id, and display label.
    // Omitted when unknown or from pre-2.12 senders; receivers without these fields leave ownership unknown with needs_review.
    [property: JsonPropertyName("ownership")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ownership = null,
    [property: JsonPropertyName("owner_person_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerPersonId = null,
    [property: JsonPropertyName("owner_label")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerLabel = null
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
    [property: JsonPropertyName("is_superadmin")] bool? IsSuperadmin = null,
    // Null = unchanged, same rule as above: a blind node's new certificate after its volume was
    // recreated, or a pin set on a row that was added without one. "" = remove the pin: sent when the
    // mode below stops pinning, so an older node — which knows nothing of the mode and applies a
    // present tls_spki as "set" — stores an empty value its pin registry skips.
    [property: JsonPropertyName("tls_spki")]      string? TlsSpki = null,
    // Null = unchanged. "pin" / "public-ca" set the mode (public-ca also clears the pin); "none" takes
    // the mode and the pin away (BlindTrust, ADR 0007). A value this build does not know changes nothing.
    [property: JsonPropertyName("tls_trust")]     string? TlsTrust = null,
    // Null = unchanged: events without ownership leave the stored ownership untouched.
    [property: JsonPropertyName("ownership")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ownership = null,
    [property: JsonPropertyName("owner_person_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerPersonId = null,
    [property: JsonPropertyName("owner_label")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerLabel = null
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
// Media is where it matters most for transport: inline, a single media_create can carry up to
// ~27MB of base64 in one event, which is why the sync endpoints have per-request size caps.
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

/// <summary>
/// node_endpoints (docs/design/lan-peer-sync.md section 8): where a node's LAN listener serves the sync surface right now, and the TLS anchors a peer
/// may accept for it. Published by the node about ITSELF only (<see cref="NodeId"/> must be the signer), replacing its previous record: the newest by
/// the Lamport time of that node's own events wins on every node.
/// </summary>
/// <param name="NodeId">The node the record is about: always the event's own node id.</param>
/// <param name="Seq">The author's own counter of its announcements (informational; the Lamport time decides).</param>
/// <param name="Endpoints">At most <see cref="NodeEndpointsRules.MaxEndpoints"/>; only private-range and VPN addresses of trusted networks.</param>
/// <param name="TlsLeafSpki">The pin of the leaf key the node serves (base64url SHA-256 of its SubjectPublicKeyInfo), or null.</param>
/// <param name="TlsCaKey">The node's local CA public key (base64url DER SubjectPublicKeyInfo): a leaf it signs is accepted (<c>ca-pin</c>), or null.</param>
/// <param name="Port">The LAN port, for the record (every endpoint carries its own).</param>
public record NodeEndpointsPayload(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("endpoints")] List<NodeEndpointEntry> Endpoints,
    [property: JsonPropertyName("tls_leaf_spki")] string? TlsLeafSpki,
    [property: JsonPropertyName("tls_ca_key")] string? TlsCaKey,
    [property: JsonPropertyName("port")] int? Port);

/// <summary>One endpoint of a <see cref="NodeEndpointsPayload"/>: an https URL and its scope (<c>lan</c> or <c>vpn</c>; <c>internet</c> is reserved).</summary>
public record NodeEndpointEntry(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>
/// Self-authored node state control event payload (<see cref="EventTypes.NodeState"/>): version, platform, runtime kind,
/// identity and DEK fingerprints, attention flags, and optional extensible metadata. Author (<c>SyncEvent.NodeId</c>) must equal <see cref="NodeId"/>.
/// </summary>
public record NodeStatePayload(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("runtime_kind")] string RuntimeKind,
    [property: JsonPropertyName("identity_key_fp")] string IdentityKeyFp,
    [property: JsonPropertyName("dek_fp")] string? DekFp,
    [property: JsonPropertyName("attention_flags")] List<string>? AttentionFlags = null,
    [property: JsonPropertyName("extra")] Dictionary<string, string>? Extra = null);

/// <summary>
/// A node's new identity key (<see cref="EventTypes.NodeKeyRotated"/>, plan WS-D D1, D3). Signed by the OLD key: it is the node's last event under
/// it. Keys and proofs are base64. <see cref="NewKeyProof"/> and <see cref="OldKeyProof"/> are the two keys' signatures over
/// <see cref="BeeMemoryBank.Core.Models.NodeKeyTransition.Statement"/>; <see cref="OwnEventIds"/> is <see cref="BeeMemoryBank.Core.Models.EventIdSet"/>.
/// </summary>
public record NodeKeyRotatedPayload(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("old_public_key")] string OldPublicKey,
    [property: JsonPropertyName("new_public_key")] string NewPublicKey,
    [property: JsonPropertyName("counter")] long Counter,
    [property: JsonPropertyName("rotated_at")] string RotatedAt,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("new_key_proof")] string NewKeyProof,
    [property: JsonPropertyName("old_key_proof")] string OldKeyProof,
    [property: JsonPropertyName("checkpoint_lamport_ts")] long CheckpointLamportTs,
    [property: JsonPropertyName("own_event_ids")] string OwnEventIds);

/// <summary>
/// A superadmin's settlement of an identity conflict (<see cref="EventTypes.NodeKeyConfirm"/>, plan WS-D D2): "node <see cref="NodeId"/>'s key is
/// <see cref="ConfirmedPublicKey"/> as of rotation <see cref="RotationEventId"/>, counter <see cref="Counter"/>". Never about its signer.
/// </summary>
public record NodeKeyConfirmPayload(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("rotation_event_id")] string RotationEventId,
    [property: JsonPropertyName("counter")] long Counter,
    [property: JsonPropertyName("confirmed_public_key")] string ConfirmedPublicKey);

/// <summary>
/// Control event setting which node manages user accounts and issues superadmin doors
/// (<see cref="EventTypes.AccountsHomeSet"/>). Newest by (Lamport time, author node id) wins.
/// </summary>
public record AccountsHomeSetPayload(
    [property: JsonPropertyName("node_id")] Guid NodeId);

/// <summary>
/// Full-state control event from the accounts node carrying an encrypted list of active superadmin
/// v2 user doors (<see cref="EventTypes.SuperadminDoorsSet"/>). Only active non-blind shared full nodes
/// receive an envelope in <see cref="ListEnvelopes"/>; personal and blind copies never receive one.
/// </summary>
public record SuperadminDoorsSetPayload(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("ciphertext")] string CiphertextB64,
    [property: JsonPropertyName("iv")] string IvB64,
    [property: JsonPropertyName("list_envelopes")] DekEnvelopesPayload ListEnvelopes);

/// <summary>Decrypted content of <see cref="SuperadminDoorsSetPayload"/> (never placed on wire in clear).</summary>
public sealed record SuperadminDoorsDocument(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("people")] List<SuperadminDoorDescriptor> People);

/// <summary>One active superadmin's door descriptor replicated from the accounts node to shared nodes.</summary>
public sealed record SuperadminDoorDescriptor(
    [property: JsonPropertyName("person_id")] Guid PersonId,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("password_hint")] string? PasswordHint,
    [property: JsonPropertyName("salt")] string SaltB64,
    [property: JsonPropertyName("argon_m")] int ArgonMemory,
    [property: JsonPropertyName("argon_t")] int ArgonIterations,
    [property: JsonPropertyName("argon_p")] int ArgonParallelism,
    [property: JsonPropertyName("door_pk")] string DoorPublicKeyB64);

/// <summary>
/// The tombstone of a removed person (<see cref="EventTypes.PersonRemoved"/>, release 2.12.0, plan R1): who (the person id of the accounts node,
/// which a shared node's mirror row carries too), what became of them (<c>deleted</c> or <c>demoted</c>) and when. No name, no folder, no role:
/// the target role and folders of a demotion stay in the accounts node's process record. Every full node keeps it in <c>tbl_removed_person</c>.
/// </summary>
public record PersonRemovedPayload(
    [property: JsonPropertyName("person_id")] string PersonId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("removed_at")] DateTime RemovedAt);

/// <summary>
/// The state of one removal process (<see cref="EventTypes.OffboardingState"/>, release 2.12.0, plan E2), newest event wins. Ids, the kind of
/// target, the step and the times only: no names, no role, no folder rules. A shared full node shows it, refuses the person while it is open, and a
/// superadmin there may take it over (<see cref="DriverNodeId"/> then names that node) when the accounts node was revoked.
/// </summary>
/// <summary>
/// The driver of a removal asks nodes for a new identity key (<see cref="EventTypes.NodeRekeyRequest"/>, release 2.12.0, step S1). Ids and the reason
/// only: <see cref="Reason"/> is <c>offboarding:&lt;process id&gt;</c>, the reason the asked node gives its own re-key, which is how the request is
/// idempotent per process. Accepted only from the node that drives that process.
/// </summary>
public record NodeRekeyRequestPayload(
    [property: JsonPropertyName("process_id")] string ProcessId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("nodes")] List<string> Nodes);

public record OffboardingStatePayload(
    [property: JsonPropertyName("process_id")] string ProcessId,
    [property: JsonPropertyName("person_id")] string PersonId,
    [property: JsonPropertyName("accounts_node_id")] string AccountsNodeId,
    [property: JsonPropertyName("driver_node_id")] string DriverNodeId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("closed_at")] DateTime? ClosedAt = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null);

