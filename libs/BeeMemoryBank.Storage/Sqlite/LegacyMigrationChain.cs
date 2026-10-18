namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// The migration history of releases 2.6.0 and 2.6.1: the 39 files (versions 1..40, there is no 32) that every 2.6.x database has
/// applied, the last of them <c>040_sealed_unlock_material.sql</c>. The files are not part of the program any more (the schema they
/// built is the one baseline migration); this table is all that is kept of them, so <see cref="MigrationRunner"/> can recognise a
/// database that ran the whole chain and adopt it, and tell it from one that stopped short (2.2..2.5, or an interrupted update), which
/// it refuses. It is NOT a migration: nothing in it is ever executed.
///
/// <para>Generated from the file lists of the released versions (see tests/BeeMemoryBank.Storage.Tests/LegacyChain/chains.txt, line
/// v2.6.1) and checked against them by <c>RecognisedChainTests</c>; do not edit by hand.</para>
/// </summary>
internal static class LegacyMigrationChain
{
    /// <summary>The prefix of the resource names the old runner wrote into <c>tbl_migration.filename</c>.</summary>
    public const string ResourcePrefix = "BeeMemoryBank.Storage.Migrations.";

    /// <summary>Version and file name of every migration of the chain, in order.</summary>
    public static readonly IReadOnlyList<(int Version, string File)> Files =
    [
        (1, "001_initial_schema.sql"),
        (2, "002_session_settings.sql"),
        (3, "003_media_kind.sql"),
        (4, "004_search_index_manifest.sql"),
        (5, "005_fts5_metadata_index.sql"),
        (6, "006_index_pending.sql"),
        (7, "007_search_segment_tombstone.sql"),
        (8, "008_article_chunk_embedding.sql"),
        (9, "009_custom_roles.sql"),
        (10, "010_brand_name.sql"),
        (11, "011_favorites.sql"),
        (12, "012_remove_unsafe_recovery_key_slots.sql"),
        (13, "013_sync_quarantine.sql"),
        (14, "014_agent_dek_optional.sql"),
        (15, "015_revoked_agents_drop_dek.sql"),
        (16, "016_blob_store_expand.sql"),
        (17, "017_blob_store_contract.sql"),
        (18, "018_master_password_notice.sql"),
        (19, "019_master_password_local_change.sql"),
        (20, "020_dek_rotation_chain_material.sql"),
        (21, "021_whitelist_row_version.sql"),
        (22, "022_sync_quarantine_deferred.sql"),
        (23, "023_media_ciphertext_hash.sql"),
        (24, "024_drop_deprecated_keyword_tag_tables.sql"),
        (25, "025_media_uploaded_by.sql"),
        (26, "026_node_data_key.sql"),
        (27, "027_blind_recovery.sql"),
        (28, "028_blind_protocol_and_state.sql"),
        (29, "029_whitelist_tls_spki.sql"),
        (30, "030_recovery_box_local.sql"),
        (31, "031_blind_restore_code.sql"),
        (33, "033_blind_rows_never_superadmin.sql"),
        (34, "034_sync_push_reported.sql"),
        (35, "035_os_auto_unlock_unique_slot.sql"),
        (36, "036_whitelist_tls_trust.sql"),
        (37, "037_metadata_keys.sql"),
        (38, "038_event_apply_state.sql"),
        (39, "039_blind_baseline_store.sql"),
        (40, "040_sealed_unlock_material.sql")
    ];

    /// <summary>The ledger rows a database that ran the whole chain has: version and resource name.</summary>
    public static IEnumerable<(int Version, string ResourceName)> LedgerRows => Files.Select(f => (f.Version, ResourcePrefix + f.File));
}
