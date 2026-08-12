using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IArticleRepository
{
    Task<Article?> GetByIdAsync(Guid id, bool includeDeleted = false);

    /// <summary>
    /// Unscoped fetch by id — returns the article regardless of caller scope.
    /// For internal service use when the caller intends to perform a write
    /// (the subsequent UpdateAsync/SoftDeleteAsync enforce scope and throw
    /// UnauthorizedAccessException on deny). Do NOT expose to HTTP endpoints.
    /// </summary>
    Task<Article?> GetByIdUnfilteredAsync(Guid id, bool includeDeleted = false);
    Task<List<Article>> ListAsync(string? treePath = null, DateTime? updatedAfter = null);
    Task CreateAsync(Article article);
    Task UpdateAsync(Article article);
    Task SoftDeleteAsync(Guid id);
    Task<List<Article>> SearchAsync(string query);
    /// <summary>
    /// Pre-WP-07 exact-substring search (per-row <c>unicode_contains</c> scan over title and tag
    /// name, no morphology). Preserved for a possible future "exact substring" search mode; not
    /// used by <c>SearchService</c>, which routes through FTS-backed <see cref="SearchAsync"/>.
    /// </summary>
    Task<List<Article>> SearchByExactSubstringAsync(string query);
    Task<List<Article>> SearchByIdPartialAsync(string partial, int limit = 20);
    Task<List<Article>> GetByIdsAsync(List<Guid> ids);
    Task<List<Article>> GetEmbeddingPendingAsync(int limit = 100);
    Task UpdateEmbeddingAsync(Guid id, byte[] projection, string modelVersion);

    /// <summary>WP-11: mirrors GetEmbeddingPendingAsync exactly, for the search-index background processor.</summary>
    Task<List<Article>> GetIndexPendingAsync(int limit = 100);

    /// <summary>WP-11: mirrors the embedding_pending = 0 clear inside UpdateEmbeddingAsync, without any projection payload to store.</summary>
    Task ClearIndexPendingAsync(Guid id);

    /// <summary>
    /// WP-11: re-flags every active article as index_pending = 1. Used only by the search-index
    /// full-rebuild path (a persisted segment failed to load and the whole persisted index is no
    /// longer trustworthy) -- returns the number of rows affected for logging.
    /// </summary>
    Task<int> MarkAllIndexPendingAsync();
    Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10);
    Task<List<Article>> GetRecentActivityAsync(int limit = 50);
    Task SetFolderIdAsync(Guid articleId, Guid folderId);
    Task ClearFolderIdAsync(Guid folderId);
    Task<List<(Guid Id, string TreePath)>> GetArticlesWithNullFolderIdAsync();
}
