using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder) : BaseRepository(factory), IArticleRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;
    private const string SelectCols = @"
        a.id              AS Id,
        a.title           AS Title,
        COALESCE(f.path, '/') AS TreePath,
        a.folder_id       AS FolderId,
        a.embedding_projection     AS EmbeddingProjection,
        a.embedding_model_version  AS EmbeddingModelVersion,
        a.embedding_pending        AS EmbeddingPending,
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

    public async Task CreateAsync(Article article)
    {
        // Repo-level write guard: close the "new endpoint forgets manual ACL check" hole.
        if (_holder.Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException($"Write access denied for path '{article.TreePath}'");
        if (_holder.Scope.IsReadOnly(article.TreePath))
            throw new ReadOnlyAccessException(article.TreePath);

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article
              (id, title, tree_path, folder_id, embedding_projection, embedding_model_version, embedding_pending,
               status, lamport_ts, source_node_id, created_at, updated_at,
               remote_subscription_id, remote_origin_id, remote_version, remote_updated_by,
               protected, protection_hint)
              VALUES (@Id, @Title, @TreePath, @FolderId, @EmbeddingProjection, @EmbeddingModelVersion, @EmbeddingPending,
                      @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt,
                      @RemoteSubscriptionId, @RemoteOriginId, @RemoteVersion, @RemoteUpdatedBy,
                      @Protected, @ProtectionHint)",
            article, tx);

        tx.Commit();
    }

    public async Task UpdateAsync(Article article)
    {
        if (_holder.Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException($"Write access denied for path '{article.TreePath}'");
        if (_holder.Scope.IsReadOnly(article.TreePath))
            throw new ReadOnlyAccessException(article.TreePath);

        // SECURITY: we ALSO need to check the article's CURRENT (pre-update)
        // path. Without this, a caller with write permission on /Public could
        // call UpdateAsync with article.Id pointing at a /Secrets article and
        // article.TreePath = "/Public" — the guard above passes, and the row
        // is moved (with full plaintext attached) into the caller's reach.
        // Gemini security review 2026-05-25.
        //
        // The mirrored-share guard is in the same SELECT so we avoid a second
        // round-trip.
        if (!_holder.Scope.IsSuperadmin)
        {
            using var check = OpenConnection();
            var stored = await check.QuerySingleOrDefaultAsync<ArticleUpdateGuardMeta>(
                @"SELECT COALESCE(f.path, '/') AS StoredTreePath,
                         a.remote_subscription_id AS RemoteSubId
                    FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
                   WHERE a.id = @id",
                new { id = article.Id });
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

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        // Note: no `status = 'A'` filter — must allow resurrecting soft-deleted rows
        // when LWW says incoming Create wins over an older Delete (Wave 2 audit
        // claude-A #7). The caller sets Status explicitly via the @Status param.
        // deleted_at is reset when transitioning back to 'A'.
        await conn.ExecuteAsync(
            @"UPDATE tbl_article
              SET title = @Title, tree_path = @TreePath, folder_id = @FolderId,
                  embedding_projection = @EmbeddingProjection,
                  embedding_model_version = @EmbeddingModelVersion,
                  embedding_pending = @EmbeddingPending,
                  lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt,
                  status = @Status,
                  deleted_at = CASE WHEN @Status = 'A' THEN NULL ELSE deleted_at END,
                  remote_version = @RemoteVersion,
                  remote_updated_by = @RemoteUpdatedBy,
                  protected = @Protected,
                  protection_hint = @ProtectionHint
              WHERE id = @Id",
            article, tx);

        tx.Commit();
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        // GetByIdAsync respects ambient scope (returns null if denied), so fetching
        // through SystemCallerScope to get the raw path, then enforce denial explicitly.
        if (!_holder.Scope.IsSuperadmin)
        {
            using var check = OpenConnection();
            // Dapper maps ValueTuple by position (Item1/Item2), not by alias, so
            // `(string?, string?)` would receive nulls regardless of SELECT — the
            // ACL/Read-only guard would silently no-op. Use a dedicated record so
            // properties bind by name. Caught by Claude+gemini third review.
            var meta = await check.QuerySingleOrDefaultAsync<ArticleDeleteMeta>(
                @"SELECT COALESCE(f.path, '/') AS TreePath, a.remote_subscription_id AS RemoteSubId
                  FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
                  WHERE a.id = @id",
                new { id });
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

        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id AND status = 'A'",
            new { id, now });
    }

    public async Task<List<Article>> GetEmbeddingPendingAsync(int limit = 100)
    {
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND a.embedding_pending = 1 LIMIT @limit",
            new { limit })).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    // AUDIT: unguarded. Only reachable from PendingEmbeddingProcessor (background worker,
    // SystemCallerScope). If a future HTTP endpoint calls this, add a scope check.
    public async Task UpdateEmbeddingAsync(Guid id, byte[] projection, string modelVersion)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_article
              SET embedding_projection = @projection,
                  embedding_model_version = @modelVersion,
                  embedding_pending = 0
              WHERE id = @id",
            new { id, projection, modelVersion });
    }

    // Narrow projection used only to rank candidates by cosine similarity. Deliberately not
    // the full Article model: SearchByEmbeddingAsync used to hydrate every column (including
    // embedding_projection's BLOB sibling columns and all remote-sync metadata) for every
    // article with an embedding, just to throw away all but `topK` of them.
    private sealed class EmbeddingCandidate
    {
        public Guid Id { get; set; }
        public byte[] EmbeddingProjection { get; set; } = null!;
    }

    public async Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10)
    {
        using var conn = OpenConnection();

        // Pass 1: only id + embedding bytes for every active article with an embedding —
        // enough to score, without hydrating the rest of the row.
        var candidates = (await conn.QueryAsync<EmbeddingCandidate>(
            "SELECT a.id AS Id, a.embedding_projection AS EmbeddingProjection FROM tbl_article a WHERE a.status = 'A' AND a.embedding_projection IS NOT NULL",
            null)).ToList();

        if (candidates.Count == 0) return [];

        var dim = queryProjection.Length;
        var topIds = candidates
            .Select(c =>
            {
                var proj = MemoryMarshal.Cast<byte, float>(c.EmbeddingProjection.AsSpan());
                if (proj.Length != dim) return (id: c.Id, score: 0f);
                float dot = 0f, normA = 0f;
                for (int i = 0; i < dim; i++)
                {
                    dot += queryProjection[i] * proj[i];
                    normA += proj[i] * proj[i];
                }
                float normQ = 0f;
                for (int i = 0; i < dim; i++) normQ += queryProjection[i] * queryProjection[i];
                var denom = MathF.Sqrt(normA * normQ);
                return (id: c.Id, score: denom > 0 ? dot / denom : 0f);
            })
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.id)
            .ToList();

        if (topIds.Count == 0) return [];

        // Pass 2: hydrate the full Article rows for just the surviving top-K ids.
        var fullArticles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.id IN @ids AND a.status = 'A'",
            new { ids = topIds })).ToList();

        // `IN` does not preserve order, so re-assemble the ranked order from topIds. This also
        // preserves the pre-existing quirk: _holder.Scope.FilterArticles runs AFTER Take(topK)
        // (below), same as before this change, so an ACL-restricted caller can still get back
        // fewer than topK (or zero) visible results if invisible articles ranked highest. That
        // is not fixed here — it's a separate, bigger decision outside this perf-only WP.
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

    // AUDIT: unguarded. Reachable only from FolderBootstrapper (startup migration) and
    // EventApplier folder-delete replay — both run under SystemCallerScope. Do not expose
    // from an HTTP endpoint without adding a scope check.
    public async Task SetFolderIdAsync(Guid articleId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET folder_id = @folderId WHERE id = @articleId",
            new { articleId, folderId });
    }

    // AUDIT: unguarded. Same rationale as SetFolderIdAsync — background/sync only.
    public async Task ClearFolderIdAsync(Guid folderId)
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
