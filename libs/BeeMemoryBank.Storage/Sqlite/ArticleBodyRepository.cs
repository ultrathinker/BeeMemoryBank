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
        var body = await conn.QuerySingleOrDefaultAsync<EncryptedArticleBody>(
            // The bytes live in tbl_blob (migration 017 dropped the inline column). LEFT JOIN so a
            // row whose blob is gone still surfaces — as a clear error below, not as a silent
            // "no body" that a caller might treat as an empty article.
            @"SELECT
                b.article_id  AS ArticleId,
                bl.data       AS Ciphertext,
                b.iv          AS IV,
                b.encrypted_dek AS EncryptedDek,
                b.dek_iv      AS DekIV
              FROM tbl_article_body b
              LEFT JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
              WHERE b.article_id = @articleId",
            new { articleId });
        if (body is { Ciphertext: null })
            throw new InvalidOperationException(
                $"Article {articleId} has a body row but its ciphertext blob is missing from tbl_blob.");
        return body;
    }

    public async Task<List<EncryptedArticleBody>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        // INNER JOIN on the blob: a body whose bytes are gone is skipped rather than handed out
        // with a null ciphertext — these bulk readers (search indexing, rewrap) should carry on
        // past one broken row, and GetByArticleIdAsync is where that row reports itself.
        return (await conn.QueryAsync<EncryptedArticleBody>(
            @"SELECT b.article_id AS ArticleId,
                     bl.data AS Ciphertext,
                     b.iv AS IV, b.encrypted_dek AS EncryptedDek, b.dek_iv AS DekIV
              FROM tbl_article_body b
              JOIN tbl_article a ON a.id = b.article_id
              JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
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
            cmd.CommandText = @"SELECT b.article_id, bl.data,
                                       b.iv, b.encrypted_dek, b.dek_iv
                                FROM tbl_article_body b
                                JOIN tbl_article a ON a.id = b.article_id
                                JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash
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
            // referenced, or one younger than its grace period (StoreOnAsync refreshes the age of a
            // blob that already exists, which is what keeps that true for a re-adopted one).
            var hash = await BlobRepository.StoreOnAsync(conn, transaction, body.Ciphertext);

            await conn.ExecuteAsync(
                @"INSERT INTO tbl_article_body (article_id, ciphertext_hash, iv, encrypted_dek, dek_iv)
                  VALUES (@ArticleId, @CiphertextHash, @IV, @EncryptedDek, @DekIV)
                  ON CONFLICT (article_id) DO UPDATE SET
                    ciphertext_hash = excluded.ciphertext_hash,
                    iv              = excluded.iv,
                    encrypted_dek   = excluded.encrypted_dek,
                    dek_iv          = excluded.dek_iv",
                new { body.ArticleId, CiphertextHash = hash,
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
