using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using System.Data;

namespace BeeMemoryBank.Storage.Sqlite;

public class ArticleRepository(DbConnectionFactory factory) : BaseRepository(factory), IArticleRepository
{
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
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? $"SELECT {SelectCols} {FromClause} WHERE a.id = @id"
            : $"SELECT {SelectCols} {FromClause} WHERE a.id = @id AND a.status = 'A'";

        var article = await conn.QuerySingleOrDefaultAsync<Article>(sql, new { id });
        if (article == null) return null;
        article.Tags = await LoadTagsAsync(conn, id);
        return article;
    }

    public async Task<List<Article>> ListAsync(string? treePath = null)
    {
        using var conn = OpenConnection();
        string sql;
        object? param;

        if (treePath == null)
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' ORDER BY f.path, a.title";
            param = null;
        }
        else if (treePath == "/")
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = '/' OR a.folder_id IS NULL) ORDER BY a.title";
            param = null;
        }
        else
        {
            sql = $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND (f.path = @treePath OR f.path LIKE @prefix) ORDER BY f.path, a.title";
            param = new { treePath, prefix = treePath.TrimEnd('/') + "/%" };
        }

        var articles = (await conn.QueryAsync<Article>(sql, param)).ToList();
        await PopulateTagsAsync(conn, articles);
        return articles;
    }

    public async Task<List<Article>> SearchAsync(string query)
    {
        using var conn = OpenConnection();

        var byTitle = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND unicode_contains(a.title, @query) ORDER BY a.title",
            new { query })).ToList();

        var byTag = (await conn.QueryAsync<Article>(
            $@"SELECT DISTINCT {SelectCols} {FromClause}
               JOIN tbl_article_tag at ON a.id = at.article_id
               JOIN tbl_tag t ON t.id = at.tag_id
               WHERE a.status = 'A' AND unicode_contains(t.name, @query) ORDER BY a.title",
            new { query })).ToList();

        var all = byTitle.Union(byTag, ArticleIdComparer.Instance).ToList();
        await PopulateTagsAsync(conn, all);
        return all;
    }

    public async Task<List<Article>> GetByIdsAsync(List<Guid> ids)
    {
        if (ids.Count == 0) return [];
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.id IN @ids AND a.status = 'A' ORDER BY a.title",
            new { ids })).ToList();
        await PopulateTagsAsync(conn, articles);
        return articles;
    }

    public async Task CreateAsync(Article article)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article
              (id, title, tree_path, folder_id, embedding_projection, embedding_model_version, embedding_pending,
               status, lamport_ts, source_node_id, created_at, updated_at)
              VALUES (@Id, @Title, @TreePath, @FolderId, @EmbeddingProjection, @EmbeddingModelVersion, @EmbeddingPending,
                      @Status, @LamportTs, @SourceNodeId, @CreatedAt, @UpdatedAt)",
            article, tx);

        await SaveTagsAsync(conn, article.Id, article.Tags, tx);
        tx.Commit();
    }

    public async Task UpdateAsync(Article article)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"UPDATE tbl_article
              SET title = @Title, tree_path = @TreePath, folder_id = @FolderId,
                  embedding_projection = @EmbeddingProjection,
                  embedding_model_version = @EmbeddingModelVersion,
                  embedding_pending = @EmbeddingPending,
                  lamport_ts = @LamportTs, source_node_id = @SourceNodeId,
                  updated_at = @UpdatedAt
              WHERE id = @Id AND status = 'A'",
            article, tx);

        await conn.ExecuteAsync(
            "DELETE FROM tbl_article_tag WHERE article_id = @id",
            new { id = article.Id }, tx);

        await SaveTagsAsync(conn, article.Id, article.Tags, tx);
        tx.Commit();
    }

    public async Task SoftDeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        var now = UtcNow();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET status = 'D', deleted_at = @now, updated_at = @now WHERE id = @id AND status = 'A'",
            new { id, now });
    }

    private static async Task SaveTagsAsync(IDbConnection conn, Guid articleId, List<string> tags, IDbTransaction tx)
    {
        foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync("INSERT OR IGNORE INTO tbl_tag (name) VALUES (@tag)", new { tag }, tx);
            var tagId = await conn.QuerySingleAsync<int>("SELECT id FROM tbl_tag WHERE name = @tag", new { tag }, tx);
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_article_tag (article_id, tag_id) VALUES (@articleId, @tagId)",
                new { articleId, tagId }, tx);
        }
    }

    private static async Task<List<string>> LoadTagsAsync(IDbConnection conn, Guid articleId)
    {
        return (await conn.QueryAsync<string>(
            @"SELECT t.name FROM tbl_tag t
              JOIN tbl_article_tag at ON t.id = at.tag_id
              WHERE at.article_id = @articleId ORDER BY t.name",
            new { articleId })).ToList();
    }

    private static async Task PopulateTagsAsync(IDbConnection conn, List<Article> articles)
    {
        foreach (var article in articles)
            article.Tags = await LoadTagsAsync(conn, article.Id);
    }

    public async Task<List<Article>> GetEmbeddingPendingAsync(int limit = 100)
    {
        using var conn = OpenConnection();
        var articles = (await conn.QueryAsync<Article>(
            $"SELECT {SelectCols} {FromClause} WHERE a.status = 'A' AND a.embedding_pending = 1 LIMIT @limit",
            new { limit })).ToList();
        await PopulateTagsAsync(conn, articles);
        return articles;
    }

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

        await PopulateTagsAsync(conn, scored);
        return scored;
    }

    public async Task<List<TagInfo>> GetAllTagsAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<TagInfo>(
            @"SELECT t.name AS Name, COUNT(at.article_id) AS ArticleCount
              FROM tbl_tag t
              JOIN tbl_article_tag at ON t.id = at.tag_id
              JOIN tbl_article a ON a.id = at.article_id
              WHERE a.status = 'A'
              GROUP BY t.id, t.name
              ORDER BY t.name")).ToList();
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
        await PopulateTagsAsync(conn, articles);
        return articles;
    }

    public async Task SetFolderIdAsync(Guid articleId, Guid folderId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_article SET folder_id = @folderId WHERE id = @articleId",
            new { articleId, folderId });
    }

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

    private sealed class ArticleIdComparer : IEqualityComparer<Article>
    {
        public static readonly ArticleIdComparer Instance = new();
        public bool Equals(Article? x, Article? y) => x?.Id == y?.Id;
        public int GetHashCode(Article a) => a.Id.GetHashCode();
    }
}
