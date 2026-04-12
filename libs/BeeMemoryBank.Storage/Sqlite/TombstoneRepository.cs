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
            @"INSERT OR IGNORE INTO tbl_tombstone (article_id, created_at, expires_at)
              VALUES (@ArticleId, @CreatedAt, @ExpiresAt)",
            tombstone);
    }

    public async Task<bool> ExistsAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM tbl_tombstone WHERE article_id = @articleId",
            new { articleId }) > 0;
    }

    public async Task<int> DeleteExpiredAsync(DateTime now)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_tombstone WHERE expires_at < @now",
            new { now });
    }
}
