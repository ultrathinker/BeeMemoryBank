using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Differential tests for the WP-04 perf refactor of ArticleRepository.SearchAsync and
/// SearchByEmbeddingAsync: both methods were rewritten to stop materializing full/duplicated
/// Article rows (with the embedding_projection BLOB) as an intermediate step, but must return
/// the exact same result *set* (and, for the embedding search, the same ranked order) as before.
/// </summary>
public class ArticleRepositorySearchTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ArticleRepository _repo = null!;
    private CallerScopeHolder _scopeHolder = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_article_search_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _scopeHolder = new CallerScopeHolder();
        _repo = new ArticleRepository(_factory, _scopeHolder);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertArticleAsync(string title, byte[]? embeddingProjection = null)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, embedding_projection)
              VALUES (@id, @title, '/', 'A', @now, @now, @embeddingProjection)",
            new { id, title, now, embeddingProjection });
        return id;
    }

    private async Task<int> InsertTagAsync(string name)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleAsync<int>(
            "INSERT INTO tbl_concept_tag (name) VALUES (@name) RETURNING id",
            new { name });
    }

    private async Task LinkTagAsync(Guid articleId, int tagId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article_concept_tag (article_id, concept_tag_id) VALUES (@articleId, @tagId)",
            new { articleId, tagId });
    }

    private static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    // --- SearchAsync -----------------------------------------------------

    [Fact]
    public async Task SearchAsync_ArticleWithManyMatchingTags_AppearsExactlyOnce()
    {
        // This is the exact bug class the old `SELECT DISTINCT` over a row that included the
        // embedding_projection BLOB was masking inefficiently rather than incorrectly: a JOIN
        // through tbl_article_concept_tag multiplies one row per matching tag. The fix restructures
        // the query as `WHERE a.id IN (subquery)`, so the outer SELECT must still yield exactly one
        // row per matching article regardless of how many of its tags match.
        var articleId = await InsertArticleAsync("Unrelated Title");
        for (var i = 0; i < 5; i++)
        {
            var tagId = await InsertTagAsync($"gizmo-{i}");
            await LinkTagAsync(articleId, tagId);
        }

        var results = await _repo.SearchAsync("gizmo");

        results.Should().ContainSingle(a => a.Id == articleId);
    }

    [Fact]
    public async Task SearchAsync_MatchesByTitleAndByTag_ReturnsUnionWithoutDuplicates()
    {
        // Differential fixture: one article matches by title only, one matches by several tags
        // (the duplicate-row scenario the old DISTINCT-over-blob was papering over), one matches
        // by both title and tags, and one matches neither and must be excluded.
        var titleMatch = await InsertArticleAsync("Project Falcon Notes");

        var tagMatchOnly = await InsertArticleAsync("Completely Different");
        foreach (var name in new[] { "falcon-wing", "falcon-tail", "falcon-nose" })
        {
            var tagId = await InsertTagAsync(name);
            await LinkTagAsync(tagMatchOnly, tagId);
        }

        var bothMatch = await InsertArticleAsync("Falcon Sighting Report");
        var bothTagId = await InsertTagAsync("falcon-alert");
        await LinkTagAsync(bothMatch, bothTagId);

        var noMatch = await InsertArticleAsync("Sparrow Watching Guide");
        var noMatchTagId = await InsertTagAsync("bird-generic");
        await LinkTagAsync(noMatch, noMatchTagId);

        var results = await _repo.SearchAsync("falcon");

        results.Select(a => a.Id).Should().BeEquivalentTo([titleMatch, tagMatchOnly, bothMatch]);
        results.Should().OnlyHaveUniqueItems(a => a.Id);
    }

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmpty()
    {
        await InsertArticleAsync("Nothing Relevant Here");

        var results = await _repo.SearchAsync("zzz-no-such-term");

        results.Should().BeEmpty();
    }

    // --- SearchByEmbeddingAsync -------------------------------------------

    [Fact]
    public async Task SearchByEmbeddingAsync_MoreCandidatesThanTopK_ReturnsSameRankingAsNaiveReference()
    {
        // Fixture with more embedded articles than topK, so the narrowing (fetch id+embedding,
        // score, take topK, then hydrate only those rows) is actually exercised. The expected
        // order below is computed independently via a plain reference cosine-similarity
        // implementation over the same vectors, not by re-reading production code.
        var query = new float[] { 1f, 0f, 0f, 0f };

        var vectors = new (string label, float[] vector)[]
        {
            ("closest", new float[] { 1f, 0f, 0f, 0f }),      // cosine = 1.0
            ("second", new float[] { 0.9f, 0.1f, 0f, 0f }),   // cosine ~ 0.994
            ("third", new float[] { 0.5f, 0.5f, 0f, 0f }),    // cosine ~ 0.707
            ("fourth", new float[] { 0.1f, 0.9f, 0f, 0f }),   // cosine ~ 0.110
            ("farthest", new float[] { 0f, 1f, 0f, 0f }),     // cosine = 0.0
        };

        var ids = new Dictionary<string, Guid>();
        foreach (var (label, vector) in vectors)
        {
            ids[label] = await InsertArticleAsync($"Article {label}", ToBytes(vector));
        }

        // An additional active article without an embedding must never be considered.
        await InsertArticleAsync("No Embedding Here");

        var expectedOrder = vectors
            .Select(v => (v.label, score: NaiveCosine(query, v.vector)))
            .OrderByDescending(x => x.score)
            .Take(3)
            .Select(x => ids[x.label])
            .ToList();

        var results = await _repo.SearchByEmbeddingAsync(query, topK: 3);

        results.Select(a => a.Id).Should().ContainInOrder(expectedOrder);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchByEmbeddingAsync_ReturnsFullyHydratedArticles()
    {
        // The narrow first-pass query must not leak into the final result: callers still get
        // back fully populated Article rows (title, tree path, etc.), not just id+embedding.
        var vector = new float[] { 1f, 0f };
        var id = await InsertArticleAsync("Hydration Check", ToBytes(vector));

        var results = await _repo.SearchByEmbeddingAsync(new float[] { 1f, 0f }, topK: 5);

        var article = results.Should().ContainSingle(a => a.Id == id).Subject;
        article.Title.Should().Be("Hydration Check");
        article.TreePath.Should().Be("/");
    }

    [Fact]
    public async Task SearchByEmbeddingAsync_NoEmbeddedArticles_ReturnsEmpty()
    {
        await InsertArticleAsync("No Embedding");

        var results = await _repo.SearchByEmbeddingAsync(new float[] { 1f, 0f }, topK: 5);

        results.Should().BeEmpty();
    }

    private static float NaiveCosine(float[] a, float[] b)
    {
        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA * normB);
        return denom > 0 ? dot / denom : 0f;
    }
}
