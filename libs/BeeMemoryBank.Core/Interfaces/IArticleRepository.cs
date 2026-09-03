using System.Data;
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

    /// <summary>
    /// Every method here (and on the other article-write repositories:
    /// <c>IArticleBodyRepository</c>, <c>IArticleVersionRepository</c>, <c>IConceptTagRepository</c>,
    /// <c>IEventLogRepository</c>, <c>IMediaRepository</c>) that takes an optional
    /// <see cref="IDbTransaction"/> follows the same contract: pass null (the default) and the
    /// method opens, commits and disposes its own connection exactly as before — every existing
    /// caller is unaffected. Pass a non-null transaction and it executes against that transaction's
    /// connection WITHOUT committing or disposing anything — commit, rollback, and (for this
    /// interface) <see cref="InvalidateVectorCache"/> become the caller's responsibility, to be
    /// done once, after the caller's own transaction actually commits. See
    /// <c>ArticleService.UpdateCoreAsync</c>/<c>CreateAsync</c>/<c>DeleteAsync</c> for the intended
    /// caller pattern.
    /// </summary>
    Task CreateAsync(Article article, IDbTransaction? transaction = null);
    Task UpdateAsync(Article article, IDbTransaction? transaction = null);
    Task SoftDeleteAsync(Guid id, IDbTransaction? transaction = null);

    /// <summary>
    /// Invalidates the process-wide embedding vector cache. Call exactly once, after a transaction
    /// passed to <see cref="CreateAsync"/>/<see cref="UpdateAsync"/> actually commits — those
    /// methods do NOT invalidate the cache themselves when given a transaction, since invalidating
    /// before commit would repopulate the cache with pre-write data that nothing would ever
    /// invalidate again.
    /// </summary>
    void InvalidateVectorCache();
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

    /// <summary>
    /// Re-flags every active article whose stored <c>embedding_model_version</c> is present but
    /// does not match <paramref name="currentModelVersion"/> as embedding_pending = 1, so a model
    /// swap (e.g. MiniLM to multilingual-e5-small) gets picked up by
    /// <c>PendingEmbeddingProcessor</c> automatically instead of silently leaving stale-model
    /// vectors mixed in with new ones. Dimension-based staleness checks elsewhere don't catch this
    /// when the old and new models happen to share a dimension. Rows with no stored version at all
    /// are left alone -- they're already pending for the ordinary "never embedded yet" reason.
    /// Returns the number of rows re-flagged, for logging.
    /// </summary>
    Task<int> MarkStaleEmbeddingsPendingAsync(string currentModelVersion);

    /// <summary>
    /// Re-flags every active article as embedding_pending = 1. Used only by the projection-matrix
    /// recovery path in <c>EmbeddingProjectionService.EnsureProjectionMatrixAsync</c>: when the
    /// stored matrix can no longer be decrypted the matrix must be regenerated, and every stored
    /// projection was computed in the OLD matrix's space, so all of them have to be recomputed —
    /// leaving them would silently return nonsense similarity scores. Returns rows affected.
    /// </summary>
    Task<int> MarkAllEmbeddingsPendingAsync();

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

    /// <summary>WP-15: chunk-based semantic search — see <c>ArticleRepository.SearchByChunkEmbeddingAsync</c>'s doc comment.</summary>
    Task<List<Article>> SearchByChunkEmbeddingAsync(float[] queryProjection, int topK = 10);
    Task<List<Article>> GetRecentActivityAsync(int limit = 50);
    Task SetFolderIdAsync(Guid articleId, Guid folderId);
    Task ClearFolderIdAsync(Guid folderId);
    Task<List<(Guid Id, string TreePath)>> GetArticlesWithNullFolderIdAsync();
}
