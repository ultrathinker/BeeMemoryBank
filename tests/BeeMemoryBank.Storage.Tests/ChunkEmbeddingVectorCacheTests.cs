using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// WP-15: correctness tests for <see cref="ChunkEmbeddingVectorCache"/> — max-pooling across an
/// article's chunks, invalidation, and a differential check against an independent reference
/// cosine implementation over dequantized vectors (mirroring
/// <see cref="EmbeddingVectorCacheTests"/>'s style for the sibling full-document cache).
/// </summary>
public class ChunkEmbeddingVectorCacheTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ArticleChunkEmbeddingRepository _repo = null!;
    private ChunkEmbeddingVectorCache _cache = null!;

    private const int Dim = 8;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_chunk_cache_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _cache = new ChunkEmbeddingVectorCache(_factory);
        _repo = new ArticleChunkEmbeddingRepository(_factory, _cache);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertArticleAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'x', '/', 'A', @now, @now)",
            new { id, now });
        return id;
    }

    private static float[] RandomUnitVector(Random random, int dim)
    {
        var v = new float[dim];
        float sumSquares = 0f;
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)(random.NextDouble() * 2 - 1);
            sumSquares += v[i] * v[i];
        }
        float norm = MathF.Sqrt(sumSquares);
        for (int i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }

    private static float ReferenceCosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom > 0 ? (float)(dot / denom) : 0f;
    }

    [Fact]
    public async Task ScoreMaxPerArticle_TakesBestChunkNotAverage()
    {
        var query = RandomUnitVector(new Random(1), Dim);
        var articleId = await InsertArticleAsync();

        // One chunk nearly identical to the query (high score), one nearly orthogonal (low score).
        var goodChunk = query.Select(v => v + 0.001f).ToArray();
        var badChunk = RandomUnitVector(new Random(2), Dim);

        var (badBytes, badScale, _) = Int8Quantizer.Quantize(badChunk);
        var (goodBytes, goodScale, _) = Int8Quantizer.Quantize(goodChunk);
        await _repo.ReplaceChunksAsync(articleId, [(badBytes, badScale), (goodBytes, goodScale)], "test-model");

        var snapshot = await _cache.GetOrRebuildAsync();
        var scores = snapshot.ScoreMaxPerArticle(query);

        scores.Should().ContainKey(articleId);
        scores[articleId].Should().BeGreaterThan(0.9f, "the max over chunks must reflect the near-identical chunk, not the orthogonal one");
    }

    [Fact]
    public async Task GetOrRebuildAsync_PicksUpNewWriteAfterInvalidation()
    {
        var articleId = await InsertArticleAsync();
        var before = await _cache.GetOrRebuildAsync();
        before.ChunkCount.Should().Be(0);

        var (bytes, scale, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(3), Dim));
        await _repo.ReplaceChunksAsync(articleId, [(bytes, scale)], "test-model");

        var after = await _cache.GetOrRebuildAsync();
        after.ChunkCount.Should().Be(1);
        after.ChunkedArticleIds.Should().Contain(articleId);
    }

    [Fact]
    public async Task ScoreMaxPerArticle_MatchesIndependentReferenceCosine_WithinQuantizationError()
    {
        var random = new Random(42);
        var query = RandomUnitVector(random, Dim);
        var articleId = await InsertArticleAsync();
        var chunkVector = RandomUnitVector(random, Dim);

        var (bytes, scale, _) = Int8Quantizer.Quantize(chunkVector);
        await _repo.ReplaceChunksAsync(articleId, [(bytes, scale)], "test-model");

        var snapshot = await _cache.GetOrRebuildAsync();
        var actual = snapshot.ScoreMaxPerArticle(query)[articleId];

        var dequantized = Int8Quantizer.Dequantize(bytes, scale);
        var expected = ReferenceCosine(query, dequantized);

        actual.Should().BeApproximately(expected, 1e-4f);
    }

    [Fact]
    public async Task ScoreMaxPerArticle_NoChunksAtAll_ReturnsEmpty()
    {
        var snapshot = await _cache.GetOrRebuildAsync();
        var scores = snapshot.ScoreMaxPerArticle(RandomUnitVector(new Random(4), Dim));
        scores.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceChunksAsync_ThenReplaceWithFewer_CacheReflectsOnlyCurrentChunks()
    {
        var articleId = await InsertArticleAsync();
        var (b1, s1, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(5), Dim));
        var (b2, s2, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(6), Dim));
        await _repo.ReplaceChunksAsync(articleId, [(b1, s1), (b2, s2)], "test-model");
        (await _cache.GetOrRebuildAsync()).ChunkCount.Should().Be(2);

        var (b3, s3, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(7), Dim));
        await _repo.ReplaceChunksAsync(articleId, [(b3, s3)], "test-model");

        (await _cache.GetOrRebuildAsync()).ChunkCount.Should().Be(1);
    }

    // Regression coverage for a finding from an independent adversarial review (2026-08-12): a
    // chunk row whose stored dimension doesn't match the snapshot's dimension (e.g. a stale row
    // left behind by a retired model version) must be excluded from the snapshot entirely -- not
    // zero-filled and still counted in ChunkedArticleIds, which would incorrectly block that
    // article's full-document fallback in ArticleRepository.SearchByChunkEmbeddingCoreAsync forever.

    [Fact]
    public async Task RebuildFromDb_MismatchedDimensionRow_ExcludedFromChunkedArticleIds()
    {
        var normalArticle = await InsertArticleAsync();
        var (normalBytes, normalScale, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(10), Dim));
        await _repo.ReplaceChunksAsync(normalArticle, [(normalBytes, normalScale)], "test-model");

        // A row whose projection is a DIFFERENT length than the snapshot's dimension (Dim=8) --
        // simulating a leftover chunk from a retired model version with a different embedding size.
        var mismatchedArticle = await InsertArticleAsync();
        var (mismatchedBytes, mismatchedScale, _) = Int8Quantizer.Quantize(RandomUnitVector(new Random(11), Dim * 2));
        await _repo.ReplaceChunksAsync(mismatchedArticle, [(mismatchedBytes, mismatchedScale)], "old-model");

        var snapshot = await _cache.GetOrRebuildAsync();

        // The snapshot's "official" dimension comes from whichever row happens to sort first by
        // article_id (an implementation detail this test doesn't control, since article ids are
        // random Guids) -- so assert the property this fix actually guarantees generically: whichever
        // article's row matches the resulting dimension is included, and the OTHER one (whichever it
        // turns out to be) is excluded, never both.
        snapshot.Dimension.Should().BeOneOf(Dim, Dim * 2);
        Guid expectedIncluded = snapshot.Dimension == Dim ? normalArticle : mismatchedArticle;
        Guid expectedExcluded = snapshot.Dimension == Dim ? mismatchedArticle : normalArticle;

        snapshot.ChunkedArticleIds.Should().ContainSingle().Which.Should().Be(expectedIncluded);
        snapshot.ChunkedArticleIds.Should().NotContain(expectedExcluded,
            "a dimension-mismatched row must not count as \"this article has a usable chunk\" -- it would otherwise score 0 forever and never fall back to its full-document embedding");
    }
}
