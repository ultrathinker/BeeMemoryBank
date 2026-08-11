using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// Local-only data access for encrypted-segment bookkeeping: which segment files exist on disk
/// (tbl_search_index_manifest) and the single wrapped "index key" that encrypts them
/// (tbl_search_index_key). Both tables are a local cache exactly like the rest of this WP --
/// never part of the sync event log, never assumed authoritative, always safe to discard and
/// rebuild.
/// </summary>
public sealed class SegmentManifestRepository(DbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<SegmentManifestEntry?> GetManifestAsync(Guid segmentId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<SegmentManifestEntry>(
            @"SELECT
                segment_id     AS SegmentId,
                file_path      AS FilePath,
                doc_count      AS DocCount,
                dek_epoch      AS DekEpoch,
                format_version AS FormatVersion,
                created_at     AS CreatedAt
              FROM tbl_search_index_manifest WHERE segment_id = @segmentId",
            new { segmentId = segmentId.ToString() });
    }

    public async Task UpsertManifestAsync(SegmentManifestEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_search_index_manifest
                (segment_id, file_path, doc_count, dek_epoch, format_version, created_at)
              VALUES (@SegmentId, @FilePath, @DocCount, @DekEpoch, @FormatVersion, @CreatedAt)
              ON CONFLICT(segment_id) DO UPDATE SET
                file_path      = excluded.file_path,
                doc_count      = excluded.doc_count,
                dek_epoch      = excluded.dek_epoch,
                format_version = excluded.format_version,
                created_at     = excluded.created_at",
            new
            {
                SegmentId = entry.SegmentId.ToString(),
                entry.FilePath,
                entry.DocCount,
                entry.DekEpoch,
                entry.FormatVersion,
                entry.CreatedAt,
            });
    }

    public async Task DeleteManifestAsync(Guid segmentId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_search_index_manifest WHERE segment_id = @segmentId",
            new { segmentId = segmentId.ToString() });
    }

    public async Task<IndexKeyRow?> GetIndexKeyRowAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<IndexKeyRow>(
            @"SELECT
                wrapped_key AS WrappedKey,
                iv          AS IV,
                dek_epoch   AS DekEpoch,
                created_at  AS CreatedAt
              FROM tbl_search_index_key LIMIT 1");
    }

    /// <summary>
    /// Replaces the single index-key row. There is exactly one index key per node at a time (see
    /// migration 004's comment), so this deletes-then-inserts within a transaction -- the same
    /// pattern ProjectionMatrixRepository.SaveAsync uses for its own single-row secret.
    /// </summary>
    public async Task SaveIndexKeyRowAsync(IndexKeyRow row)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM tbl_search_index_key", transaction: tx);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_search_index_key (wrapped_key, iv, dek_epoch, created_at)
              VALUES (@WrappedKey, @IV, @DekEpoch, @CreatedAt)",
            new { row.WrappedKey, row.IV, row.DekEpoch, row.CreatedAt }, transaction: tx);
        tx.Commit();
    }

    /// <summary>
    /// Reads the node's current DEK epoch the same way DekRotationService.Propose does it: a raw
    /// SELECT against tbl_node_identity.dek_epoch (see AGENTS.md's "Non-obvious invariants" note
    /// on how dek_epoch is read elsewhere). There is no existing repository method that exposes
    /// this value as of this WP, and adding one to INodeIdentityRepository is out of this WP's
    /// declared file scope (see wp-09.md's "DO NOT TOUCH" list) -- so this queries the table
    /// directly, exactly like DekRotationService already does from a different project. Falls
    /// back to 1 (tbl_node_identity's own column default) if the identity row does not exist yet;
    /// that should not happen in practice, since writing/reading segments requires an unlocked
    /// session, which itself requires the node identity to already exist.
    /// </summary>
    public async Task<int> GetCurrentDekEpochAsync()
    {
        using var conn = OpenConnection();
        var epoch = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT dek_epoch FROM tbl_node_identity LIMIT 1");
        return epoch ?? 1;
    }
}
