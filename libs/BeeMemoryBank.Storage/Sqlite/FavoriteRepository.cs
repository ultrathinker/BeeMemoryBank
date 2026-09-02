using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class FavoriteRepository(DbConnectionFactory factory) : BaseRepository(factory), IFavoriteRepository
{
    public async Task<List<Favorite>> ListByUserAsync(int userId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Favorite>(
            @"SELECT user_id AS UserId, article_id AS ArticleId,
                     sort_order AS SortOrder, created_at AS CreatedAt
              FROM tbl_favorite WHERE user_id = @userId",
            new { userId })).ToList();
    }

    public async Task AddAsync(int userId, Guid articleId, int? sortOrder)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_favorite (user_id, article_id, sort_order, created_at)
              VALUES (@userId, @articleId, @sortOrder, @createdAt)
              ON CONFLICT(user_id, article_id) DO NOTHING",
            new { userId, articleId, sortOrder, createdAt = DateTime.UtcNow });
    }

    public async Task RemoveAsync(int userId, Guid articleId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "DELETE FROM tbl_favorite WHERE user_id = @userId AND article_id = @articleId",
            new { userId, articleId });
    }

    public async Task SetSortOrdersAsync(int userId, IReadOnlyList<(Guid ArticleId, int SortOrder)> positions)
    {
        if (positions.Count == 0) return;

        using var conn = OpenConnection();
        // One transaction: a half-written reorder would leave the list in the forbidden
        // "some rows positioned, some null" state, which reads as alphabetical again.
        using var trans = conn.BeginTransaction();
        foreach (var (articleId, sortOrder) in positions)
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_favorite SET sort_order = @sortOrder WHERE user_id = @userId AND article_id = @articleId",
                new { userId, articleId, sortOrder }, trans);
        }
        trans.Commit();
    }

    public async Task ClearSortOrdersAsync(int userId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_favorite SET sort_order = NULL WHERE user_id = @userId",
            new { userId });
    }
}
