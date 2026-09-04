using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class TombstoneRepository(DbConnectionFactory factory) : BaseRepository(factory), ITombstoneRepository
{
    public async Task CreateAsync(Tombstone tombstone)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_tombstone (article_id, created_at, expires_at, lamport_ts, source_node_id)
              VALUES (@ArticleId, @CreatedAt, @ExpiresAt, @LamportTs, @SourceNodeId)
              ON CONFLICT(article_id) DO UPDATE SET
                lamport_ts = excluded.lamport_ts,
                created_at = excluded.created_at,
                expires_at = excluded.expires_at,
                source_node_id = excluded.source_node_id
              WHERE excluded.lamport_ts > tbl_tombstone.lamport_ts
                 OR (excluded.lamport_ts = tbl_tombstone.lamport_ts
                     AND COALESCE(excluded.source_node_id, @EmptyNodeId)
                       > COALESCE(tbl_tombstone.source_node_id, @EmptyNodeId))",
            new
            {
                tombstone.ArticleId,
                tombstone.CreatedAt,
                tombstone.ExpiresAt,
                tombstone.LamportTs,
                tombstone.SourceNodeId,
                // COALESCE rather than the previous pair of IS NOT NULL guards: those made the
                // upsert refuse a tie against a tombstone written before source tracking existed,
                // so one node kept the unattributed row and its peer took the attributed one — the
                // two then answered the create gate differently for the same event. Guid.Empty is
                // how RowVersion.Of reads a missing node id, and it sorts below every real one, so
                // this is the same rule the comparator applies, expressed in SQL.
                EmptyNodeId = Guid.Empty
            });
    }

    public async Task<bool> ExistsAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM tbl_tombstone WHERE article_id = @articleId",
            new { articleId }) > 0;
    }

    public async Task<Tombstone?> GetByEntityIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Tombstone>(
            @"SELECT article_id AS ArticleId, created_at AS CreatedAt,
                     expires_at AS ExpiresAt, lamport_ts AS LamportTs,
                     source_node_id AS SourceNodeId
              FROM tbl_tombstone WHERE article_id = @articleId",
            new { articleId });
    }

    public async Task<int> DeleteExpiredAsync(DateTime now)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_tombstone WHERE expires_at < @now",
            new { now });
    }
}
