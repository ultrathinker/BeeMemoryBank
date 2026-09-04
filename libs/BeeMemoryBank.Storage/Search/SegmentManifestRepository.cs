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

    /// <summary>
    /// WP-11: every currently-recorded segment manifest row, for the unlock warm-start path to
    /// enumerate and attempt to load each one back via <c>EncryptedSegmentStore.LoadAsync</c>.
    /// </summary>
    public async Task<List<SegmentManifestEntry>> GetAllManifestsAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<SegmentManifestEntry>(
            @"SELECT
                segment_id     AS SegmentId,
                file_path      AS FilePath,
                doc_count      AS DocCount,
                dek_epoch      AS DekEpoch,
                format_version AS FormatVersion,
                created_at     AS CreatedAt
              FROM tbl_search_index_manifest");
        return rows.ToList();
    }

    /// <summary>
    /// WP-11: clears every manifest row. Used only by the search-index full-rebuild path (see
    /// <c>SearchIndexLifecycleService.TriggerFullRebuildAsync</c>) -- the segment FILES themselves
    /// are deliberately left on disk (an orphaned .bmesg file is just wasted space, not a
    /// correctness problem, and deleting them here would add I/O to an already-degraded path
    /// without changing behavior).
    /// </summary>
    public async Task DeleteAllManifestsAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_search_index_manifest");
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

    /// <summary>
    /// WP-19 (merge persistence): atomically retires a merge's input segments and, if the merge
    /// produced surviving content, installs its output segment's manifest row -- both in ONE
    /// database transaction, never as separate statements. This is the crash-safety-critical step
    /// <c>SearchIndexLifecycleService.PersistMostRecentlyMergedSegmentAsync</c> relies on; see that
    /// method's own doc comment for the full write-new-file / commit-this-transaction / delete-old-
    /// files ordering and why it must be exactly that order.
    ///
    /// <para>
    /// <b>Why this touches tbl_search_segment_tombstone, a table <see cref="SegmentTombstoneRepository"/>
    /// otherwise owns:</b> Dapper/SQLite transactions are scoped to a single open connection
    /// (<see cref="System.Data.IDbConnection.BeginTransaction"/>), and the whole point of this
    /// method is that the new manifest row's insert and the old rows' deletes across BOTH tables
    /// must commit or roll back together -- splitting this across two repositories, each opening
    /// its own connection/transaction, would reopen exactly the "insert committed, deletes not yet"
    /// (or vice versa) crash window this method exists to close. Retiring a segment's tombstone rows
    /// alongside its manifest row here is deliberate, not a layering violation: the two tables are
    /// only ever mutated together for a segment's whole lifetime (see
    /// <see cref="SegmentTombstoneRepository"/>'s own doc comment -- both are "sibling" local-cache
    /// tables for the same segment id), they are just conventionally accessed through separate
    /// repository classes for single-row operations that do not need this cross-table atomicity.
    /// </para>
    ///
    /// <para>
    /// <b>Ordering within the transaction (delete, then insert-or-update):</b> the new segment's
    /// row is written using the exact same INSERT ... ON CONFLICT DO UPDATE shape as
    /// <see cref="UpsertManifestAsync"/> so that this method is also safe to call for a merge whose
    /// output happens to reuse an id already present (it never does in practice -- <c>IndexBuilder</c>
    /// always mints a fresh internal id per merge, and the caller always mints a fresh persisted
    /// Guid for it too -- but there is no reason to make this method load-bearing on that never
    /// happening). The retired ids' rows are deleted from both tables afterward within the same
    /// transaction; since SQLite transactions are all-or-nothing, the relative order of the insert
    /// vs. the deletes inside this one transaction has no observable effect on crash safety (only
    /// the transaction's own commit boundary does). The deletes are written first purely so the
    /// retired rows' file paths can be captured in the same block that removes them, immediately
    /// before the DELETE that makes them unreadable.
    /// </para>
    /// </summary>
    /// <param name="newSegment">
    /// The merge's output segment to install, or null if the merge's surviving-document set was
    /// empty (see <c>MergedSegmentPersistenceInfo.NewSegment</c>'s own doc comment) -- in that case
    /// this method only performs the retirement deletes, with no corresponding insert.
    /// </param>
    /// <param name="retiredSegmentIds">
    /// The persisted Guids of every input segment this merge consumed, whose manifest/tombstone
    /// rows are now moot and must be removed. May be empty (nothing to retire), though in practice
    /// a merge always has at least one input.
    /// </param>
    /// <returns>
    /// The file path recorded against each retired segment id, captured from its manifest row
    /// BEFORE that row is deleted -- the caller needs these to delete the now-unreferenced files
    /// from disk AFTER this transaction has durably committed (see the caller's own doc comment for
    /// why file deletion must happen strictly after, never before or during, this transaction).
    /// </returns>
    public async Task<List<string>> ReplaceMergedSegmentsAsync(SegmentManifestEntry? newSegment, IReadOnlyList<Guid> retiredSegmentIds)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var retiredFilePaths = new List<string>();
        if (retiredSegmentIds.Count > 0)
        {
            var idStrings = retiredSegmentIds.Select(id => id.ToString()).ToArray();

            // Captured BEFORE the delete below -- once the manifest row is gone, this is the only
            // place the file path was ever recorded.
            var rows = await conn.QueryAsync<string>(
                "SELECT file_path FROM tbl_search_index_manifest WHERE segment_id IN @ids",
                new { ids = idStrings }, transaction: tx);
            retiredFilePaths.AddRange(rows);

            await conn.ExecuteAsync(
                "DELETE FROM tbl_search_index_manifest WHERE segment_id IN @ids",
                new { ids = idStrings }, transaction: tx);
            await conn.ExecuteAsync(
                "DELETE FROM tbl_search_segment_tombstone WHERE segment_id IN @ids",
                new { ids = idStrings }, transaction: tx);
        }

        if (newSegment is not null)
        {
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
                    SegmentId = newSegment.SegmentId.ToString(),
                    newSegment.FilePath,
                    newSegment.DocCount,
                    newSegment.DekEpoch,
                    newSegment.FormatVersion,
                    newSegment.CreatedAt,
                },
                transaction: tx);
        }

        tx.Commit();
        return retiredFilePaths;
    }
}
