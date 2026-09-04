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
    /// connection WITHOUT committing or disposing anything — commit and rollback become the caller's
    /// responsibility.
    ///
    /// <para>
    /// <b>Vector cache:</b> when given no transaction, both methods invalidate the embedding vector
    /// cache themselves after their own commit (a generic safety net for a hypothetical direct
    /// caller that sets <c>Article.EmbeddingProjection</c> to something new). When given a
    /// transaction, they do NOT — <see cref="InvalidateVectorCache"/> would then be the caller's
    /// responsibility, to be called once after the caller's own transaction commits. In practice
    /// neither of this codebase's two transactional callers
    /// (<c>ArticleService.CreateAsync</c>/<c>UpdateCoreAsync</c>, and
    /// <c>EventApplier.ApplyArticleCreateCoreAsync</c>/<c>ApplyArticleUpdateCoreAsync</c>) ever calls
    /// it, because neither ever sets <c>EmbeddingProjection</c> to anything other than what the row
    /// already carried — see <c>EmbeddingVectorCache</c>'s own doc comment for the full reasoning
    /// and why that's deliberate, not an oversight. A future transactional caller that DOES
    /// genuinely change projection bytes must call <see cref="InvalidateVectorCache"/> (or, if it has
    /// the new bytes in hand for exactly one row like <see cref="UpdateEmbeddingUnscopedAsync"/>
    /// does, prefer patching the cache directly the same way that method does).
    /// </para>
    /// </summary>
    Task CreateAsync(Article article, IDbTransaction? transaction = null);
    Task UpdateAsync(Article article, IDbTransaction? transaction = null);
    /// <summary>
    /// Marks the article deleted AND stamps <paramref name="version"/> onto the row.
    ///
    /// <para>
    /// The version is not optional and not derived here, because a soft-deleted row is still a
    /// replicated row: the applier compares incoming creates and updates against
    /// <c>tbl_article.lamport_ts</c>/<c>source_node_id</c>. Leaving those at the last EDIT's
    /// version — which is what this method used to do — means a peer edit older than the delete
    /// still wins the comparison and flips the row back to 'A'.
    /// </para>
    /// </summary>
    Task SoftDeleteAsync(Guid id, RowVersion version, IDbTransaction? transaction = null);

    /// <summary>
    /// Stamps <paramref name="version"/> onto a row that is already deleted, for when a SECOND
    /// delete of the same article turns out to supersede the one already recorded.
    ///
    /// <para>
    /// Two nodes deleting the same article independently is ordinary (two people tidying the same
    /// page), and each applies the other's delete against a row that is already 'D'. Without this,
    /// each keeps its own delete's version and the two rows disagree — so a later create or edit
    /// at the same Lamport is judged against a different node id on each node and lands on one but
    /// not the other. Converging on the winning delete is what stops that.
    /// </para>
    /// </summary>
    Task SetDeleteVersionAsync(Guid id, RowVersion version);

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

    /// <summary>
    /// Writes the embedding projection directly and clears embedding_pending, with NO caller-scope
    /// check of its own — the "Unscoped" suffix is the load-bearing part of the name, not
    /// decoration: it exists so a call site reads as a deliberate choice instead of an oversight.
    /// Only ever reachable from <c>PendingEmbeddingProcessor</c> (background worker,
    /// SystemCallerScope, where every scope check would be a no-op anyway). If a future HTTP
    /// endpoint needs this, add a scope check at the endpoint AND rename the call site's intent
    /// back into a guarded wrapper — do not just call this directly.
    /// </summary>
    Task UpdateEmbeddingUnscopedAsync(Guid id, byte[] projection, string modelVersion);

    /// <summary>
    /// Re-flags every active article whose stored <c>embedding_model_version</c> is present but
    /// does not match <paramref name="currentModelVersion"/> as embedding_pending = 1, so a model
    /// swap (e.g. MiniLM to multilingual-e5-small) gets picked up by
    /// <c>PendingEmbeddingProcessor</c> automatically instead of silently leaving stale-model
    /// vectors mixed in with new ones. Dimension-based staleness checks elsewhere don't catch this
    /// when the old and new models happen to share a dimension. Rows with no stored version at all
    /// are left alone -- they're already pending for the ordinary "never embedded yet" reason.
    /// Returns the number of rows re-flagged, for logging.
    ///
    /// "Unscoped": no caller-scope check, by design — reachable only from
    /// <c>PendingEmbeddingProcessor</c> (background worker, SystemCallerScope).
    /// </summary>
    Task<int> MarkStaleEmbeddingsPendingUnscopedAsync(string currentModelVersion);

    /// <summary>
    /// Re-flags every active article as embedding_pending = 1. Used only by the projection-matrix
    /// recovery path in <c>EmbeddingProjectionService.EnsureProjectionMatrixAsync</c>: when the
    /// stored matrix can no longer be decrypted the matrix must be regenerated, and every stored
    /// projection was computed in the OLD matrix's space, so all of them have to be recomputed —
    /// leaving them would silently return nonsense similarity scores. Returns rows affected.
    ///
    /// "Unscoped": no caller-scope check, by design — reachable only from
    /// <c>EmbeddingProjectionService</c>'s matrix-recovery path (background, SystemCallerScope).
    /// </summary>
    Task<int> MarkAllEmbeddingsPendingUnscopedAsync();

    /// <summary>WP-11: mirrors GetEmbeddingPendingAsync exactly, for the search-index background processor.</summary>
    Task<List<Article>> GetIndexPendingAsync(int limit = 100);

    /// <summary>
    /// WP-11: mirrors the embedding_pending = 0 clear inside <see cref="UpdateEmbeddingUnscopedAsync"/>,
    /// without any projection payload to store. "Unscoped": no caller-scope check, by design —
    /// reachable only from <c>PendingIndexProcessor</c> (background worker, SystemCallerScope).
    /// </summary>
    Task ClearIndexPendingUnscopedAsync(Guid id);

    /// <summary>
    /// WP-11: re-flags every active article as index_pending = 1. Used only by the search-index
    /// full-rebuild path (a persisted segment failed to load and the whole persisted index is no
    /// longer trustworthy) -- returns the number of rows affected for logging.
    ///
    /// "Unscoped": no caller-scope check, by design — reachable only from
    /// <c>SearchIndexLifecycleService.TriggerFullRebuildAsync</c> (background, SystemCallerScope).
    /// </summary>
    Task<int> MarkAllIndexPendingUnscopedAsync();

    /// <summary>
    /// Returns the bare ids of every active article still awaiting search-index ingestion
    /// (index_pending = 1) -- with NO batch limit, unlike <see cref="GetIndexPendingAsync"/> (which
    /// is deliberately batch-capped for <c>PendingIndexProcessor</c>'s incremental background work).
    /// <see cref="SearchService.SearchWebContentAsync"/> needs the FULL backlog, not one batch of
    /// it, to know exactly which articles the ranked BM25 index cannot yet be trusted for.
    ///
    /// "Unscoped": no caller-scope check of its own, same convention as the other
    /// <c>*IndexPendingUnscopedAsync</c>/<c>*IndexPendingAsync</c> members on this interface --
    /// but note this one only ever hands out bare <see cref="Guid"/> values, never row content, and
    /// its one caller intersects the result with its own caller-visible id set (via
    /// <c>ListAsync</c>/<c>FilterArticles</c>) before any article body is touched, so the missing
    /// check here is closed before any ciphertext is read, not merely skipped.
    /// </summary>
    Task<List<Guid>> GetIndexPendingIdsUnscopedAsync(int limit);
    Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10);

    /// <summary>WP-15: chunk-based semantic search — see <c>ArticleRepository.SearchByChunkEmbeddingAsync</c>'s doc comment.</summary>
    Task<List<Article>> SearchByChunkEmbeddingAsync(float[] queryProjection, int topK = 10);
    Task<List<Article>> GetRecentActivityAsync(int limit = 50);

    /// <summary>
    /// Sets an article's folder_id directly, with NO caller-scope check of its own. Marked
    /// <see langword="internal"/> (see the <c>InternalsVisibleTo</c> grants on
    /// BeeMemoryBank.Core's project file) rather than merely documented, so the compiler — not a
    /// comment someone has to notice — keeps this off the API/MCP surface: only
    /// BeeMemoryBank.Storage (the implementation, and <c>FolderBootstrapper</c>'s startup
    /// migration) and BeeMemoryBank.Sync (sync replay) can see this member at all.
    /// </summary>
    internal Task SetFolderIdUnscopedAsync(Guid articleId, Guid folderId);

    /// <summary>
    /// Detaches every article under a folder by setting folder_id = NULL and tree_path = '/', with
    /// NO caller-scope check of its own — this is the exact method a user-facing folder-delete path
    /// once called directly, relocating ACL-denied articles to the vault root instead of being
    /// blocked. Marked <see langword="internal"/> for the same reason as
    /// <see cref="SetFolderIdUnscopedAsync"/>: the compiler, not a comment, keeps it off the
    /// API/MCP surface. The two remaining callers are safe DESPITE the missing check:
    /// <c>FolderService.DeleteAsync</c> (Core, same assembly) calls
    /// <c>folderRepo.SoftDeleteByPathPrefixAsync</c> first, which walks every descendant and throws
    /// on the first ACL violation, so a denied descendant aborts the whole cascade before this is
    /// ever reached; <c>EventApplier.Folder</c> (Sync, granted access via InternalsVisibleTo) runs
    /// under SystemCallerScope, where every check would be a no-op anyway. Do not add a new call
    /// site ahead of an equivalent guard.
    /// </summary>
    internal Task ClearFolderIdUnscopedAsync(Guid folderId);
    Task<List<(Guid Id, string TreePath)>> GetArticlesWithNullFolderIdAsync();
}
