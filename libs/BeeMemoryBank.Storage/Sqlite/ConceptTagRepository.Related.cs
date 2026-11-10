using System.Data;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public partial class ConceptTagRepository
{
    // Shared with every other repository in the process by DI; a direct `new` (tests, the phone) gets a
    // private one. A blind node has no vectors at all, so there the semantic part is simply empty.
    private EmbeddingVectorCache VectorCache => _vectorCache ??= new EmbeddingVectorCache(Factory);

    public async Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId, int limit = RelatedArticle.DefaultLimit, bool includeMeaning = true)
    {
        limit = RelatedArticle.ClampLimit(limit);

        // Vectors first, with no connection held open: the first call after a change rebuilds the snapshot.
        // Without the meaning part (the vault is locked) the snapshot is not touched, not even to build it.
        var neighbours = includeMeaning
            ? VectorCache.GetOrRebuild()
                .GetNeighbours(articleId, RelatedArticleRanker.NeighbourPool, RelatedArticleRanker.NeighbourMinZ)
            : [];

        using var conn = OpenConnection();
        var byId = (await LoadTagCandidatesAsync(conn, articleId)).ToDictionary(c => c.Id);

        if (neighbours.Count > 0)
        {
            // Neighbours that share no rare tag are not loaded yet: read their title and folder, then apply the
            // caller's folder filter, so a note they may not see never fills a slot of the list or shows its
            // title. The pool is wider than the list for exactly that reason.
            var unknown = neighbours.Where(n => !byId.ContainsKey(n.Id)).Select(n => n.Id).ToList();
            var visible = await LoadVisibleArticlesAsync(conn, unknown);

            var kept = 0;
            foreach (var (id, similarity, z) in neighbours)
            {
                if (kept >= RelatedArticleRanker.NeighbourKeep) break;
                if (byId.TryGetValue(id, out var known))
                    byId[id] = known with { Similarity = similarity, Z = z };
                else if (visible.TryGetValue(id, out var row))
                    byId[id] = new RelatedCandidate(id, row.Title, row.TreePath, [], similarity, z);
                else
                    continue;
                kept++;
            }
        }

        return RelatedArticleRanker.Rank(byId.Values.ToList(), limit);
    }

    public async Task<(int Count, int SharedTagTotal)> GetRelatedTagSummaryAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        var candidates = await LoadTagCandidatesAsync(conn, articleId);
        return (candidates.Count, candidates.Sum(c => c.Tags.Count));
    }

    /// <summary>
    /// The active articles the caller may see that share at least one rare tag with <paramref name="articleId"/>,
    /// each with the rare tags it shares and their weights. Commonplace tags are not read at all.
    /// </summary>
    private async Task<List<RelatedCandidate>> LoadTagCandidatesAsync(IDbConnection conn, Guid articleId)
    {
        var activeArticles = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_article WHERE status = 'A'");

        // Frequency of each of this article's tags over the whole vault (not the caller's slice): the weight is a
        // property of the tag, the same for every caller.
        var tags = (await conn.QueryAsync<RelatedTagRow>(
            @"SELECT ct.id AS TagId, ct.name AS Name, COUNT(a.id) AS Df
              FROM tbl_article_concept_tag mine
              JOIN tbl_concept_tag ct ON ct.id = mine.concept_tag_id
              JOIN tbl_article_concept_tag act ON act.concept_tag_id = ct.id
              JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
              WHERE mine.article_id = @articleId
              GROUP BY ct.id, ct.name",
            new { articleId })).ToList();

        var weights = new Dictionary<int, (string Name, double Weight)>();
        foreach (var t in tags)
        {
            var weight = RelatedArticleRanker.TagWeight(activeArticles, t.Df);
            if (weight > 0) weights[t.TagId] = (t.Name, weight);
        }
        if (weights.Count == 0) return [];

        // At most (tags x commonplace limit) rows: every tag read here is rare by construction.
        var rows = await conn.QueryAsync<RelatedTagHitRow>(
            @"SELECT act.concept_tag_id AS TagId, a.id AS ArticleId, a.title AS Title, COALESCE(f.path, '/') AS TreePath
              FROM tbl_article_concept_tag act
              JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
              LEFT JOIN tbl_folder f ON f.id = a.folder_id
              WHERE act.concept_tag_id IN @tagIds AND act.article_id != @articleId",
            new { tagIds = weights.Keys.ToList(), articleId });

        var scope = _holder.Scope;
        return rows
            .Where(r => scope.IsSuperadmin || !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => r.ArticleId)
            .Select(g =>
            {
                var first = g.First();
                var shared = g.Select(r => r.TagId).Distinct().Select(id => weights[id]).ToList();
                return new RelatedCandidate(Guid.Parse(g.Key), first.Title, first.TreePath, shared, null, 0f);
            })
            .ToList();
    }

    /// <summary>Title and folder path of the given active articles, minus those the caller may not see.</summary>
    private async Task<Dictionary<Guid, (string Title, string TreePath)>> LoadVisibleArticlesAsync(IDbConnection conn, List<Guid> ids)
    {
        var result = new Dictionary<Guid, (string Title, string TreePath)>();
        if (ids.Count == 0) return result;

        // Bind the Guids themselves, not strings (see GetByArticleIdsAsync).
        var rows = await conn.QueryAsync<ArticleSearchRow>(
            @"SELECT a.id AS Id, a.title AS Title, COALESCE(f.path, '/') AS TreePath
              FROM tbl_article a
              LEFT JOIN tbl_folder f ON f.id = a.folder_id
              WHERE a.id IN @ids AND a.status = 'A'",
            new { ids });

        var scope = _holder.Scope;
        foreach (var r in rows)
            if (scope.IsSuperadmin || !scope.IsAccessDenied(r.TreePath))
                result[r.Id] = (r.Title, r.TreePath);
        return result;
    }

    private sealed class RelatedTagRow
    {
        public int TagId { get; set; }
        public string Name { get; set; } = "";
        public int Df { get; set; }
    }

    private sealed class RelatedTagHitRow
    {
        public int TagId { get; set; }
        public string ArticleId { get; set; } = "";
        public string Title { get; set; } = "";
        public string TreePath { get; set; } = "";
    }
}
