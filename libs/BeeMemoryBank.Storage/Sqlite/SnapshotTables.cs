namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// What travels between nodes in a snapshot, in one place.
///
/// <para>This list used to exist three times — in <c>SnapshotJoinClient</c> and twice in
/// <c>SnapshotService.NetworkRestore</c> — and the copies had already drifted: two of them omitted
/// <c>tbl_comment</c> and <c>tbl_article_version</c>, so a node that JOINED a network silently
/// received no comments and no article history, while a node seeded by a network-wide RESTORE got
/// both. Nothing failed; the joiner simply had less content than everyone else, permanently.</para>
///
/// <para>The other half of the problem was that filtering worked by deny-list: name the secret
/// tables, ship everything else. Every table added since then shipped to peers by default, and
/// several nobody ever revisited — <c>tbl_remote_api_token</c>, <c>tbl_search_index_key</c>,
/// <c>tbl_dek_rotation_state</c> — went out with their contents. A new table is now stripped unless
/// it is listed here, so the failure mode of forgetting is missing content, not leaked data.</para>
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
        "tbl_comment", "tbl_article_version"
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
