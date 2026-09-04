using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleBodyRepository
{
    Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId);
    Task<List<EncryptedArticleBody>> GetAllActiveAsync();

    /// <summary>
    /// Batch fetch of active bodies restricted to <paramref name="articleIds"/>, pushed into the
    /// SQL WHERE clause rather than filtered after the fact -- the primitive
    /// <see cref="BeeMemoryBank.Core.Services.SearchService.SearchWebContentAsync"/> uses for its
    /// "restricted to just the pending article ids" fallback scan: unlike
    /// <see cref="StreamActiveAsync"/> (which walks and blob-reads every active body in the vault),
    /// this only ever touches <c>tbl_blob</c> rows for the ids the caller actually asked for, which
    /// is the entire point of the fallback -- a handful of pending ids must cost a handful of blob
    /// reads, not a full-vault scan.
    /// <para>
    /// Deliberately a plain buffered list (like <see cref="IArticleRepository.GetByIdsAsync"/>),
    /// not a streamed <c>IAsyncEnumerable</c>: the streaming/bounded-channel machinery in
    /// <c>StreamActiveAsync</c> exists specifically to avoid materializing an UNKNOWN, vault-sized
    /// active set in memory. Here the caller already knows the exact (id-bounded) set it's asking
    /// for before the call is made, so there is nothing open-ended to bound. Returns an empty list
    /// without touching the database when <paramref name="articleIds"/> is empty.
    /// </para>
    /// </summary>
    Task<List<EncryptedArticleBody>> GetByArticleIdsAsync(IReadOnlyCollection<Guid> articleIds);
    Task UpsertAsync(EncryptedArticleBody body, IDbTransaction? transaction = null);
    /// <summary>
    /// Streams all active article bodies over a single, long-lived SQLite connection.
    /// Each yielded <see cref="EncryptedArticleBody"/> is materialized lazily as the reader
    /// advances, so the full active set is never buffered into one in-memory list. Because the
    /// whole read happens on one connection, SQLite WAL gives the caller a consistent snapshot
    /// of the active-body set for the lifetime of the enumeration — concurrent
    /// creates/soft-deletes on other connections cannot shift a row out from under this read
    /// (the failure mode of the former LIMIT/OFFSET batched approach).
    /// </summary>
    IAsyncEnumerable<EncryptedArticleBody> StreamActiveAsync(CancellationToken cancellationToken = default);
    /// <summary>Purges ciphertexts for soft-deleted articles older than cutoff.</summary>
    Task<int> PurgeForDeletedArticlesOlderThanAsync(DateTime cutoff);
}
