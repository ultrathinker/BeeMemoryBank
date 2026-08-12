using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Search;

/// <summary>
/// WP-11 (Gap 2): local-only durable bookkeeping for search-index segment tombstones
/// (tbl_search_segment_tombstone), a sibling table to <see cref="SegmentManifestRepository"/>'s
/// tbl_search_index_manifest. <see cref="Indexing.IndexBuilder"/> (in
/// <c>BeeMemoryBank.Search</c>) tracks tombstones purely in memory
/// (<c>SealedSegment.Tombstones</c>); this repository is where a caller that also persists
/// segments to disk (<c>EncryptedSegmentStore</c>) durably records which articleIds have been
/// tombstoned in which persisted segment, so a process restart does not resurrect stale content
/// that was tombstoned but never re-persisted.
///
/// <para>
/// Local-only cache metadata exactly like the manifest: never synced, never authoritative, safe
/// to lose (a missing row just means one stale result might transiently reappear until the next
/// full index rebuild -- not silent data corruption).
/// </para>
/// </summary>
public sealed class SegmentTombstoneRepository(DbConnectionFactory factory) : BaseRepository(factory)
{
    /// <summary>
    /// Records that <paramref name="articleId"/>'s occurrence in <paramref name="segmentId"/> is
    /// stale. Idempotent (INSERT OR IGNORE): re-tombstoning the same (segment, article) pair --
    /// e.g. a retry after a transient failure -- is a harmless no-op, not an error.
    /// </summary>
    public async Task AddAsync(Guid segmentId, Guid articleId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO tbl_search_segment_tombstone (segment_id, article_id) VALUES (@segmentId, @articleId)",
            new { segmentId = segmentId.ToString(), articleId = articleId.ToString() });
    }

    /// <summary>
    /// Returns every articleId durably tombstoned for <paramref name="segmentId"/>. Used by the
    /// unlock warm-start path when reconstructing a <c>SealedSegment</c>-equivalent for a segment
    /// reloaded from disk (see <c>IndexBuilder.AdoptPersistedSegment</c>).
    /// </summary>
    public async Task<HashSet<Guid>> GetForSegmentAsync(Guid segmentId)
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<string>(
            "SELECT article_id FROM tbl_search_segment_tombstone WHERE segment_id = @segmentId",
            new { segmentId = segmentId.ToString() });
        return rows.Select(Guid.Parse).ToHashSet();
    }

    /// <summary>Deletes every tombstone row for one segment (e.g. after a merge makes them moot).</summary>
    public async Task DeleteForSegmentAsync(Guid segmentId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_search_segment_tombstone WHERE segment_id = @segmentId",
            new { segmentId = segmentId.ToString() });
    }

    /// <summary>WP-11: clears every tombstone row. Used only by the search-index full-rebuild path.</summary>
    public async Task DeleteAllAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_search_segment_tombstone");
    }
}
