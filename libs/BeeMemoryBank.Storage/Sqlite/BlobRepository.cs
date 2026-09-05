using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Sqlite;

public class BlobRepository(DbConnectionFactory factory) : BaseRepository(factory), IBlobRepository
{
    // SQLite's default bound-parameter ceiling is 32766 on current builds but was 999 on older
    // ones; Dapper expands an IN (@hashes) list into one parameter per element. 500 keeps every
    // query far under either limit and the SQL text small enough not to matter.
    private const int InListChunk = 500;

    public async Task<string> StoreAsync(byte[] data, System.Data.IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            return await StoreOnAsync(conn, transaction, data);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    /// <summary>
    /// The one statement that puts bytes into tbl_blob — ArticleBodyRepository and
    /// ArticleVersionRepository call this too, so the row-referencing tables and the store never
    /// disagree on how a blob is written.
    ///
    /// On conflict the row is kept but its created_at is REFRESHED. That restarts the garbage
    /// collector's grace period for a blob that is being adopted again: a blob stored hours ago,
    /// since orphaned (its article deleted, its events compacted), is re-referenced by a new save of
    /// identical ciphertext. With a plain INSERT OR IGNORE its created_at would stay old, and a
    /// sweep landing between this statement and the row that references it — possible whenever the
    /// caller passes no transaction — would delete a blob about to be needed. With the refresh it
    /// is young again and immune for the full grace period.
    /// </summary>
    internal static async Task<string> StoreOnAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? transaction, byte[] data)
    {
        var hash = BlobHash.Compute(data);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_blob (hash, data, size, created_at)
              VALUES (@hash, @data, @size, @createdAt)
              ON CONFLICT (hash) DO UPDATE SET created_at = excluded.created_at",
            new { hash, data, size = data.LongLength, createdAt = UtcNow() }, transaction);
        return hash;
    }

    public async Task<byte[]?> GetAsync(string hash)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT data FROM tbl_blob WHERE hash = @hash", new { hash });
    }

    public async Task<HashSet<string>> GetExistingAsync(IReadOnlyCollection<string> hashes)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (hashes.Count == 0) return found;

        using var conn = OpenConnection();
        foreach (var chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(InListChunk))
        {
            var rows = await conn.QueryAsync<string>(
                "SELECT hash FROM tbl_blob WHERE hash IN @chunk", new { chunk });
            found.UnionWith(rows);
        }
        return found;
    }

    public async Task<List<StoredBlob>> GetManyAsync(IReadOnlyCollection<string> hashes, long byteBudget)
    {
        var result = new List<StoredBlob>();
        if (hashes.Count == 0) return result;

        using var conn = OpenConnection();
        long total = 0;
        var budgetHit = false;
        foreach (var chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(InListChunk))
        {
            // Sizes first, bytes second: deciding what fits from the 8-byte size column avoids
            // pulling megabytes of blob data off disk only to discard them past the budget.
            var sizes = await conn.QueryAsync<(string Hash, long Size)>(
                "SELECT hash, size FROM tbl_blob WHERE hash IN @chunk", new { chunk });

            var take = new List<string>();
            foreach (var (hash, size) in sizes)
            {
                // Always admit the first blob even if it alone exceeds the budget — otherwise a
                // caller paging through a list could never get past it.
                if (result.Count + take.Count > 0 && total + size > byteBudget)
                {
                    budgetHit = true;
                    break;
                }
                take.Add(hash);
                total += size;
            }

            if (take.Count > 0)
            {
                var rows = await conn.QueryAsync<(string Hash, byte[] Data)>(
                    "SELECT hash, data FROM tbl_blob WHERE hash IN @take", new { take });
                result.AddRange(rows.Select(r => new StoredBlob(r.Hash, r.Data)));
            }
            if (budgetHit) break;
        }
        return result;
    }

    public async Task<int> SweepUnreferencedAsync(DateTime createdBefore)
    {
        using var conn = OpenConnection();
        // BEGIN IMMEDIATE takes the write lock up front, so the reference scan and the delete see
        // one consistent state: with a deferred transaction, a writer committing a new reference
        // between the two would surface as SQLITE_BUSY_SNAPSHOT at best. The created_at cutoff
        // is the other half of the safety argument — a row younger than the grace period may be
        // referenced by a transaction that has not committed yet (or by an event that a peer has
        // shipped the blob for but not yet pushed), and is never a candidate.
        //
        // References counted: current bodies, version history, live media rows (item 16a — a media
        // row now points at its ciphertext blob by hash, and unlike an event that reference is not
        // removed by compaction, so the blob survives as long as the media does), and any event
        // payload carrying a ciphertext_sha256 — checked by JSON path rather than by event type, so
        // a future event kind that references a blob is covered without touching this query. Events
        // of a hard-deleted article keep its blobs alive until compaction removes the events, which
        // is the same retention the inline base64 copy had before the blob store existed.
        // tbl_conflict_version is not consulted: it still stores its ciphertext inline.
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var swept = await conn.ExecuteAsync(
                @"DELETE FROM tbl_blob
                  WHERE created_at < @cutoff
                    AND hash NOT IN (SELECT ciphertext_hash FROM tbl_article_body    WHERE ciphertext_hash IS NOT NULL)
                    AND hash NOT IN (SELECT ciphertext_hash FROM tbl_article_version WHERE ciphertext_hash IS NOT NULL)
                    AND hash NOT IN (SELECT ciphertext_sha256 FROM tbl_media          WHERE ciphertext_sha256 IS NOT NULL)
                    AND hash NOT IN (SELECT json_extract(payload, '$.ciphertext_sha256') FROM tbl_event
                                     WHERE json_extract(payload, '$.ciphertext_sha256') IS NOT NULL)",
                new { cutoff = createdBefore.ToUniversalTime().ToString("o") });
            await conn.ExecuteAsync("COMMIT");
            return swept;
        }
        catch
        {
            try { await conn.ExecuteAsync("ROLLBACK"); } catch { /* connection may already be out of the transaction */ }
            throw;
        }
    }

    public async Task<(long Count, long Bytes)> GetStatsAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleAsync<(long Count, long Bytes)>(
            "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM tbl_blob");
    }
}
