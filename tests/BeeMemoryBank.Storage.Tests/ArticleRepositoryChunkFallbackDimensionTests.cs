using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Regression coverage for a finding from an independent adversarial review (2026-08-12) of
/// <see cref="ArticleRepository.SearchByChunkEmbeddingAsync"/>: when the QUERY's own projection
/// dimension doesn't match the chunk cache's dimension at all (e.g. right after a model version
/// upgrade, before background reprocessing has re-chunked anything), every chunk score is a
/// meaningless 0 for that query. Before the fix, <c>ChunkedArticleIds</c> still included every
/// chunked article regardless, which incorrectly withheld the full-document fallback from ALL of
/// them -- not just the ones genuinely covered by chunk scoring.
/// </summary>
public class ArticleRepositoryChunkFallbackDimensionTests : IAsyncLifetime
{
    private const int FullDocDim = 8;
    private const int ChunkDim = 4; // deliberately different, simulating a stale/retired model dimension

    private DbConnectionFactory _factory = null!;
    private ArticleRepository _repo = null!;
    private ArticleChunkEmbeddingRepository _chunkRepo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_chunk_fallback_dim_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var scopeHolder = new CallerScopeHolder();
        var vectorCache = new EmbeddingVectorCache(_factory);
        var chunkCache = new ChunkEmbeddingVectorCache(_factory);
        _repo = new ArticleRepository(_factory, scopeHolder, vectorCache, searchMetrics: null, chunkCache);
        _chunkRepo = new ArticleChunkEmbeddingRepository(_factory, chunkCache);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertArticleWithFullDocEmbeddingAsync(float[] projection)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        byte[] bytes = MemoryMarshal.AsBytes(projection.AsSpan()).ToArray();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, embedding_projection)
              VALUES (@id, 'x', '/', 'A', @now, @now, @bytes)",
            new { id, now, bytes });
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

    [Fact]
    public async Task SearchByChunkEmbeddingAsync_QueryDimensionMatchesNoChunkRow_FallsBackForEveryChunkedArticle()
    {
        var random = new Random(42);
        var query = RandomUnitVector(random, FullDocDim);

        // An article whose full-document embedding is a near-perfect match for the query, but whose
        // ONLY chunk row is at a different (stale) dimension -- exactly what "chunked, but from a
        // retired model version, and the query is now in the new model's dimension" looks like.
        var articleId = await InsertArticleWithFullDocEmbeddingAsync(query.Select(v => v + 0.0001f).ToArray());
        var (staleChunkBytes, staleScale, _) = Int8Quantizer.Quantize(RandomUnitVector(random, ChunkDim));
        await _chunkRepo.ReplaceChunksAsync(articleId, [(staleChunkBytes, staleScale)], "old-model");

        var results = await _repo.SearchByChunkEmbeddingAsync(query, topK: 10);

        results.Should().Contain(a => a.Id == articleId,
            "the article's full-document embedding is a near-perfect match for the query -- the stale-dimension chunk row must not silently suppress that via a meaningless 0 score");
    }
}
