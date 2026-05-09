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
        a.deleted_at      AS DeletedAt";

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

    public async Task<List<Article>> ListAsync(string? treePath = null)
    {
        using var conn = OpenConnection();
        string sql;
        object? param;

        if (treePath == null)
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' ORDER BY f.path, (substr(a.title,1,1)='_') DESC, a.title";
            param = null;
        }
        else if (treePath == "/")
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = '/' OR a.folder_id IS NULL) ORDER BY (substr(a.title,1,1)='_') DESC, a.title";
            param = null;
        }
        else
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = @treePath OR f.path LIKE @prefix ESCAPE '\\') ORDER BY f.path, (substr(a.title,1,1)='_') DESC, a.title";
            var escapedPrefix = treePath.TrimEnd('/').Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%";
            param = new { treePath, prefix = escapedPrefix };
        }

        var articles = (await conn.QueryAsync<Article>(sql, param)).ToList();
        return _holder.Scope.FilterArticles(articles);
    }

    public async Task<List<Article>> SearchAsync(string query)
    {
        using var conn = OpenConnection();

        var byTitle = (await conn.QueryAsync<Article>(
            $@"SELECT DISTINCT {SelectCols} {FromClause}
               LEFT JOIN tbl_article_concept_tag act ON act.article_id = a.id
               LEFT JOIN tbl_concept_tag ct ON ct.id = act.concept_tag_id
               WHERE a.status = 'A'
                 AND (unicode_contains(a.title, @query) OR unicode_contains(ct.name, @query))
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

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article
              (id, title, tree_path, folder_id, embedding_projection, embedding_model_version, embedding_pending,
               status, lamport_ts, source_node_id, created_at, updated_at)
              VALUES (@Id, @Title, @TreePath, @FolderId, @EmbeddingProjection, @EmbeddingModelVersion, @EmbeddingPending,
                      @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt)",
            article, tx);

        tx.Commit();
    }

    public async Task UpdateAsync(Article article)
    {
        if (_holder.Scope.IsAccessDenied(article.TreePath))
            throw new UnauthorizedAccessException($"Write access denied for path '{article.TreePath}'");

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
                  deleted_at = CASE WHEN @Status = 'A' THEN NULL ELSE deleted_at END
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
            var treePath = await check.QuerySingleOrDefaultAsync<string?>(
                "SELECT COALESCE(f.path, '/') FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.id = @id",
                new { id });
            if (treePath != null && _holder.Scope.IsAccessDenied(treePath))
                throw new UnauthorizedAccessException($"Write access denied for path '{treePath}'");
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

    public async Task<List<Article>> SearchByEmbeddingAsync(float[] queryProjection, int topK = 10)
    {
        // Load all articles with embeddings and compute cosine similarity in memory
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND a.embedding_projection IS NOT NULL",
            null)).ToList();

        if (articles.Count == 0) return [];

        var dim = queryProjection.Length;
        var scored = articles
            .Select(a =>
            {
                var proj = MemoryMarshal.Cast<byte, float>(a.EmbeddingProjection.AsSpan());
                if (proj.Length != dim) return (article: a, score: 0f);
                float dot = 0f, normA = 0f;
                for (int i = 0; i < dim; i++)
                {
                    dot += queryProjection[i] * proj[i];
                    normA += proj[i] * proj[i];
                }
                float normQ = 0f;
                for (int i = 0; i < dim; i++) normQ += queryProjection[i] * queryProjection[i];
                var denom = MathF.Sqrt(normA * normQ);
                return (article: a, score: denom > 0 ? dot / denom : 0f);
            })
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.article)
            .ToList();

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

}
