using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;
using System.Diagnostics;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleRepository(
    DbConnectionFactory factory,
    CallerScopeHolder scopeHolder,
    EmbeddingVectorCache? vectorCache = null,
    SearchMetrics? searchMetrics = null,
    ChunkEmbeddingVectorCache? chunkCache = null) : BaseRepository(factory), IArticleRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;

    // WP-18: optional search-metrics recorder (DI injects the process-wide singleton; direct `new`
    // callers -- tests -- get null and recording is a no-op). Records only timings + coarse result
    // counts for semantic search; the query projection vector is never handed to it.
    private readonly SearchMetrics? _searchMetrics = searchMetrics;

    // WP-14: embedding vector cache. DI injects the shared singleton; direct `new` callers (tests)
    // get a per-instance cache when they omit the argument. The cache is the only state this
    // otherwise-stateless, scoped repository carries, and it is process-wide by design so an
    // embedding write in one scope invalidates the cache every other scope's next search sees.
    private readonly EmbeddingVectorCache _vectorCache = vectorCache ?? new EmbeddingVectorCache(factory);

    // WP-15: chunk-embedding cache, same optional-DI/per-instance-fallback pattern as _vectorCache.
    private readonly ChunkEmbeddingVectorCache _chunkCache = chunkCache ?? new ChunkEmbeddingVectorCache(factory);
    private const string SelectCols = @"
        a.id              AS Id,
        a.title           AS Title,
        COALESCE(f.path, '/') AS TreePath,
        a.folder_id       AS FolderId,
        a.embedding_projection     AS EmbeddingProjection,
        a.embedding_model_version  AS EmbeddingModelVersion,
        a.embedding_pending        AS EmbeddingPending,
        a.index_pending            AS IndexPending,
        a.status          AS Status,
        a.lamport_ts      AS LamportTs,
        a.source_node_id  AS SourceNodeId,
        a.created_at      AS CreatedAt,
        a.updated_at      AS UpdatedAt,
        a.deleted_at      AS DeletedAt,
        a.remote_subscription_id AS RemoteSubscriptionId,
        a.remote_origin_id       AS RemoteOriginId,
        a.remote_version         AS RemoteVersion,
        a.remote_updated_by      AS RemoteUpdatedBy,
        a.protected              AS Protected,
        a.protection_hint        AS ProtectionHint";

    private const string FromClause = "FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id";

    public async Task<Article?> GetByIdAsync(Guid id, bool includeDeleted = false)
    {
        var article = await GetByIdUnfilteredAsync(id, includeDeleted);
        if (article == null) return null;
        if (_holder.Scope.IsAccessDenied(article.TreePath)) return null;
        return article;
    }

    public async Task<Article?> GetByIdUnfilteredAsync(Guid id, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? $"SELECT {SelectCols} {FromClause} WHERE a.id = @id"
            : $"SELECT {SelectCols} {FromClause} WHERE a.id = @id AND a.status = 'A'";
        return await conn.QuerySingleOrDefaultAsync<Article>(sql, new { id });
    }

    public async Task<List<Article>> ListAsync(string? treePath = null, DateTime? updatedAfter = null)
    {
        using var conn = OpenConnection();
        string sql;
        object? param;
        var updatedAfterClause = updatedAfter.HasValue ? "AND a.updated_at > @updatedAfter " : "";

        if (treePath == null)
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' {updatedAfterClause}ORDER BY f.path, (substr(a.title,1,1)='_') DESC, a.title";
            param = new { updatedAfter };
        }
        else if (treePath == "/")
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = '/' OR a.folder_id IS NULL) {updatedAfterClause}ORDER BY (substr(a.title,1,1)='_') DESC, a.title";
            param = new { updatedAfter };
        }
        else
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = @treePath OR f.path LIKE @prefix ESCAPE '\\') {updatedAfterClause}ORDER BY f.path, (substr(a.title,1,1)='_') DESC, a.title";
            var escapedPrefix = treePath.TrimEnd('/').Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%";
            param = new { treePath, prefix = escapedPrefix, updatedAfter };
        }

        var articles = (await conn.QueryAsync<Article>(sql, param)).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    public async Task<List<Article>> SearchAsync(string query)
    {
        // WP-07: FTS5-backed search. The query string is run through the same
        // DefaultTokenizer/DefaultStemmer pipeline the index is designed around, then each stem
        // is emitted as a quoted prefix token (see FtsQueryBuilder). Empty/whitespace-only input
        // has no usable terms and returns an empty result — a deliberate change from the old
        // unicode_contains("") path, which (because "".Contains("") is true) returned the entire
        // active corpus; an empty search box returning everything is not useful behavior.
        var matchExpr = FtsQueryBuilder.BuildMatchExpression(query);
        if (matchExpr == null)
        {
            return [];
        }

        using var conn = OpenConnection();

        // Two match sources, UNIONed by article id:
        //   * fts_article  — title + tree_path hits, ranked by bm25(title weighted above path).
        //   * fts_tag       — concept-tag hits joined through tbl_article_concept_tag.
        // Soft-deleted rows stay in the FTS index (the delete trigger only fires on a real
        // DELETE, not a status-flip UPDATE), so every join back to tbl_article re-applies
        // status = 'A' — same filter the old non-FTS code applied, now load-bearing here.
        //
        // Ranking: title/path hits (tier 0) rank above tag-only hits (tier 1). Within tier 0,
        // bm25 ASC (more negative = better) orders by relevance. Tag-only hits get a fixed score
        // (0.0) since bm25(fts_article) is undefined for them — they sort after every title hit
        // because every real bm25 value is non-positive. The underscore-prefix-sorts-first quirk
        // stays the PRIMARY key so system/pinned titles (e.g. "_Drafts/...") continue to surface
        // at the top, matching every other listing query in the codebase.
        var results = (await conn.QueryAsync<Article>(
            $@"WITH
               title_hits AS (
                 SELECT art.id AS id, bm25(fts_article, 10.0, 2.0) AS score
                 FROM fts_article
                 JOIN tbl_article art ON art.rowid = fts_article.rowid
                 WHERE fts_article MATCH @matchExpr AND art.status = 'A'
               ),
               tag_hits AS (
                 SELECT DISTINCT art.id AS id
                 FROM fts_tag
                 JOIN tbl_article_concept_tag act ON act.concept_tag_id = fts_tag.rowid
                 JOIN tbl_article art ON art.id = act.article_id
                 WHERE fts_tag MATCH @matchExpr AND art.status = 'A'
               ),
               matched AS (
                 SELECT m.id AS id,
                        CASE WHEN th.id IS NOT NULL THEN 0 ELSE 1 END AS tier,
                        COALESCE(th.score, 0.0) AS score
                 FROM (SELECT id FROM title_hits UNION SELECT id FROM tag_hits) m
                 LEFT JOIN title_hits th ON th.id = m.id
               )
               SELECT {SelectCols} {FromClause}
               JOIN matched mm ON mm.id = a.id
               WHERE a.status = 'A'
               ORDER BY (substr(a.title,1,1)='_') DESC, mm.tier ASC, mm.score ASC, a.title",
            new { matchExpr })).ToList();

        return _holder.Scope.FilterArticles(results);
    }

    /// <summary>
    /// The pre-WP-07 <see cref="SearchAsync"/> implementation: a per-row managed-code
    /// <c>unicode_contains</c> substring scan over title and tag name, no morphology. Kept
    /// available (currently unused by <c>SearchService</c>) for a possible future "exact
    /// substring" search mode. Wiring a UI/API toggle for it is out of WP-07's scope.
    /// </summary>
    public async Task<List<Article>> SearchByExactSubstringAsync(string query)
    {
        using var conn = OpenConnection();

        // The tag match runs as an `id IN (subquery)` rather than a JOIN + DISTINCT on the
        // outer query: joining tbl_article_concept_tag/tbl_concept_tag directly here would
        // multiply each article into one row per matching tag, and DISTINCT would then have to
        // byte-compare full rows (including the embedding_projection BLOB) to dedupe. Filtering
        // by id membership instead means the outer SELECT touches each matching article exactly
        // once, with no BLOB comparison involved.
        var byTitle = (await conn.QueryAsync<Article>(
            $@"SELECT {SelectCols} {FromClause}
               WHERE a.status = 'A'
                 AND a.id IN (
                   SELECT a2.id FROM tbl_article a2
                   LEFT JOIN tbl_article_concept_tag act ON act.article_id = a2.id
                   LEFT JOIN tbl_concept_tag ct ON ct.id = act.concept_tag_id
                   WHERE a2.status = 'A' AND (unicode_contains(a2.title, @query) OR unicode_contains(ct.name, @query))
                 )
               ORDER BY (substr(a.title,1,1)='_') DESC, a.title",
            new { query })).ToList();

        return _holder.Scope.FilterArticles(byTitle);
    }

    public async Task<List<Article>> SearchByIdPartialAsync(string partial, int limit = 20)
    {
        using var conn = OpenConnection();
        var escaped = partial.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = "%" + escaped + "%";
        var articles = (await conn.QueryAsync<Article>(
            $@"SELECT {SelectCols} {FromClause}
               WHERE a.status = 'A' AND a.id LIKE @pattern ESCAPE '\'
               ORDER BY (substr(a.title,1,1)='_') DESC, a.title
               LIMIT @limit",
            new { pattern, limit })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    public async Task<List<Article>> GetByIdsAsync(List<Guid> ids)
    {
        if (ids.Count == 0) return [];
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.id IN @ids AND a.status = 'A' ORDER BY (substr(a.title,1,1)='_') DESC, a.title",
            new { ids })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    public void InvalidateVectorCache() => _vectorCache.Invalidate();

    public async Task CreateAsync(Article article, System.Data.IDbTransaction? transaction = null)
    {
        // Repo-level write guard: close the "new endpoint forgets manual ACL check" hole.
        if (_holder.Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException($"Write access denied for path '{article.TreePath}'");
        if (_holder.Scope.IsReadOnly(article.TreePath))
            throw new ReadOnlyAccessException(article.TreePath);

        const string insertSql = @"INSERT INTO tbl_article
              (id, title, tree_path, folder_id, embedding_projection, embedding_model_version, embedding_pending, index_pending,
               status, lamport_ts, source_node_id, created_at, updated_at,
               remote_subscription_id, remote_origin_id, remote_version, remote_updated_by,
               protected, protection_hint)
              VALUES (@Id, @Title, @TreePath, @FolderId, @EmbeddingProjection, @EmbeddingModelVersion, @EmbeddingPending, @IndexPending,
                      @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt,
                      @RemoteSubscriptionId, @RemoteOriginId, @RemoteVersion, @RemoteUpdatedBy,
                      @Protected, @ProtectionHint)";

        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(insertSql, article, transaction);
        }
        else
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            await conn.ExecuteAsync(insertSql, article, tx);

            tx.Commit();

            // WP-14: a brand-new row may carry an embedding_projection (sync create, or a create with
            // an inline projection), so the cache must be rebuilt on next search to pick it up.
            _vectorCache.Invalidate();
        }
    }

    public async Task UpdateAsync(Article article, System.Data.IDbTransaction? transaction = null)
    {
        if (_holder.Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException($"Write access denied for path '{article.TreePath}'");
        if (_holder.Scope.IsReadOnly(article.TreePath))
            throw new ReadOnlyAccessException(article.TreePath);

        const string updateSql = @"UPDATE tbl_article
              SET title = @Title, tree_path = @TreePath, folder_id = @FolderId,
                  embedding_projection = @EmbeddingProjection,
                  embedding_model_version = @EmbeddingModelVersion,
                  embedding_pending = @EmbeddingPending,
                  index_pending = @IndexPending,
                  lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt,
                  status = @Status,
                  deleted_at = CASE WHEN @Status = 'A' THEN NULL ELSE deleted_at END,
                  remote_version = @RemoteVersion,
                  remote_updated_by = @RemoteUpdatedBy,
                  protected = @Protected,
                  protection_hint = @ProtectionHint
              WHERE id = @Id";

        if (transaction != null)
        {
            // Caller-supplied transaction: the guard and the write already share the
            // caller's connection, so just run them against it in order -- unchanged
            // from before this fix.
            if (!_holder.Scope.IsSuperadmin)
            {
                await CheckStoredPathGuardAsync(transaction.Connection!, transaction, article);
            }
            await transaction.Connection!.ExecuteAsync(updateSql, article, transaction);
        }
        else
        {
            // SECURITY: the pre-update stored-path guard (CheckStoredPathGuardAsync) reads
            // the article's CURRENT path, and the UPDATE below is only safe to run under
            // the authorization that read produced if NOTHING can move the row between the
            // two. This used to open a short-lived connection for the guard, read, and
            // dispose it BEFORE the UPDATE even began on a second, independent
            // connection/transaction -- a concurrent UpdateAsync/move for the same article
            // could land in that gap, and this call would then write against a stored path
            // that was already stale by the time its own transaction opened.
            //
            // BeginTransaction() on this provider issues BEGIN IMMEDIATE, which takes
            // SQLite's write lock the instant the transaction opens (see
            // DbConnectionFactory.CreateConnection). Opening the connection+transaction
            // FIRST and running BOTH the guard query and the UPDATE against it closes that
            // window: any other writer touching this article row blocks on the write lock
            // until this transaction commits or rolls back, so the stored path the guard
            // just verified is guaranteed to still be the path the UPDATE acts on. Do not
            // split these back onto separate connections "to simplify" -- that's exactly
            // what reopened the race before. Keep everything between BeginTransaction() and
            // Commit() cheap: the write lock is held for the whole span.
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            if (!_holder.Scope.IsSuperadmin)
            {
                await CheckStoredPathGuardAsync(conn, tx, article);
            }

            // Note: no `status = 'A'` filter — must allow resurrecting soft-deleted rows
            // when LWW says incoming Create wins over an older Delete (Wave 2 audit
            // claude-A #7). The caller sets Status explicitly via the @Status param.
            // deleted_at is reset when transitioning back to 'A'.
            await conn.ExecuteAsync(updateSql, article, tx);

            tx.Commit();

            // WP-14: UpdateAsync rewrites embedding_projection with whatever the Article model carries.
            // Every current caller load-then-modify-then-save (so the bytes are usually unchanged), but
            // the column is written unconditionally here and a future caller could change it -- bumping
            // unconditionally keeps the "cache never silently goes stale" guarantee simple at the cost
            // of a redundant rebuild on metadata-only edits. See the WP-14 report for the tradeoff.
            _vectorCache.Invalidate();
        }
    }

    /// <summary>
    /// SECURITY: we ALSO need to check the article's CURRENT (pre-update) path. Without this, a
    /// caller with write permission on /Public could call UpdateAsync with article.Id pointing at
    /// a /Secrets article and article.TreePath = "/Public" — the ordinary guard at the top of
    /// <see cref="UpdateAsync"/> only sees the NEW path and passes, and the row would be moved
    /// (with full plaintext attached) into the caller's reach. Gemini security review 2026-05-25.
    ///
    /// Must run against the SAME connection AND transaction as the UPDATE it protects -- see the
    /// SECURITY comment in the self-managed branch of <see cref="UpdateAsync"/> for why a
    /// separate connection here would reopen the TOCTOU race. The mirrored-share guard is in the
    /// same SELECT so we avoid a second round-trip.
    /// </summary>
    private async Task CheckStoredPathGuardAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? transaction, Article article)
    {
        var stored = await conn.QuerySingleOrDefaultAsync<ArticleUpdateGuardMeta>(
            @"SELECT COALESCE(f.path, '/') AS StoredTreePath,
                     a.remote_subscription_id AS RemoteSubId
                FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
               WHERE a.id = @id",
            new { id = article.Id }, transaction: transaction);
        if (stored != null)
        {
            if (!string.IsNullOrEmpty(stored.RemoteSubId))
                throw new ReadOnlyAccessException($"Article {article.Id} is in a remote read-only share.");
            if (!string.IsNullOrEmpty(stored.StoredTreePath))
            {
                if (_holder.Scope.IsAccessDenied(stored.StoredTreePath))
                    throw new UnauthorizedAccessException(
                        $"Write access denied for stored path '{stored.StoredTreePath}' (cannot move article you don't own).");
                if (_holder.Scope.IsReadOnly(stored.StoredTreePath))
                    throw new ReadOnlyAccessException(stored.StoredTreePath);
            }
        }
    }

    public async Task SoftDeleteAsync(Guid id, System.Data.IDbTransaction? transaction = null)
    {
        if (transaction != null)
        {
            // Caller-supplied transaction: guard and write already share the caller's
            // connection, so just run them against it in order -- unchanged from before
            // this fix.
            if (!_holder.Scope.IsSuperadmin)
            {
                await CheckSoftDeleteGuardAsync(transaction.Connection!, transaction, id);
            }
            var now = UtcNow();
            await transaction.Connection!.ExecuteAsync(
                "UPDATE tbl_article SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id AND status = 'A'",
                new { id, now }, transaction);
            return;
        }

        // SECURITY: same TOCTOU shape as the stored-path guard in UpdateAsync above (not
        // called out in the original review comment, but identical bug): the pre-delete
        // ACL/read-only/remote-share guard (CheckSoftDeleteGuardAsync) reads this article's
        // CURRENT path, and that read only stays valid for the UPDATE below if nothing else
        // can move or reassign the row in the meantime. This used to run the guard on its
        // own short-lived connection and then the UPDATE on a second connection with NO
        // explicit transaction at all -- a concurrent move could land in the gap and this
        // call would delete/keep based on a stale path. Opening the connection+transaction
        // FIRST (BEGIN IMMEDIATE takes the write lock immediately, see
        // DbConnectionFactory.CreateConnection) and running both the guard query and the
        // UPDATE against it closes that window the same way UpdateAsync's fix does. Keep
        // the span between BeginTransaction() and Commit() cheap -- the write lock is held
        // for the whole span.
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        if (!_holder.Scope.IsSuperadmin)
        {
            await CheckSoftDeleteGuardAsync(conn, tx, id);
        }

        var nowTs = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id AND status = 'A'",
            new { id, now = nowTs }, tx);

        tx.Commit();
    }

    /// <summary>
    /// GetByIdAsync respects ambient scope (returns null if denied), so this reads the raw
    /// path/remote-share metadata directly and enforces denial explicitly. Must run against the
    /// SAME connection AND transaction as the UPDATE it protects -- see the SECURITY comment in
    /// the self-managed branch of <see cref="SoftDeleteAsync"/> for why.
    /// </summary>
    private async Task CheckSoftDeleteGuardAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? transaction, Guid id)
    {
        // Dapper maps ValueTuple by position (Item1/Item2), not by alias, so
        // `(string?, string?)` would receive nulls regardless of SELECT — the
        // ACL/Read-only guard would silently no-op. Use a dedicated record so
        // properties bind by name. Caught by Claude+gemini third review.
        var meta = await conn.QuerySingleOrDefaultAsync<ArticleDeleteMeta>(
            @"SELECT COALESCE(f.path, '/') AS TreePath, a.remote_subscription_id AS RemoteSubId
              FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
              WHERE a.id = @id",
            new { id }, transaction: transaction);
        if (meta != null)
        {
            if (!string.IsNullOrEmpty(meta.TreePath))
            {
                if (_holder.Scope.IsAccessDenied(meta.TreePath))
                    throw new UnauthorizedAccessException($"Write access denied for path '{meta.TreePath}'");
                if (_holder.Scope.IsReadOnly(meta.TreePath))
                    throw new ReadOnlyAccessException(meta.TreePath);
            }
            if (!string.IsNullOrEmpty(meta.RemoteSubId))
                throw new ReadOnlyAccessException($"Article {id} is in a remote read-only share.");
        }
    }

    public async Task<List<Article>> GetEmbeddingPendingAsync(int limit = 100)
    {
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND a.embedding_pending = 1 LIMIT @limit",
            new { limit })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    // Unscoped by name (see IArticleRepository's doc comment) — only reachable from
    // PendingEmbeddingProcessor (background worker, SystemCallerScope). If a future HTTP endpoint
    // calls this, add a scope check.
    public async Task UpdateEmbeddingUnscopedAsync(Guid id, byte[] projection, string modelVersion)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_article
              SET embedding_projection = @projection,
                  embedding_model_version = @modelVersion,
                  embedding_pending = 0
              WHERE id = @id",
            new { id, projection, modelVersion });

        // WP-14: this is the one write path that actually changes embedding bytes during normal
        // operation (the background PendingEmbeddingProcessor). Invalidate so the next search
        // rebuilds the cache with the fresh projection.
        _vectorCache.Invalidate();
    }

    // WP-11: mirrors GetEmbeddingPendingAsync exactly, for PendingIndexProcessor.
    public async Task<List<Article>> GetIndexPendingAsync(int limit = 100)
    {
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND a.index_pending = 1 LIMIT @limit",
            new { limit })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    // Unscoped by name. Only reachable from PendingIndexProcessor (background worker,
    // SystemCallerScope), mirroring UpdateEmbeddingUnscopedAsync's own note above.
    public async Task ClearIndexPendingUnscopedAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET index_pending = 0 WHERE id = @id",
            new { id });
    }

    // Unscoped by name. Only reachable from the search-index full-rebuild path (background
    // worker, SystemCallerScope) -- see SearchIndexLifecycleService.TriggerFullRebuildAsync.
    public async Task<int> MarkAllIndexPendingUnscopedAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync("UPDATE tbl_article SET index_pending = 1 WHERE status = 'A'");
    }

    // Unscoped by name, matching MarkAllIndexPendingUnscopedAsync above. Only reachable from
    // EmbeddingProjectionService's matrix-recovery path, itself driven by the background
    // PendingEmbeddingProcessor (SystemCallerScope).
    public async Task<int> MarkAllEmbeddingsPendingUnscopedAsync()
    {
        using var conn = OpenConnection();
        var affected = await conn.ExecuteAsync(
            "UPDATE tbl_article SET embedding_pending = 1 WHERE status = 'A'");
        // The cached vectors were projected in the discarded matrix's space — drop the cache so
        // no search can score against them while the re-embed catches up.
        _vectorCache.Invalidate();
        return affected;
    }

    // Unscoped by name. Only reachable from PendingEmbeddingProcessor (background worker,
    // SystemCallerScope), mirroring UpdateEmbeddingUnscopedAsync's own note above.
    public async Task<int> MarkStaleEmbeddingsPendingUnscopedAsync(string currentModelVersion)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            @"UPDATE tbl_article
              SET embedding_pending = 1
              WHERE status = 'A'
                AND embedding_pending = 0
                AND embedding_model_version IS NOT NULL
                AND embedding_model_version <> @currentModelVersion",
            new { currentModelVersion });
    }

    // Narrow projection used only to rank candidates by cosine similarity. Deliberately not
    // the full Article model: SearchByEmbeddingAsync used to hydrate every column (including
    // embedding_projection's BLOB sibling columns and all remote-sync metadata) for every
    // article with an embedding, just to throw away all but `topK` of them.
    //
    // WP-14: kept for the independent reference cosine computation in Storage tests (and as
    // documentation of the pre-WP-14 first-pass shape). SearchByEmbeddingAsync itself no longer
    // issues this SQL per call -- it scores out of the process-wide EmbeddingVectorCache, which
    // rebuilds this same id+projection result set only on invalidation.
    private sealed class EmbeddingCandidate
    {
        public Guid Id { get; set; }
        public byte[] EmbeddingProjection { get; set; } = null!;
    }

    public async Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10)
    {
        // WP-18: timing/counting wrapper around the semantic-search path. Only elapsed time and the
        // coarse result count are recorded; the query projection vector (and any query text it was
        // derived from upstream) is never passed to the metrics component. Behavior is unchanged on
        // every path -- the recording runs only on the success return, and exceptions propagate
        // exactly as before.
        var sw = _searchMetrics is null ? null : Stopwatch.StartNew();
        var results = await SearchByEmbeddingCoreAsync(queryProjection, topK);
        if (_searchMetrics is not null)
        {
            sw!.Stop();
            _searchMetrics.Record(SearchMetrics.SemanticSearch, sw.Elapsed, results.Count);
        }
        return results;
    }

    private async Task<List<Article>> SearchByEmbeddingCoreAsync(float[] queryProjection, int topK)
    {
        // WP-14: pass 1 (score every active article's projection) now runs out of the in-memory
        // EmbeddingVectorCache instead of a fresh full-table SQL scan on every call. The cache is
        // rebuilt (from the same `status = 'A' AND embedding_projection IS NOT NULL` query) only
        // when an embedding write has invalidated it since the last build. Scoring itself moved
        // into EmbeddingVectorCache.Snapshot.Score, which uses TensorPrimitives (SIMD) for the dot
        // product and reuses precomputed candidate norms + a once-per-query query norm.
        EmbeddingVectorCache.Snapshot snapshot = _vectorCache.GetOrRebuild();

        var topIds = snapshot.Score(queryProjection, topK);

        if (topIds.Count == 0) return [];

        // Pass 2: hydrate the full Article rows for just the surviving top-K ids. Unchanged from
        // pre-WP-14: only the embedding vectors are cached, not full rows.
        using var conn = OpenConnection();
        var fullArticles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.id IN @ids AND a.status = 'A'",
            new { ids = topIds })).ToList();

        // `IN` does not preserve order, so re-assemble the ranked order from topIds. This also
        // preserves the pre-existing quirk: _holder.Scope.FilterArticles runs AFTER Take(topK)
        // (inside Snapshot.Score), same as before this change, so an ACL-restricted caller can
        // still get back fewer than topK (or zero) visible results if invisible articles ranked
        // highest. That is not fixed here — it's a separate, bigger decision outside this
        // perf-only WP.
        var byId = fullArticles.ToDictionary(a => a.Id);
        var scored = topIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        return _holder.Scope.FilterArticles(scored);
    }

    /// <summary>
    /// WP-15: semantic search over per-chunk embeddings instead of one embedding per article, so a
    /// "needle" past <c>OnnxEmbeddingGenerator.MaxSequenceLength</c> tokens into a long article is
    /// findable via its own chunk. An article's score is the max cosine score over its own chunks
    /// (<see cref="ChunkEmbeddingVectorCache.Snapshot.ScoreMaxPerArticle"/>). An article that has no
    /// chunk rows yet (not (re)chunked since WP-15 shipped) falls back to its old full-document
    /// score from <see cref="EmbeddingVectorCache"/>, so this method never regresses an
    /// not-yet-backfilled article to "invisible" relative to the pre-WP-15
    /// <see cref="SearchByEmbeddingAsync"/>.
    /// </summary>
    public async Task<List<Article>> SearchByChunkEmbeddingAsync(float[] queryProjection, int topK = 10)
    {
        var sw = _searchMetrics is null ? null : Stopwatch.StartNew();
        var results = await SearchByChunkEmbeddingCoreAsync(queryProjection, topK);
        if (_searchMetrics is not null)
        {
            sw!.Stop();
            _searchMetrics.Record(SearchMetrics.SemanticSearch, sw.Elapsed, results.Count);
        }
        return results;
    }

    private async Task<List<Article>> SearchByChunkEmbeddingCoreAsync(float[] queryProjection, int topK)
    {
        ChunkEmbeddingVectorCache.Snapshot chunkSnapshot = await _chunkCache.GetOrRebuildAsync();
        Dictionary<Guid, float> scores = chunkSnapshot.ScoreMaxPerArticle(queryProjection);

        // Fallback: any active article with a full-document embedding but NO chunk rows yet keeps
        // its old score instead of silently dropping out of semantic search until it's (re)chunked.
        //
        // Only treat ChunkedArticleIds as authoritative when the query's own projection dimension
        // actually matches the chunk snapshot's dimension. If it doesn't (e.g. right after a model
        // version upgrade, before background reprocessing has re-chunked anything), every chunk
        // score is a meaningless 0 for this query -- treating those articles as "already handled by
        // chunk scoring" would incorrectly withhold their full-document fallback from every single
        // one of them, not just the ones that genuinely have no better answer. Found during an
        // independent adversarial review (2026-08-12); see ChunkEmbeddingVectorCache.Snapshot.
        // ChunkedArticleIds's own doc comment for the full reasoning.
        HashSet<Guid> chunkedIds = queryProjection.Length == chunkSnapshot.Dimension
            ? chunkSnapshot.ChunkedArticleIds
            : [];
        EmbeddingVectorCache.Snapshot fullSnapshot = _vectorCache.GetOrRebuild();
        foreach ((Guid id, float score) in fullSnapshot.ScoreAll(queryProjection))
        {
            if (!chunkedIds.Contains(id))
            {
                scores[id] = score;
            }
        }

        if (scores.Count == 0) return [];

        var topIds = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key) // deterministic tie-break when scores are equal
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();

        using var conn = OpenConnection();
        var fullArticles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.id IN @ids AND a.status = 'A'",
            new { ids = topIds })).ToList();

        var byId = fullArticles.ToDictionary(a => a.Id);
        var scored = topIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        return _holder.Scope.FilterArticles(scored);
    }

    public async Task<List<Article>> GetRecentActivityAsync(int limit = 50)
    {
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $@"SELECT {SelectCols} {FromClause}
               WHERE a.status = 'A'
               ORDER BY COALESCE(a.deleted_at, a.updated_at) DESC
               LIMIT @limit",
            new { limit })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    // Unguarded (no caller-scope check) and, since this is exactly the shape that once let a
    // user-facing folder-delete path relocate ACL-denied articles to the vault root (see
    // ClearFolderIdUnscopedAsync below), marked `internal` on the interface rather than just
    // documented: BeeMemoryBank.Api/Web cannot reference this member at all, by construction, not
    // by convention. See IArticleRepository's doc comment for the InternalsVisibleTo grants this
    // relies on. Reachable only from FolderBootstrapper (startup migration, same assembly) and
    // EventApplier folder-delete replay (Sync, granted access) — both run under SystemCallerScope.
    async Task IArticleRepository.SetFolderIdUnscopedAsync(Guid articleId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET folder_id = @folderId WHERE id = @articleId",
            new { articleId, folderId });
    }

    // Unguarded (no caller-scope check) — the exact method a user-facing folder-delete path once
    // called directly, relocating ACL-denied articles to the vault root instead of being blocked.
    // Marked `internal` on the interface (see IArticleRepository's doc comment) so the compiler,
    // not a comment someone has to notice, keeps it off the API/MCP surface. The two remaining
    // callers are safe DESPITE the missing check: FolderService.DeleteAsync (Core, same assembly)
    // calls folderRepo.SoftDeleteByPathPrefixAsync first, which walks every descendant and throws
    // on the first ACL violation — see the H1 comment there — so a denied descendant aborts the
    // whole cascade before this is ever reached; EventApplier.Folder (Sync, granted access) runs
    // under SystemCallerScope, where every check would be a no-op anyway. Do not add a new call
    // site ahead of an equivalent guard.
    async Task IArticleRepository.ClearFolderIdUnscopedAsync(Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET folder_id = NULL, tree_path = '/' WHERE folder_id = @folderId",
            new { folderId });
    }

    public async Task<List<(Guid Id, string TreePath)>> GetArticlesWithNullFolderIdAsync()
    {
        using var conn = OpenConnection();
        var results = await conn.QueryAsync<(Guid Id, string TreePath)>(
            "SELECT id AS Id, tree_path AS TreePath FROM tbl_article WHERE folder_id IS NULL AND status = 'A' AND tree_path != '/'");
        return results.ToList();
    }

    // Dapper binds public properties by alias — used by SoftDeleteAsync to
    // probe path and remote-subscription guard. Plain record class on purpose:
    // ValueTuple binds by position (Item1/Item2) and would defeat the guard.
    private sealed class ArticleDeleteMeta
    {
        public string? TreePath { get; set; }
        public string? RemoteSubId { get; set; }
    }

    // Same shape — used by UpdateAsync to enforce the stored-path guard.
    private sealed class ArticleUpdateGuardMeta
    {
        public string? StoredTreePath { get; set; }
        public string? RemoteSubId { get; set; }
    }
}
