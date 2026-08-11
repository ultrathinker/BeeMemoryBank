using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleBodyRepository
{
    Task<EncryptedArticleBody?> GetByArticleIdAsync(Guid articleId);
    Task<List<EncryptedArticleBody>> GetAllActiveAsync();
    Task UpsertAsync(EncryptedArticleBody body);
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
