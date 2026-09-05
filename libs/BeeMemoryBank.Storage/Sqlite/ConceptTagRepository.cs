using System.Data;
using System.Diagnostics;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public partial class ConceptTagRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder)
    : BaseRepository(factory), IConceptTagRepository
{
    private readonly CallerScopeHolder _holder = scopeHolder;

    public async Task<List<ConceptTagInfo>> GetAllAsync()
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<ConceptTagRow>(
            @"SELECT ct.name AS Name, act.article_id AS ArticleId, a.tree_path AS TreePath
              FROM tbl_concept_tag ct
              LEFT JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
              LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
              ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name")).ToList();
        return AggregateByScope(rows);
    }

    public async Task<List<string>> GetByArticleIdAsync(Guid articleId, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            return (await conn.QueryAsync<string>(
                @"SELECT ct.name
                  FROM tbl_concept_tag ct
                  JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
                  WHERE act.article_id = @articleId
                  ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name",
                new { articleId }, transaction)).ToList();
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    public async Task<Dictionary<Guid, List<string>>> GetByArticleIdsAsync(IEnumerable<Guid> articleIds)
    {
        var ids = articleIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, List<string>>();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<(string ArticleId, string Name)>(
            @"SELECT act.article_id AS ArticleId, ct.name AS Name
              FROM tbl_article_concept_tag act
              JOIN tbl_concept_tag ct ON ct.id = act.concept_tag_id
              WHERE act.article_id IN @Ids
              ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name",
            // Bind the Guids themselves, never `.ToString()`. Every other article_id parameter in
            // this file (and in ArticleRepository / ArticleBodyRepository) binds a Guid and lets the
            // provider render it; those rows come out uppercase. A hand-rolled `.ToString()` renders
            // lowercase, and SQLite compares TEXT case-sensitively — so this one query silently
            // matched nothing and every article in a list response came back with no tags at all,
            // while the single-article route right next to it returned them correctly.
            new { Ids = ids });
        var dict = new Dictionary<Guid, List<string>>();
        foreach (var (aid, name) in rows)
        {
            var guid = Guid.Parse(aid);
            if (!dict.TryGetValue(guid, out var list)) { list = new List<string>(); dict[guid] = list; }
            list.Add(name);
        }
        return dict;
    }

    public async Task SetForArticleAsync(Guid articleId, List<string> conceptNames, IDbTransaction? transaction = null)
    {
        if (transaction != null)
        {
            await SetForArticleCoreAsync(transaction.Connection!, transaction, articleId, conceptNames);
            return;
        }

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        await SetForArticleCoreAsync(conn, tx, articleId, conceptNames);
        tx.Commit();
    }

    private static async Task SetForArticleCoreAsync(IDbConnection conn, IDbTransaction tx, Guid articleId, List<string> conceptNames)
    {
        foreach (var name in conceptNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_concept_tag (name) VALUES (@name)",
                new { name }, tx);
        }

        await conn.ExecuteAsync(
            "DELETE FROM tbl_article_concept_tag WHERE article_id = @articleId",
            new { articleId }, tx);

        foreach (var name in conceptNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = await conn.QuerySingleAsync<int>(
                "SELECT id FROM tbl_concept_tag WHERE name = @name COLLATE NOCASE",
                new { name }, tx);
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_article_concept_tag (article_id, concept_tag_id) VALUES (@articleId, @id)",
                new { articleId, id }, tx);
        }
    }

    public async Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId)
    {
        using var conn = OpenConnection();

        var rows = (await conn.QueryAsync<RelatedArticleRow>(
            @"SELECT DISTINCT a.id AS ArticleId, a.title AS Title, COALESCE(f.path, '/') AS TreePath, ct.name AS ConceptName
              FROM tbl_article_concept_tag act1
              JOIN tbl_article_concept_tag act2 ON act1.concept_tag_id = act2.concept_tag_id
              JOIN tbl_article a ON a.id = act2.article_id
              LEFT JOIN tbl_folder f ON f.id = a.folder_id
              JOIN tbl_concept_tag ct ON ct.id = act1.concept_tag_id
              WHERE act1.article_id = @articleId
                AND act2.article_id != @articleId
                AND a.status = 'A'",
            new { articleId })).ToList();

        if (rows.Count == 0) return [];

        var scope = _holder.Scope;
        return rows
            .Where(r => scope.IsSuperadmin || !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => r.ArticleId)
            .Select(g =>
            {
                var first = g.First();
                var concepts = g.Select(r => r.ConceptName).Distinct().ToList();
                return new RelatedArticle
                {
                    Id = Guid.Parse(first.ArticleId),
                    Title = first.Title,
                    TreePath = first.TreePath,
                    SharedConcepts = concepts,
                    Strength = concepts.Count
                };
            })
            .OrderByDescending(r => r.Strength)
            .ToList();
    }

    public async Task<List<(Guid Id, string Title, string TreePath)>> SearchByConceptAsync(string concept)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<ArticleSearchRow>(
            @"SELECT a.id AS Id, a.title AS Title, COALESCE(f.path, '/') AS TreePath
              FROM tbl_concept_tag ct
              JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
              JOIN tbl_article a ON a.id = act.article_id
              LEFT JOIN tbl_folder f ON f.id = a.folder_id
              WHERE ct.name = @concept COLLATE NOCASE
                AND a.status = 'A'
              ORDER BY (substr(a.title,1,1)='_') DESC, a.title",
            new { concept })).ToList();

        var scope = _holder.Scope;
        return rows
            .Where(r => scope.IsSuperadmin || !scope.IsAccessDenied(r.TreePath))
            .Select(r => (r.Id, r.Title, r.TreePath))
            .ToList();
    }

    public async Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit, int offset = 0)
    {
        using var conn = OpenConnection();
        List<ConceptTagRow> rows;

        if (string.IsNullOrWhiteSpace(filter))
        {
            rows = (await conn.QueryAsync<ConceptTagRow>(
                @"SELECT ct.name AS Name, act.article_id AS ArticleId, a.tree_path AS TreePath
                  FROM tbl_concept_tag ct
                  LEFT JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
                  LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
                  ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name")).ToList();
        }
        else
        {
            var escaped = filter.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            var pattern = $"%{escaped}%";
            rows = (await conn.QueryAsync<ConceptTagRow>(
                @"SELECT ct.name AS Name, act.article_id AS ArticleId, a.tree_path AS TreePath
                  FROM tbl_concept_tag ct
                  LEFT JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
                  LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
                  WHERE ct.name LIKE @pattern ESCAPE '\' COLLATE NOCASE
                  ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name",
                new { pattern })).ToList();
        }

        return AggregateByScope(rows).Skip(offset).Take(limit).ToList();
    }

    public async Task<List<ConceptTagWithEmbedding>> GetWithEmbeddingsAsync()
    {
        using var conn = OpenConnection();
        var results = new List<ConceptTagWithEmbedding>();
        var rows = await conn.QueryAsync("SELECT name, embedding, embedding_model_version FROM tbl_concept_tag WHERE embedding IS NOT NULL");
        foreach (var row in rows)
        {
            results.Add(new ConceptTagWithEmbedding
            {
                Name = (string)row.name,
                Embedding = row.embedding as byte[],
                EmbeddingModelVersion = row.embedding_model_version as string
            });
        }
        return results;
    }

    public async Task AddToArticleAsync(Guid articleId, List<string> conceptNames)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var name in conceptNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_concept_tag (name) VALUES (@name)",
                new { name }, tx);

            var id = await conn.QuerySingleAsync<int>(
                "SELECT id FROM tbl_concept_tag WHERE name = @name COLLATE NOCASE",
                new { name }, tx);

            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_article_concept_tag (article_id, concept_tag_id) VALUES (@articleId, @id)",
                new { articleId, id }, tx);
        }

        tx.Commit();
    }

    public async Task RemoveFromArticleAsync(Guid articleId, string conceptName)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            @"DELETE FROM tbl_article_concept_tag
              WHERE article_id = @articleId
                AND concept_tag_id = (SELECT id FROM tbl_concept_tag WHERE name = @conceptName COLLATE NOCASE)",
            new { articleId, conceptName }, tx);

        tx.Commit();
    }

    public async Task RenameAsync(string name, string newName)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        // Allow case-only changes (e.g. "foo" → "FOO") — skip duplicate check in that case
        if (!string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT id FROM tbl_concept_tag WHERE name = @newName COLLATE NOCASE",
                new { newName }, tx);
            if (existing != null)
                throw new InvalidOperationException($"Concept tag '{newName}' already exists");
        }

        var found = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT id FROM tbl_concept_tag WHERE name = @name COLLATE NOCASE",
            new { name }, tx);
        if (found is null)
            throw new InvalidOperationException($"Concept tag '{name}' not found");

        await conn.ExecuteAsync(
            "UPDATE tbl_concept_tag SET name = @newName WHERE name = @name COLLATE NOCASE",
            new { name, newName }, tx);

        tx.Commit();
    }

    public async Task MergeAsync(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot merge a concept tag into itself.");

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var sourceId = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT id FROM tbl_concept_tag WHERE name = @source COLLATE NOCASE",
            new { source }, tx);
        if (sourceId is null)
            throw new InvalidOperationException($"Concept tag '{source}' not found");

        var targetId = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT id FROM tbl_concept_tag WHERE name = @target COLLATE NOCASE",
            new { target }, tx);
        if (targetId is null)
            throw new InvalidOperationException($"Concept tag '{target}' not found");

        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_article_concept_tag (article_id, concept_tag_id)
              SELECT article_id, @targetId FROM tbl_article_concept_tag WHERE concept_tag_id = @sourceId",
            new { targetId, sourceId }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM tbl_article_concept_tag WHERE concept_tag_id = @sourceId",
            new { sourceId }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM tbl_concept_tag WHERE id = @sourceId",
            new { sourceId }, tx);

        tx.Commit();
    }

    public async Task DeleteAsync(string name)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var id = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT id FROM tbl_concept_tag WHERE name = @name COLLATE NOCASE",
            new { name }, tx);
        if (id is null)
            throw new InvalidOperationException($"Concept tag '{name}' not found");

        await conn.ExecuteAsync(
            "DELETE FROM tbl_article_concept_tag WHERE concept_tag_id = @id",
            new { id }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM tbl_concept_tag WHERE id = @id",
            new { id }, tx);

        tx.Commit();
    }

    public async Task UpdateEmbeddingAsync(string name, byte[] embedding, string modelVersion, IDbTransaction? transaction = null)
    {
        var conn = transaction?.Connection ?? OpenConnection();
        try
        {
            await conn.ExecuteAsync(
                @"UPDATE tbl_concept_tag
                  SET embedding = @embedding, embedding_model_version = @modelVersion
                  WHERE name = @name COLLATE NOCASE",
                new { name, embedding, modelVersion }, transaction);
        }
        finally
        {
            if (transaction == null) conn.Dispose();
        }
    }

    private List<ConceptTagInfo> AggregateByScope(List<ConceptTagRow> rows)
    {
        var scope = _holder.Scope;
        var result = new List<ConceptTagInfo>();
        foreach (var g in rows.GroupBy(r => r.Name))
        {
            var articles = g.Where(r => r.ArticleId != null).ToList();
            var totalCount = articles.Select(r => r.ArticleId!).Distinct().Count();
            var accessibleCount = scope.IsSuperadmin
                ? totalCount
                : articles.Where(r => !scope.IsAccessDenied(r.TreePath))
                          .Select(r => r.ArticleId!).Distinct().Count();

            // Visibility rules:
            //   * Superadmin: show every tag, including orphans (totalCount == 0) — they
            //     need these to clean up the vocabulary.
            //   * Non-superadmin: show a tag only if at least one article carrying it is
            //     accessible. Otherwise the tag name itself leaks metadata about hidden
            //     content (think "codename-project-xyz") even when the article is denied.
            var visible = scope.IsSuperadmin
                ? accessibleCount > 0 || totalCount == 0
                : accessibleCount > 0;
            if (visible)
                result.Add(new ConceptTagInfo { Name = g.Key, ArticleCount = accessibleCount });
        }
        return result;
    }

    private sealed class ConceptTagRow
    {
        public string Name { get; set; } = "";
        public string? ArticleId { get; set; }
        public string? TreePath { get; set; }
    }

    private sealed class RelatedArticleRow
    {
        public string ArticleId { get; set; } = "";
        public string Title { get; set; } = "";
        public string TreePath { get; set; } = "";
        public string ConceptName { get; set; } = "";
    }

    private sealed class ArticleSearchRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string TreePath { get; set; } = "";
    }
}
