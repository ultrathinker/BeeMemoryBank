namespace BeeMemoryBank.Rekey;

/// <summary>
/// The fate of every table of the copy (rekey-offline.md §8.3). The orchestrator refuses to run on a database with a
/// table that is not listed here, so a new migration cannot slip a table past the re-key.
/// <para><see cref="Main"/> is re-cut from the live design's <c>CampaignTables</c> lists (45f08e8c, 7f277546) and
/// <c>SnapshotTables.Replicated</c>, checked against the schema migrations 001..039 build. The comment on each group
/// names the step that carries its fate out.</para>
/// </summary>
public static class RekeyTables
{
    private static Dictionary<string, TableFate> Build(params (TableFate Fate, string[] Tables)[] groups)
    {
        var map = new Dictionary<string, TableFate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fate, tables) in groups)
            foreach (var t in tables)
                map.Add(t, fate); // Add, not the indexer: a table listed twice is a bug in this file
        return map;
    }

    /// <summary>The main database. Owned by R1.</summary>
    public static IReadOnlyDictionary<string, TableFate> Main { get; } = Build(
        // RowResealStep: every row sealed under a fresh entity key wrapped under D_c (comments under their body's
        // key), media imported from .enc files into blobs, the blobs of the old ciphertexts removed, sealed secrets
        // and remote-account tokens re-sealed directly under D_c.
        (TableFate.Resealed,
        [
            "tbl_article_body", "tbl_article_version", "tbl_conflict_version", "tbl_comment", "tbl_media", "tbl_blob",
            "tbl_sealed_secret", "tbl_remote_account",
        ]),
        // DerivedDataClearStep: derived from content and rebuilt by the existing startup paths (the index key and
        // the projection matrix are created again on first use). tbl_article and tbl_concept_tag keep their rows,
        // their vector columns are cleared by the same step.
        (TableFate.Cleared,
        [
            "tbl_article_chunk_embedding", "tbl_search_index_manifest", "tbl_search_segment_tombstone",
            "tbl_search_index_key", "tbl_projection_matrix",
        ]),
        // KeyMaterialStep (L): recovery boxes, links and their bookkeeping.
        // EventLogResetStep (L): the event log, quarantine, and what is positioned in the old log (sync positions,
        // restore replay state, state anchors).
        (TableFate.Cleared,
        [
            "tbl_recovery_box", "tbl_recovery_box_check", "tbl_recovery_box_pending_retire", "tbl_dek_retired_link",
            "tbl_event", "tbl_sync_quarantine", "tbl_sync_position", "tbl_sync_push_position",
            "tbl_restore_event_state", "tbl_restore_replay_shield", "tbl_state_anchor",
            // A blind host's adopted baseline and its blob list (migration 039, step-a-spec 9.1): positioned in the old log
            // (local_cut). A full node holds none; cleared with the log all the same.
            "tbl_baseline", "tbl_baseline_blob",
        ]),
        // PeerRevokeStep (L): the invitations to blind copies this node made (migration 041) and the redemptions waiting for its owner. They are about who
        // may join, and a re-keyed node revokes every peer; a redeemed one also holds the device's backup key sealed under the OLD master key, which nothing
        // can open any more (and the D1 check would find it). The owner invites again.
        (TableFate.Cleared, ["tbl_blind_invite"]),
        // KeyMaterialStep (L): the sentinel and identity key, the owner's slot (others removed), agents' wrapped
        // DEKs, node data keys (retired-master-dek:* removed; chat re-wrapped by ChatRekeyStep), rotation chain columns.
        (TableFate.KeyMaterial,
        [
            "tbl_node_identity", "tbl_key_slot", "tbl_agent", "tbl_node_data_key", "tbl_dek_rotation_state",
            // The metadata keys (migration 037): every row is wrapped under an old DEK. An activated vault gets one new
            // generation-1 key under D_c; nothing is logged, the re-keyed node is alone (step-a-spec 4.1).
            "tbl_metadata_key",
        ]),
        // Kept as they are: nothing in them is sealed under the DEK. The whitelist is revoked by PeerRevokeStep (L).
        (TableFate.Plaintext,
        [
            "tbl_migration", "tbl_migration_marker", "sqlite_sequence",
            "tbl_folder", "tbl_article", "tbl_concept_tag", "tbl_article_concept_tag", "tbl_concept_tag_edge",
            "tbl_tombstone",
            "tbl_user", "tbl_role", "tbl_role_folder_acl_entry", "tbl_folder_acl_entry",
            "tbl_favorite", "tbl_remote_api_token", "tbl_remote_subscription",
            "tbl_audit_log", "tbl_hard_delete_audit", "tbl_compaction_log",
            "tbl_whitelist", "tbl_blind_state", "tbl_blind_restore_code",
            // The activation record (migration 037): ids only; the vault stays activated after a re-key.
            "tbl_metadata_activation",
            // The full-text index of titles, paths and tag names (plaintext columns) and its shadow tables.
            "fts_article", "fts_article_config", "fts_article_data", "fts_article_docsize", "fts_article_idx",
            "fts_folder", "fts_folder_config", "fts_folder_data", "fts_folder_docsize", "fts_folder_idx",
            "fts_tag", "fts_tag_config", "fts_tag_data", "fts_tag_docsize", "fts_tag_idx",
        ]));

    /// <summary>chat.db. Owned by R2. Conversation titles and key prefixes are sealed under the chat key; one still in
    /// its legacy plaintext column is sealed by ChatRekeyStep and the column cleared.</summary>
    public static IReadOnlyDictionary<string, TableFate> Chat { get; } =
        new Dictionary<string, TableFate>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat_message"] = TableFate.Resealed,
            ["chat_attachment"] = TableFate.Resealed,
            ["chat_api_key"] = TableFate.Resealed,
            ["chat_conversation"] = TableFate.Resealed,
            ["chat_model"] = TableFate.Plaintext,
            ["chat_settings"] = TableFate.Plaintext,
            ["chat_user_settings"] = TableFate.Plaintext,
        };
}
