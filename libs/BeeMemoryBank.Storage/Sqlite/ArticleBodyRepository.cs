using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
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
            // COALESCE(blob, inline): during the expand phase both are populated, and rows written
            // before migration 016 have only the inline column. Reading the blob first means the
            // contract migration can drop the inline column without touching this query again.
            @"SELECT
                b.article_id  AS ArticleId,
                COALESCE(bl.data, b.ciphertext) AS Ciphertext,
                b.iv          AS IV,
                b.encrypted_dek AS EncryptedDek,
                b.dek_iv      AS DekIV
              FROM tbl_article_body b
              LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
              WHERE b.article_id = @articleId",
            new { articleId });
    }

    public async Task<List<EncryptedArticleBody>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<EncryptedArticleBody>(
            @"SELECT b.article_id AS ArticleId,
                     COALESCE(bl.data, b.ciphertext) AS Ciphertext,
                     b.iv AS IV, b.encrypted_dek AS EncryptedDek, b.dek_iv AS DekIV
              FROM tbl_article_body b
              JOIN tbl_article a ON a.id = b.article_id
              LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
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
            cmd.CommandText = @"SELECT b.article_id, COALESCE(bl.data, b.ciphertext),
                                       b.iv, b.encrypted_dek, b.dek_iv
                                FROM tbl_article_body b
                                JOIN tbl_article a ON a.id = b.article_id
                                LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
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

    public async Task UpsertAsync(EncryptedArticleBody body, System.Data.IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            // The blob goes in FIRST and in the same transaction as the row that references it.
            // Order matters for the garbage collector: it only ever sees a blob that is already
            // referenced, or one younger than its grace period. INSERT OR IGNORE because the hash
            // is the identity — re-saving identical ciphertext is a no-op, not a conflict.
            var hash = BlobHash.Compute(body.Ciphertext);
            await conn.ExecuteAsync(
                @"INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at)
                  VALUES (@hash, @data, @size, @createdAt)",
                new { hash, data = body.Ciphertext, size = body.Ciphertext.LongLength,
                      createdAt = DateTime.UtcNow.ToString("o") }, transaction);

            // Expand phase: the inline ciphertext is still written so the previous binary keeps
            // working against a migrated database. Migration 017 drops the column and this
            // parameter with it.
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_body (article_id, ciphertext, ciphertext_hash, iv, encrypted_dek, dek_iv)
                  VALUES (@ArticleId, @Ciphertext, @CiphertextHash, @IV, @EncryptedDek, @DekIV)
                  ON CONFLICT (article_id) DO UPDATE SET
                    ciphertext      = excluded.ciphertext,
                    ciphertext_hash = excluded.ciphertext_hash,
                    iv              = excluded.iv,
                    encrypted_dek   = excluded.encrypted_dek,
                    dek_iv          = excluded.dek_iv",
                new { body.ArticleId, body.Ciphertext, CiphertextHash = hash,
                      body.IV, body.EncryptedDek, body.DekIV }, transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
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
