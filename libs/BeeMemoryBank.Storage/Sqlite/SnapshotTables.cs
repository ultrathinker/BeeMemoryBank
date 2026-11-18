namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// What travels between nodes in a snapshot, in one place.
///
/// <para>Keep this the single list: <c>SnapshotJoinClient</c> and <c>SnapshotService.NetworkRestore</c>
/// both read it. Separate copies drift silently — a join path missing <c>tbl_comment</c> or
/// <c>tbl_article_version</c> leaves the joiner permanently short of content with nothing
/// failing.</para>
///
/// <para>Filtering is an allow-list, not a deny-list: a new table is stripped unless it is listed
/// here, so the failure mode of forgetting is missing content, not leaked data (a deny-list ships
/// every new table by default, secrets such as <c>tbl_remote_api_token</c>,
/// <c>tbl_search_index_key</c> or <c>tbl_dek_rotation_state</c> included).</para>
/// </summary>
public static class SnapshotTables
{
    /// <summary>
    /// Content replicated to peers, in import order.
    ///
    /// <para>Order is load-bearing: <c>tbl_blob</c> must come first because article bodies and
    /// versions address their ciphertext by hash into it (since migration 016; the inline column
    /// is gone since 017). Import it later and there is a window where a body row resolves to
    /// nothing; omit it and every article on the receiving node reads as empty while looking
    /// perfectly healthy.</para>
    /// </summary>
    public static readonly string[] Replicated =
    [
        "tbl_blob",
        "tbl_folder", "tbl_article", "tbl_article_body", "tbl_concept_tag",
        "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_media",
        "tbl_tombstone", "tbl_conflict_version", "tbl_projection_matrix",
        "tbl_comment", "tbl_article_version",
        // Blind-node recovery material (migration 027): sealed, safe for peers, and needed by any
        // node seeded or joined after the events that filled it were compacted away.
        "tbl_recovery_box", "tbl_dek_retired_link", "tbl_state_anchor", "tbl_sealed_secret",
        // The metadata keys, wrapped under the master DEK, and the activation record (migration 037, step-a-spec 4.1):
        // a joiner starts activated with the keys it opens after its first unlock.
        "tbl_metadata_key", "tbl_metadata_activation",
        // What each node announced about itself (node_endpoints, migration 044): a joiner can reach the host's other peers directly from the start,
        // even when their last announcement was compacted away. Older joiners never get here (schema 44), and ignore a table they do not list.
        "tbl_peer_endpoint",
        // Latest control state per node (node_state, migration 047): app version, runtime kind, key fingerprints and attention flags so a joiner
        // sees every node's runtime state even when superseded node_state events were compacted away.
        "tbl_node_state",
        // The retired identity keys of every node (node_key_rotated, migration 049): a joiner verifies a node's events signed before its
        // re-key, and walks the key transition chain, even when the rotation events were compacted away.
        "tbl_node_key_history",
        // Replicated accounts-home designation (accounts_home_set, migration 050): which node manages user accounts and superadmin doors.
        "tbl_accounts_home",
        // The tombstones of removed people (person_removed, migration 051, plan R1): a joiner and a restored node learn who was removed even when
        // the events were compacted away, and close those accounts before anybody signs in. Ids, outcome and time only.
        "tbl_removed_person"
    ];

    /// <summary>
    /// The replicated tables that only ever grow (release 2.12, plan R1): a network restore MERGES them (insert-or-ignore, nothing deleted)
    /// instead of replacing them, because an older snapshot does not undo what happened since. <c>tbl_dek_retired_link</c>: a link a later
    /// rotation wrote is what lets this node read data and restores sealed under the keys it retired. Later packages add their own (the
    /// removed people, the node key history). Every entry is also in <see cref="Replicated"/>.
    /// </summary>
    public static readonly string[] Monotonic =
    [
        "tbl_dek_retired_link",
        "tbl_node_key_history",
        // A restore of a snapshot from before a removal must not bring the removed person back (plan R1).
        "tbl_removed_person",
    ];

    /// <summary>
    /// The clear control tables a format-2 package carries in <c>control.db</c> beside the encrypted baseline
    /// (step-a-spec 8.4): the metadata keys and the activation, and the recovery material a restore needs before it can
    /// open anything. Every entry is also in <see cref="Replicated"/>; nothing here holds human-authored metadata.
    /// </summary>
    public static readonly string[] Control =
    [
        "tbl_metadata_key", "tbl_metadata_activation",
        "tbl_recovery_box", "tbl_dek_retired_link", "tbl_state_anchor", "tbl_sealed_secret",
        // A package signed before the producer's re-key, and the events in it, verify only with the key history (plan D4).
        "tbl_node_key_history"
    ];

    /// <summary>
    /// The control tables a package made before release 2.12 does not have (<c>tbl_node_key_history</c>, migration 049): a reader accepts a
    /// package without them, and checks their rows when they are there. Every entry is also in <see cref="Control"/>.
    /// </summary>
    public static readonly string[] ControlOptional =
    [
        "tbl_node_key_history",
    ];

    /// <summary>
    /// Schema bookkeeping that must survive filtering untouched. Stripping the migration record
    /// would leave a receiving node believing migrations had run against tables that are not there,
    /// and nothing re-runs schema creation after an import.
    /// </summary>
    public static readonly string[] SchemaMeta = ["tbl_migration", "tbl_migration_marker"];

    /// <summary>
    /// Tables removed from a peer package by DROP rather than by emptying — node identity, key
    /// slots, users, sessions, the event log and sync positions.
    ///
    /// <para>The distinction is deliberate and doing it the other way round would be a mistake in
    /// both directions. These specific tables are dropped because their ABSENCE is what identifies
    /// an archive as a peer package rather than a backup: <c>SnapshotService.RestoreAsync</c>
    /// refuses to restore an archive with no <c>tbl_key_slot</c>, since the key slots hold the only
    /// wrapped copies of the master DEK and restoring without them yields a vault nobody can ever
    /// open again. Leave an empty <c>tbl_key_slot</c> behind and that check has nothing to see.</para>
    ///
    /// <para>Everything else outside <see cref="Replicated"/> and <see cref="SchemaMeta"/> is
    /// EMPTIED instead, keeping its schema intact — see the comments in <c>FilterSecretsFrom</c>
    /// for why a receiving node needs the table to still exist.</para>
    /// </summary>
    public static readonly string[] StrippedByDropping =
    [
        "tbl_node_identity", "tbl_session", "tbl_agent", "tbl_agent_access",
        "tbl_sync_position", "tbl_sync_push_position", "tbl_compaction_log", "tbl_event",
        "tbl_key_slot", "tbl_user", "tbl_folder_acl_entry", "tbl_audit_log",
        "tbl_hard_delete_audit"
    ];
}
