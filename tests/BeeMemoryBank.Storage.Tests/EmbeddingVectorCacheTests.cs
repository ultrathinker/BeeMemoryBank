using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// WP-14: correctness tests for <see cref="EmbeddingVectorCache"/> and its wiring into
/// <see cref="ArticleRepository.SearchByEmbeddingAsync"/> — a differential test against an
/// independent reference cosine implementation, an invalidation test (a fresh write must be
/// picked up without a process restart), and a concurrency stress test (concurrent readers must
/// never observe a torn/inconsistent snapshot while a rebuild runs), matching the style of
/// <see cref="IndexBuilderConcurrencyTests"/>-family tests elsewhere in this initiative: real
/// concurrent threads, deterministic operation counts, no sleep-based synchronization.
/// </summary>
public class EmbeddingVectorCacheTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private ArticleRepository _repo = null!;
    private EmbeddingVectorCache _cache = null!;
    private CallerScopeHolder _scopeHolder = null!;

    private const int Dim = 8;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_embedding_cache_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _scopeHolder = new CallerScopeHolder();
        _cache = new EmbeddingVectorCache(_factory);
        _repo = new ArticleRepository(_factory, _scopeHolder, _cache);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertArticleAsync(string title, float[]? projection)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("o");
        byte[]? bytes = projection == null ? null : MemoryMarshal.AsBytes(projection.AsSpan()).ToArray();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, embedding_projection)
              VALUES (@id, @title, '/', 'A', @now, @now, @bytes)",
            new { id, title, now, bytes });
        return id;
    }

    private static float[] RandomVector(Random random, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(random.NextDouble() * 2 - 1);
        return v;
    }

    private static float ReferenceCosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
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

    // --- Differential correctness --------------------------------------------------

    [Fact]
    public async Task SearchByEmbeddingAsync_MatchesIndependentReferenceCosineRanking()
    {
        var random = new Random(20260812);
        var vectors = new Dictionary<Guid, float[]>();

        for (int i = 0; i < 15; i++)
        {
            var vec = RandomVector(random, Dim);
            var id = await InsertArticleAsync($"Doc {i}", vec);
            vectors[id] = vec;
        }
        // One article with no embedding at all, and one with a mismatched dimension — both must
        // score 0 / never crash, matching pre-WP-14 behavior exactly.
        await InsertArticleAsync("No embedding", null);
        var mismatchId = await InsertArticleAsync("Mismatched dim", [1f, 2f, 3f]);

        var query = RandomVector(random, Dim);
        const int topK = 5;

        var expected = vectors
            .Select(kv => (Id: kv.Key, Score: ReferenceCosine(query, kv.Value)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Id)
            .ToList();

        var actual = await _repo.SearchByEmbeddingAsync(query, topK);

        actual.Select(a => a.Id).Should().Equal(expected, "cached+SIMD scoring must rank identically to an independent reference cosine implementation");
        actual.Select(a => a.Id).Should().NotContain(mismatchId, "a dimension-mismatched candidate must never outrank real matches (it always scores 0)");
    }

    [Fact]
    public async Task SearchByEmbeddingAsync_NoEmbeddedArticles_ReturnsEmpty()
    {
        await InsertArticleAsync("No embedding at all", null);
        var result = await _repo.SearchByEmbeddingAsync(RandomVector(new Random(1), Dim), 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchByEmbeddingAsync_MoreCandidatesThanTopK_TruncatesToTopK()
    {
        var random = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            await InsertArticleAsync($"Doc {i}", RandomVector(random, Dim));
        }

        var result = await _repo.SearchByEmbeddingAsync(RandomVector(random, Dim), topK: 3);

        result.Should().HaveCount(3);
    }

    // --- Invalidation ---------------------------------------------------------------

    [Fact]
    public async Task Invalidate_NewEmbeddingWrite_IsPickedUpWithoutRestart()
    {
        var random = new Random(7);
        var query = RandomVector(random, Dim);

        // First search builds and caches a snapshot with zero candidates.
        var before = await _repo.SearchByEmbeddingAsync(query, 5);
        before.Should().BeEmpty();

        // A fresh write must invalidate the cache — the next search must see it, not the stale
        // empty snapshot built above. InsertArticleAsync writes via raw SQL (standing in for an
        // external write path, e.g. RemoteEventApplier's sync writes, that doesn't go through
        // ArticleRepository's own Create/Update/UpdateEmbedding methods), so it doesn't trigger
        // ArticleRepository's invalidation itself — call it explicitly here to model what any real
        // write path (all of which DO call Invalidate(), per the diff) is responsible for doing.
        var newId = await InsertArticleAsync("Freshly embedded", query); // identical to the query => cosine 1.0
        _cache.Invalidate();
        var after = await _repo.SearchByEmbeddingAsync(query, 5);

        after.Should().ContainSingle(a => a.Id == newId, "a new embedding write must invalidate the cache so the very next search sees it");
    }

    [Fact]
    public async Task Invalidate_UpdateEmbeddingAsync_RebuildsWithNewVector()
    {
        var random = new Random(9);
        var original = RandomVector(random, Dim);
        var id = await InsertArticleAsync("Doc", original);

        // Warm the cache with the original vector.
        await _repo.SearchByEmbeddingAsync(original, 5);

        // UpdateEmbeddingAsync rewrites the projection directly — this must invalidate too.
        var replacement = RandomVector(random, Dim);
        await _repo.UpdateEmbeddingAsync(id, MemoryMarshal.AsBytes(replacement.AsSpan()).ToArray(), "v2");

        var resultForReplacement = await _repo.SearchByEmbeddingAsync(replacement, 5);
        resultForReplacement.Should().ContainSingle(a => a.Id == id, "UpdateEmbeddingAsync must invalidate the cache so the new vector is immediately searchable");
    }

    // --- Concurrency ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentSearchesDuringInvalidation_NeverThrowsOrReturnsTornState()
    {
        var random = new Random(20260812);
        for (int i = 0; i < 30; i++)
        {
            await InsertArticleAsync($"Seed {i}", RandomVector(random, Dim));
        }

        var query = RandomVector(random, Dim);
        var stop = new CancellationTokenSource();
        var readerExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        long readerIterations = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!stop.Token.IsCancellationRequested)
                {
                    var result = await _repo.SearchByEmbeddingAsync(query, 5);
                    // A torn/inconsistent snapshot would surface as an exception during scoring
                    // (mismatched array lengths) or, at minimum, a result count that can never
                    // legitimately exceed the requested topK.
                    if (result.Count > 5)
                    {
                        throw new InvalidOperationException($"Got {result.Count} results for topK=5 — torn snapshot.");
                    }
                    Interlocked.Increment(ref readerIterations);
                }
            }
            catch (Exception ex)
            {
                readerExceptions.Add(ex);
            }
        })).ToArray();

        // Writer: repeatedly insert new embedded articles and invalidate, forcing many concurrent
        // rebuilds while the readers above are hammering the cache. InsertArticleAsync writes via
        // raw SQL and does not itself call Invalidate() (see the invalidation tests above for why)
        // — call it explicitly here so this test actually exercises concurrent rebuilds, not just
        // concurrent reads of one never-invalidated snapshot.
        for (int round = 0; round < 40; round++)
        {
            await InsertArticleAsync($"Churn {round}", RandomVector(random, Dim));
            _cache.Invalidate();
        }

        stop.Cancel();
        bool completedInTime = true;
        try
        {
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            completedInTime = false;
        }

        completedInTime.Should().BeTrue("reader threads must observe cancellation and exit promptly, not hang");
        readerExceptions.Should().BeEmpty("no reader should ever see a torn/inconsistent cache snapshot or throw");
        Interlocked.Read(ref readerIterations).Should().BeGreaterThan(0, "readers must have actually done meaningful concurrent work for this test to mean anything");
    }

    // --- ACL / pre-existing quirk preserved --------------------------------------------

    [Fact]
    public async Task SearchByEmbeddingAsync_FullRowsHydratedForTopKOnly()
    {
        var random = new Random(11);
        var query = RandomVector(random, Dim);
        var id = await InsertArticleAsync("Findable", query);

        var result = await _repo.SearchByEmbeddingAsync(query, 5);

        result.Should().ContainSingle(a => a.Id == id);
        result.Single(a => a.Id == id).Title.Should().Be("Findable", "pass 2 must still hydrate full Article rows, not just ids");
    }
}
