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
    public async Task Invalidate_UpdateEmbeddingUnscopedAsync_RebuildsWithNewVector()
    {
        var random = new Random(9);
        var original = RandomVector(random, Dim);
        var id = await InsertArticleAsync("Doc", original);

        // Warm the cache with the original vector.
        await _repo.SearchByEmbeddingAsync(original, 5);
        var rebuildsAfterWarm = _cache.RebuildCount;

        // UpdateEmbeddingUnscopedAsync rewrites the projection directly -- this must patch the
        // cache (incrementally, not via a full rebuild -- see the RebuildCount assertion below).
        var replacement = RandomVector(random, Dim);
        await _repo.UpdateEmbeddingUnscopedAsync(id, MemoryMarshal.AsBytes(replacement.AsSpan()).ToArray(), "v2");

        _cache.RebuildCount.Should().Be(rebuildsAfterWarm,
            "UpdateEmbeddingUnscopedAsync already has the new bytes in hand -- it must patch the " +
            "single changed row into the cache instead of forcing a full SQL rebuild");

        var resultForReplacement = await _repo.SearchByEmbeddingAsync(replacement, 5);
        resultForReplacement.Should().ContainSingle(a => a.Id == id, "UpdateEmbeddingUnscopedAsync must invalidate/patch the cache so the new vector is immediately searchable");
    }

    // --- UpdateOne (incremental patch) -------------------------------------------------

    [Fact]
    public async Task UpdateOne_NoSnapshotPublishedYet_IsANoOpAndFirstSearchStillWorks()
    {
        var random = new Random(101);
        var vec = RandomVector(random, Dim);
        var id = await InsertArticleAsync("Doc", null); // no embedding at insert time

        // Cache has never been built (_current is still null) -- UpdateOne must not throw and must
        // not need to do anything, since the next real read builds fresh from SQL anyway.
        var updated = MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();
        // Directly exercise the raw SQL write + UpdateOne pairing UpdateEmbeddingUnscopedAsync does,
        // without going through the repo method (which would also update tbl_article itself) --
        // here we only care about the cache's own behavior before anything is published.
        using (var conn = _factory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = @bytes WHERE id = @id", new { bytes = updated, id });
        }
        _cache.UpdateOne(id, updated);

        _cache.RebuildCount.Should().Be(0, "UpdateOne must not force a rebuild when nothing has been published yet");

        var result = await _repo.SearchByEmbeddingAsync(vec, 5);
        result.Should().ContainSingle(a => a.Id == id, "the first real read must build a fresh snapshot straight from SQL and find the row");
        _cache.RebuildCount.Should().Be(1, "the first GetOrRebuild call after nothing was published must do exactly one full rebuild");
    }

    [Fact]
    public async Task UpdateOne_NewCandidateAfterWarm_AppendsWithoutFullRebuild()
    {
        var random = new Random(102);
        var seedVec = RandomVector(random, Dim);
        var seedId = await InsertArticleAsync("Seed", seedVec);

        // Warm the cache -- the new article below does not exist in this snapshot yet.
        await _repo.SearchByEmbeddingAsync(seedVec, 5);
        var rebuildsAfterWarm = _cache.RebuildCount;

        var newVec = RandomVector(random, Dim);
        var newId = await InsertArticleAsync("Fresh", null); // row exists, no embedding at insert time
        using (var conn = _factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_article SET embedding_projection = @bytes WHERE id = @id",
                new { bytes = MemoryMarshal.AsBytes(newVec.AsSpan()).ToArray(), id = newId });
        }
        _cache.UpdateOne(newId, MemoryMarshal.AsBytes(newVec.AsSpan()).ToArray());

        _cache.RebuildCount.Should().Be(rebuildsAfterWarm,
            "appending a brand-new candidate must patch the snapshot in place, not force a full SQL rebuild");

        var result = await _repo.SearchByEmbeddingAsync(newVec, 5);
        result.Should().ContainSingle(a => a.Id == newId, "the appended row must be immediately searchable");
        _cache.RebuildCount.Should().Be(rebuildsAfterWarm, "reading the patched snapshot must not trigger a rebuild either");
    }

    [Fact]
    public async Task UpdateOne_DimensionConflict_FallsBackToFullRebuildRatherThanCorruptTheLayout()
    {
        var random = new Random(103);
        var vec = RandomVector(random, Dim);
        var id = await InsertArticleAsync("Doc", vec);

        await _repo.SearchByEmbeddingAsync(vec, 5); // warm at Dim
        var rebuildsAfterWarm = _cache.RebuildCount;

        // A projection at a DIFFERENT dimension than the snapshot's established one (e.g. mid-way
        // through a projection-matrix swap) cannot be patched into the existing flat D-wide layout.
        var wrongDim = new byte[(Dim + 4) * sizeof(float)];
        _cache.UpdateOne(id, wrongDim);

        // UpdateOne must have forced the generation forward (via a plain Invalidate), so the very
        // next GetOrRebuild call does a full rebuild rather than silently living with a mismatched
        // patch.
        _cache.GetOrRebuild();
        _cache.RebuildCount.Should().Be(rebuildsAfterWarm + 1,
            "a dimension conflict must fall back to a full rebuild rather than risk corrupting the flat vector layout");
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

        // On a CPU-starved CI runner (e.g. GitHub Actions' 2-vCPU ubuntu-latest), the thread pool
        // can fail to actually schedule any of the 4 reader tasks before the writer loop below races
        // ahead and finishes, making "readerIterations > 0" fail for a thread-pool-scheduling reason
        // unrelated to the cache code under test. Block until every reader has completed a first
        // iteration before letting the writer churn start.
        using var readersStarted = new CountdownEvent(4);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            bool signaled = false;
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
                    if (!signaled)
                    {
                        signaled = true;
                        readersStarted.Signal();
                    }
                }
            }
            catch (Exception ex)
            {
                readerExceptions.Add(ex);
                if (!signaled)
                {
                    signaled = true;
                    readersStarted.Signal();
                }
            }
        })).ToArray();

        if (!readersStarted.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Reader threads never completed a first iteration within 30s -- thread pool starvation?");
        }

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
