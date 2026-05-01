using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleBodyRepository(DbConnectionFactory factory) : BaseRepository(factory), IArticleBodyRepository
{
    public async Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<EncryptedArticleBody>(
            @"SELECT
                article_id  AS ArticleId,
                ciphertext  AS Ciphertext,
                iv          AS IV,
                encrypted_dek AS EncryptedDek,
                dek_iv      AS DekIV
              FROM tbl_article_body WHERE article_id = @articleId",
            new { articleId });
    }

    public async Task<List<EncryptedArticleBody>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<EncryptedArticleBody>(
            @"SELECT b.article_id AS ArticleId, b.ciphertext AS Ciphertext,
                     b.iv AS IV, b.encrypted_dek AS EncryptedDek, b.dek_iv AS DekIV
              FROM tbl_article_body b
              JOIN tbl_article a ON a.id = b.article_id
              WHERE a.status = 'A'")).ToList();
    }

    public async Task<int> GetActiveCountAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM tbl_article_body b
              JOIN tbl_article a ON a.id = b.article_id
              WHERE a.status = 'A'");
    }

    public async Task<List<EncryptedArticleBody>> GetActiveBatchAsync(int limit, int offset)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<EncryptedArticleBody>(
            @"SELECT b.article_id AS ArticleId, b.ciphertext AS Ciphertext,
                     b.iv AS IV, b.encrypted_dek AS EncryptedDek, b.dek_iv AS DekIV
              FROM tbl_article_body b
              JOIN tbl_article a ON a.id = b.article_id
              WHERE a.status = 'A'
              ORDER BY b.article_id
              LIMIT @limit OFFSET @offset",
            new { limit, offset })).ToList();
    }

    public async Task UpsertAsync(EncryptedArticleBody body)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article_body (article_id, ciphertext, iv, encrypted_dek, dek_iv)
              VALUES (@ArticleId, @Ciphertext, @IV, @EncryptedDek, @DekIV)
              ON CONFLICT (article_id) DO UPDATE SET
                ciphertext    = excluded.ciphertext,
                iv            = excluded.iv,
                encrypted_dek = excluded.encrypted_dek,
                dek_iv        = excluded.dek_iv",
            body);
    }

    public async Task<int> PurgeForDeletedArticlesOlderThanAsync(DateTime cutoff)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"DELETE FROM tbl_article_body
              WHERE article_id IN (
                SELECT id FROM tbl_article
                WHERE status = 'D' AND deleted_at < @cutoff
              )",
            new { cutoff });
    }
}
