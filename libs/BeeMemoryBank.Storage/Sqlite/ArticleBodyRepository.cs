using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using System.Data.Common;
using System.Runtime.CompilerServices;

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

    /// <summary>
    /// Streams active article bodies over a single connection using an unbuffered
    /// <see cref="DbDataReader"/>. The connection stays open for the lifetime of the
    /// enumeration (caller must fully consume or dispose the async enumerable), which pins a
    /// consistent WAL snapshot for the whole read. See interface doc for why this matters.
    /// </summary>
    public async IAsyncEnumerable<EncryptedArticleBody> StreamActiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // OpenConnection() returns IDbConnection; the concrete instance is SqliteConnection,
        // which is a DbConnection (IAsyncDisposable) — cast to use the async reader API and to
        // get deterministic async disposal tied to the enumeration lifetime.
        var conn = (DbConnection)OpenConnection();
        await using (conn.ConfigureAwait(false))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT b.article_id, b.ciphertext, b.iv, b.encrypted_dek, b.dek_iv
                                FROM tbl_article_body b
                                JOIN tbl_article a ON a.id = b.article_id
                                WHERE a.status = 'A'";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new EncryptedArticleBody
                {
                    ArticleId = reader.GetGuid(0),
                    Ciphertext = (byte[])reader[1],
                    IV = (byte[])reader[2],
                    EncryptedDek = (byte[])reader[3],
                    DekIV = (byte[])reader[4]
                };
            }
        }
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
