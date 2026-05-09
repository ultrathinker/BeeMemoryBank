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
                     AND excluded.source_node_id IS NOT NULL
                     AND tbl_tombstone.source_node_id IS NOT NULL
                     AND excluded.source_node_id > tbl_tombstone.source_node_id)",
            tombstone);
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
