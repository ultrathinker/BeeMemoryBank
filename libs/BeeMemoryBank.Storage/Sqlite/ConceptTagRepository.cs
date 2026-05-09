using System.Data;
using System.Diagnostics;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class ConceptTagRepository(DbConnectionFactory factory, CallerScopeHolder scopeHolder)
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

    public async Task<List<string>> GetByArticleIdAsync(Guid articleId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<string>(
            @"SELECT ct.name
              FROM tbl_concept_tag ct
              JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
              WHERE act.article_id = @articleId
              ORDER BY (substr(ct.name,1,1)='_') DESC, ct.name",
            new { articleId })).ToList();
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
            new { Ids = ids.Select(i => i.ToString()).ToList() });
        var dict = new Dictionary<Guid, List<string>>();
        foreach (var (aid, name) in rows)
        {
            var guid = Guid.Parse(aid);
            if (!dict.TryGetValue(guid, out var list)) { list = new List<string>(); dict[guid] = list; }
            list.Add(name);
        }
        return dict;
    }

    public async Task SetForArticleAsync(Guid articleId, List<string> conceptNames)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

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

        tx.Commit();
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

    public async Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit)
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

        return AggregateByScope(rows).Take(limit).ToList();
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

    public async Task<List<ConceptGraphEdge>> GetGraphDataAsync()
    {
        using var conn = OpenConnection();
        var scope = _holder.Scope;

        if (scope.IsSuperadmin)
        {
            return (await conn.QueryAsync<ConceptGraphEdge>(
                @"SELECT ct1.name AS Source, ct2.name AS Target, COUNT(*) AS Weight
                  FROM tbl_article_concept_tag act1
                  JOIN tbl_article_concept_tag act2
                      ON act1.article_id = act2.article_id
                      AND act1.concept_tag_id < act2.concept_tag_id
                  JOIN tbl_concept_tag ct1 ON act1.concept_tag_id = ct1.id
                  JOIN tbl_concept_tag ct2 ON act2.concept_tag_id = ct2.id
                  JOIN tbl_article a ON act1.article_id = a.id AND a.status = 'A'
                  GROUP BY ct1.name, ct2.name")).ToList();
        }

        var rows = (await conn.QueryAsync<GraphEdgeRow>(
            @"SELECT ct1.name AS Source, ct2.name AS Target, a.tree_path AS TreePath
              FROM tbl_article_concept_tag act1
              JOIN tbl_article_concept_tag act2 ON act1.article_id = act2.article_id AND act1.concept_tag_id < act2.concept_tag_id
              JOIN tbl_concept_tag ct1 ON act1.concept_tag_id = ct1.id
              JOIN tbl_concept_tag ct2 ON act2.concept_tag_id = ct2.id
              JOIN tbl_article a ON act1.article_id = a.id AND a.status = 'A'")).ToList();

        return rows
            .Where(r => !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => (r.Source, r.Target))
            .Select(g => new ConceptGraphEdge { Source = g.Key.Source, Target = g.Key.Target, Weight = g.Count() })
            .ToList();
    }

    public async Task<List<ConceptGraphEdge>> GetNeighborGraphAsync(string tag)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<GraphEdgeRow>(
            @"SELECT ct1.name AS Source, ct2.name AS Target, a.tree_path AS TreePath
              FROM tbl_article_concept_tag act1
              JOIN tbl_article_concept_tag act2
                  ON act1.article_id = act2.article_id
                  AND act1.concept_tag_id < act2.concept_tag_id
              JOIN tbl_concept_tag ct1 ON act1.concept_tag_id = ct1.id
              JOIN tbl_concept_tag ct2 ON act2.concept_tag_id = ct2.id
              JOIN tbl_article a ON act1.article_id = a.id AND a.status = 'A'
              WHERE ct1.name = @tag COLLATE NOCASE OR ct2.name = @tag COLLATE NOCASE",
            new { tag })).ToList();

        var scope = _holder.Scope;
        return rows
            .Where(r => scope.IsSuperadmin || !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => (r.Source, r.Target))
            .Select(g => new ConceptGraphEdge { Source = g.Key.Source, Target = g.Key.Target, Weight = g.Count() })
            .ToList();
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

    public async Task UpdateEmbeddingAsync(string name, byte[] embedding, string modelVersion)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_concept_tag
              SET embedding = @embedding, embedding_model_version = @modelVersion
              WHERE name = @name COLLATE NOCASE",
            new { name, embedding, modelVersion });
    }

    public async Task<ConceptTagGraphData> GetHomeGraphAsync()
    {
        using var conn = OpenConnection();
        var scope = _holder.Scope;

        var allTags = (await conn.QueryAsync<TagFreqRow>(
            @"SELECT ct.name AS Name, act.article_id AS ArticleId, a.tree_path AS TreePath,
                     a.updated_at AS UpdatedAt
              FROM tbl_concept_tag ct
              LEFT JOIN tbl_article_concept_tag act ON ct.id = act.concept_tag_id
              LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'")).ToList();

        var totalVisibleArticles = scope.IsSuperadmin
            ? allTags.Where(r => r.ArticleId != null).Select(r => r.ArticleId!).Distinct().Count()
            : allTags.Where(r => r.ArticleId != null && !scope.IsAccessDenied(r.TreePath))
                     .Select(r => r.ArticleId!).Distinct().Count();

        // A tag is a "hub" if it's attached to more than half of all visible articles
        // (e.g. meta-tags like "important" or "idea"). We drop hubs from the home view
        // so they don't dominate. Threshold tuned after initial 20% proved too aggressive
        // for small vaults.
        var hubThreshold = totalVisibleArticles * 0.5;

        var tagStats = new Dictionary<string, int>();
        var tagRecency = new Dictionary<string, DateTime>();
        foreach (var g in allTags.GroupBy(r => r.Name))
        {
            var articles = g.Where(r => r.ArticleId != null).ToList();
            var visibleIds = scope.IsSuperadmin
                ? articles.Select(r => r.ArticleId!).Distinct().ToList()
                : articles.Where(r => !scope.IsAccessDenied(r.TreePath))
                          .Select(r => r.ArticleId!).Distinct().ToList();

            if (visibleIds.Count == 0) continue;
            tagStats[g.Key] = visibleIds.Count;

            var maxUpdated = articles
                .Where(r => !scope.IsAccessDenied(r.TreePath) || scope.IsSuperadmin)
                .Select(r => r.UpdatedAt)
                .Where(d => d != default)
                .DefaultIfEmpty()
                .Max();
            if (maxUpdated != default)
                tagRecency[g.Key] = maxUpdated;
        }

        HashSet<string> selectedNames;
        List<(string Name, int Count, string Group)> selectedTags;
        if (tagStats.Count <= 30)
        {
            selectedNames = tagStats.Keys.ToHashSet();
            selectedTags = tagStats.Select(kv => (kv.Key, kv.Value, "base")).ToList();
        }
        else
        {
            var hubs = tagStats.Where(kv => kv.Value > hubThreshold).Select(kv => kv.Key).ToHashSet();

            var pulseTags = tagRecency
                .Where(kv => !hubs.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => kv.Key)
                .ToHashSet();

            var baseTags = tagStats
                .Where(kv => !hubs.Contains(kv.Key) && !pulseTags.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Take(30)
                .Select(kv => kv.Key)
                .ToHashSet();

            selectedNames = pulseTags.Union(baseTags).ToHashSet();
            selectedTags = baseTags.Select(n => (n, tagStats[n], "base"))
                .Concat(pulseTags.Select(n => (n, tagStats[n], "pulse")))
                .ToList();
        }

        var inducedEdges = await GetInducedEdgesAsync(conn, selectedNames, scope);

        var selectedIds = (await conn.QueryAsync<(int Id, string Name)>(
            "SELECT id AS Id, name AS Name FROM tbl_concept_tag WHERE name IN @names COLLATE NOCASE",
            new { names = selectedNames.ToList() })).ToDictionary(r => r.Name, r => r.Id);
        var neighborCounts = await GetTotalVisibleNeighborsBatchAsync(conn, selectedIds.Values.ToHashSet(), scope);

        var nodes = selectedTags
            .Select(t => new ConceptTagGraphNode(
                t.Name,
                t.Count,
                t.Group,
                selectedIds.TryGetValue(t.Name, out var id) ? neighborCounts.GetValueOrDefault(id) : 0))
            .ToList();

        return new ConceptTagGraphData(nodes, inducedEdges);
    }

    public async Task<ConceptTagGraphData> SearchGraphAsync(string query, int depth, int maxNodes)
    {
        depth = Math.Clamp(depth, 1, 3);
        maxNodes = Math.Clamp(maxNodes, 10, 500);

        using var conn = OpenConnection();
        var scope = _holder.Scope;

        var escaped = query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        var pattern = $"%{escaped}%";

        var matchingRows = (await conn.QueryAsync<(int Id, string Name)>(
            @"SELECT ct.id AS Id, ct.name AS Name FROM tbl_concept_tag ct
              WHERE ct.name LIKE @pattern ESCAPE '\' COLLATE NOCASE
              LIMIT 30",
            new { pattern })).ToList();

        if (matchingRows.Count == 0)
            return new ConceptTagGraphData([], []);

        var matchIds = matchingRows.Select(r => r.Id).ToHashSet();
        var visitedIds = new HashSet<int>(matchIds);
        var idToName = matchingRows.ToDictionary(r => r.Id, r => r.Name);
        var frontier = new HashSet<int>(matchIds);

        var topK = new[] { 20, 10, 5 };

        for (var level = 0; level < depth && frontier.Count > 0 && visitedIds.Count < maxNodes; level++)
        {
            var k = topK[level];
            var frontierIds = frontier.ToList();

            var neighborRows = (await conn.QueryAsync<EdgePairRow>(
                @"SELECT e.tag_id_a AS TagIdA, e.tag_id_b AS TagIdB
                  FROM tbl_concept_tag_edge e
                  WHERE e.tag_id_a IN @ids OR e.tag_id_b IN @ids",
                new { ids = frontierIds })).ToList();

            // For each frontier node, tally candidate neighbors by edge-row count (co-occurrence weight).
            var candidatesByFrontier = new Dictionary<int, Dictionary<int, int>>();
            foreach (var edge in neighborRows)
            {
                int frontierId, neighborId;
                if (frontier.Contains(edge.TagIdA))
                {
                    frontierId = edge.TagIdA;
                    neighborId = edge.TagIdB;
                }
                else
                {
                    frontierId = edge.TagIdB;
                    neighborId = edge.TagIdA;
                }
                if (visitedIds.Contains(neighborId)) continue;

                if (!candidatesByFrontier.TryGetValue(frontierId, out var dict))
                {
                    dict = new Dictionary<int, int>();
                    candidatesByFrontier[frontierId] = dict;
                }
                dict[neighborId] = dict.GetValueOrDefault(neighborId) + 1;
            }

            var nextFrontier = new HashSet<int>();
            foreach (var kv in candidatesByFrontier)
            {
                if (visitedIds.Count >= maxNodes) break;
                var topNeighbors = kv.Value
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key)
                    .Take(k)
                    .Select(p => p.Key);
                foreach (var nid in topNeighbors)
                {
                    if (visitedIds.Count >= maxNodes) break;
                    if (visitedIds.Add(nid))
                        nextFrontier.Add(nid);
                }
            }

            // Batch-load names for newly discovered nodes (avoid N+1 lookups).
            var newIds = nextFrontier.Where(id => !idToName.ContainsKey(id)).ToList();
            if (newIds.Count > 0)
            {
                var nameRows = await conn.QueryAsync<(int Id, string Name)>(
                    "SELECT id AS Id, name AS Name FROM tbl_concept_tag WHERE id IN @ids",
                    new { ids = newIds });
                foreach (var r in nameRows)
                    idToName[r.Id] = r.Name;
            }

            frontier = nextFrontier;
        }

        var countsByTagId = await GetVisibleArticleCountsBatchAsync(conn, visitedIds, scope);

        var visibleIds = visitedIds.Where(id => countsByTagId.GetValueOrDefault(id) > 0).ToHashSet();
        var visibleNames = visibleIds.Select(id => idToName[id]).ToHashSet();

        var edges = await GetInducedEdgesAsync(conn, visibleNames, scope);

        // Prune neighbor-only nodes whose connection to the search origin is visible
        // exclusively through articles the caller cannot see. Keeping them would leak
        // metadata (the user could infer "tag X co-occurs with tag Y on some hidden doc").
        // Match nodes stay regardless, since they were found by name and already public.
        var namesInVisibleEdges = new HashSet<string>(edges.SelectMany(e => new[] { e.Source, e.Target }));
        var keptIds = visibleIds
            .Where(id => matchIds.Contains(id) || namesInVisibleEdges.Contains(idToName[id]))
            .ToHashSet();

        var neighborCounts = await GetTotalVisibleNeighborsBatchAsync(conn, keptIds, scope);

        var nodes = keptIds.Select(id => new ConceptTagGraphNode(
            idToName[id],
            countsByTagId.GetValueOrDefault(id),
            matchIds.Contains(id) ? "match" : "neighbor",
            neighborCounts.GetValueOrDefault(id))).ToList();

        return new ConceptTagGraphData(nodes, edges);
    }

    public async Task<ConceptTagEdgeStats> GetEdgeStatsAsync()
    {
        using var conn = OpenConnection();

        var totalTags = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag");
        var totalEdgeRows = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag_edge");

        var nodesWithEdges = await conn.QuerySingleAsync<long>(
            @"SELECT COUNT(DISTINCT tag_id) FROM (
                SELECT tag_id_a AS tag_id FROM tbl_concept_tag_edge
                UNION ALL
                SELECT tag_id_b AS tag_id FROM tbl_concept_tag_edge
              )");

        var pairStats = (await conn.QueryAsync<PairStatRow>(
            @"SELECT COUNT(*) AS Cnt FROM tbl_concept_tag_edge GROUP BY tag_id_a, tag_id_b")).ToList();

        var uniquePairs = pairStats.Count;
        var maxWeight = pairStats.Count > 0 ? pairStats.Max(p => p.Cnt) : 0;
        var avgWeight = pairStats.Count > 0 ? pairStats.Average(p => p.Cnt) : 0.0;

        return new ConceptTagEdgeStats(totalTags, nodesWithEdges, totalEdgeRows, uniquePairs, maxWeight, avgWeight);
    }

    public async Task<ConceptTagEdgeRebuildReport> CheckAndRebuildEdgesAsync()
    {
        var sw = Stopwatch.StartNew();
        using var conn = OpenConnection();

        var beforeEdgeRows = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag_edge");

        var expectedEdgeRows = await conn.QuerySingleAsync<long>(
            @"SELECT COUNT(*)
              FROM tbl_article_concept_tag act1
              JOIN tbl_article_concept_tag act2
                ON act1.article_id = act2.article_id
               AND act1.concept_tag_id < act2.concept_tag_id");

        var wasConsistent = beforeEdgeRows == expectedEdgeRows;
        long afterEdgeRows;
        bool rebuilt;

        if (wasConsistent)
        {
            afterEdgeRows = beforeEdgeRows;
            rebuilt = false;
        }
        else
        {
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM tbl_concept_tag_edge", transaction: tx);
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_concept_tag_edge (tag_id_a, tag_id_b, article_id)
                  SELECT act1.concept_tag_id, act2.concept_tag_id, act1.article_id
                  FROM tbl_article_concept_tag act1
                  JOIN tbl_article_concept_tag act2
                    ON act1.article_id = act2.article_id
                   AND act1.concept_tag_id < act2.concept_tag_id",
                transaction: tx);
            tx.Commit();
            afterEdgeRows = await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag_edge");
            rebuilt = true;
        }

        sw.Stop();
        return new ConceptTagEdgeRebuildReport(beforeEdgeRows, expectedEdgeRows, afterEdgeRows, wasConsistent, rebuilt, sw.ElapsedMilliseconds);
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

    private sealed class GraphEdgeRow
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string TreePath { get; set; } = "";
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

    private sealed class TagFreqRow
    {
        public string Name { get; set; } = "";
        public string? ArticleId { get; set; }
        public string? TreePath { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class EdgePairRow
    {
        public int TagIdA { get; set; }
        public int TagIdB { get; set; }
    }

    private sealed class PairStatRow
    {
        public int Cnt { get; set; }
    }

    private async Task<List<ConceptGraphEdge>> GetInducedEdgesAsync(IDbConnection conn, HashSet<string> nodeNames, ICallerScope scope)
    {
        if (nodeNames.Count < 2) return [];

        var nameList = nodeNames.ToList();

        if (scope.IsSuperadmin)
        {
            return (await conn.QueryAsync<ConceptGraphEdge>(
                @"SELECT ct1.name AS Source, ct2.name AS Target, COUNT(DISTINCT e.article_id) AS Weight
                  FROM tbl_concept_tag_edge e
                  JOIN tbl_concept_tag ct1 ON e.tag_id_a = ct1.id
                  JOIN tbl_concept_tag ct2 ON e.tag_id_b = ct2.id
                  JOIN tbl_article a ON e.article_id = a.id AND a.status = 'A'
                  WHERE ct1.name IN @names AND ct2.name IN @names
                  GROUP BY ct1.name, ct2.name",
                new { names = nameList })).ToList();
        }

        var rows = (await conn.QueryAsync<GraphEdgeRow>(
            @"SELECT ct1.name AS Source, ct2.name AS Target, a.tree_path AS TreePath
              FROM tbl_concept_tag_edge e
              JOIN tbl_concept_tag ct1 ON e.tag_id_a = ct1.id
              JOIN tbl_concept_tag ct2 ON e.tag_id_b = ct2.id
              JOIN tbl_article a ON e.article_id = a.id AND a.status = 'A'
              WHERE ct1.name IN @names AND ct2.name IN @names",
            new { names = nameList })).ToList();

        return rows
            .Where(r => !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => (r.Source, r.Target))
            .Select(g => new ConceptGraphEdge { Source = g.Key.Source, Target = g.Key.Target, Weight = g.Count() })
            .ToList();
    }

    // Count how many distinct neighbor tags each given tag has in tbl_concept_tag_edge,
    // respecting ACL (scope-limited — a neighbor only known through hidden articles
    // doesn't count). Used to render a "has more neighbors" indicator on the graph
    // without shipping every neighbor's name to the client.
    private async Task<Dictionary<int, int>> GetTotalVisibleNeighborsBatchAsync(IDbConnection conn, HashSet<int> tagIds, ICallerScope scope)
    {
        if (tagIds.Count == 0) return new Dictionary<int, int>();
        var ids = tagIds.ToList();

        if (scope.IsSuperadmin)
        {
            var rows = await conn.QueryAsync<(int TagId, int Cnt)>(
                @"SELECT tag_id AS TagId, COUNT(DISTINCT other_tag) AS Cnt
                  FROM (
                      SELECT tag_id_a AS tag_id, tag_id_b AS other_tag FROM tbl_concept_tag_edge
                      UNION ALL
                      SELECT tag_id_b AS tag_id, tag_id_a AS other_tag FROM tbl_concept_tag_edge
                  )
                  WHERE tag_id IN @ids
                  GROUP BY tag_id", new { ids });
            return rows.ToDictionary(r => r.TagId, r => r.Cnt);
        }

        var all = (await conn.QueryAsync<(int TagId, int OtherTag, string? TreePath)>(
            @"SELECT tag_id AS TagId, other_tag AS OtherTag, tree_path AS TreePath
              FROM (
                  SELECT e.tag_id_a AS tag_id, e.tag_id_b AS other_tag, a.tree_path
                  FROM tbl_concept_tag_edge e
                  JOIN tbl_article a ON a.id = e.article_id AND a.status = 'A'
                  UNION ALL
                  SELECT e.tag_id_b AS tag_id, e.tag_id_a AS other_tag, a.tree_path
                  FROM tbl_concept_tag_edge e
                  JOIN tbl_article a ON a.id = e.article_id AND a.status = 'A'
              )
              WHERE tag_id IN @ids", new { ids })).ToList();

        return all
            .Where(r => !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => r.TagId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.OtherTag).Distinct().Count());
    }

    private async Task<Dictionary<int, int>> GetVisibleArticleCountsBatchAsync(IDbConnection conn, HashSet<int> tagIds, ICallerScope scope)
    {
        if (tagIds.Count == 0) return new Dictionary<int, int>();
        var ids = tagIds.ToList();

        if (scope.IsSuperadmin)
        {
            var rows = await conn.QueryAsync<(int TagId, int Cnt)>(
                @"SELECT act.concept_tag_id AS TagId, COUNT(DISTINCT act.article_id) AS Cnt
                  FROM tbl_article_concept_tag act
                  JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
                  WHERE act.concept_tag_id IN @ids
                  GROUP BY act.concept_tag_id",
                new { ids });
            return rows.ToDictionary(r => r.TagId, r => r.Cnt);
        }

        var all = (await conn.QueryAsync<(int TagId, string ArticleId, string? TreePath)>(
            @"SELECT act.concept_tag_id AS TagId, act.article_id AS ArticleId, a.tree_path AS TreePath
              FROM tbl_article_concept_tag act
              JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
              WHERE act.concept_tag_id IN @ids",
            new { ids })).ToList();

        return all
            .Where(r => !scope.IsAccessDenied(r.TreePath))
            .GroupBy(r => r.TagId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ArticleId).Distinct().Count());
    }
}
