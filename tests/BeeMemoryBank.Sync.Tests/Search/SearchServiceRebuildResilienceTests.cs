using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests.Search;

/// <summary>
/// WP-13 Task 3: concurrent search load through the actual public entry point
/// (<see cref="SearchService.SearchIndexedContentAsync"/>) while a full index rebuild is in
/// progress, triggered the same way <see cref="SearchIndexLifecycleService.TriggerFullRebuildAsync"/>
/// is triggered in production -- a corrupted segment discovered during warm-start.
///
/// <para>
/// <b>Checked before writing this</b> to confirm this combination is genuinely new coverage:
/// IndexBuilderConcurrencyTests.cs (WP-10) stresses concurrent readers directly against
/// <see cref="IndexBuilder.GetSealedSegments"/>/<c>Lookup</c> while a writer thread churns
/// add/update/remove/seal/merge -- it never touches persistence, warm-start, or a rebuild trigger
/// at all. SearchIndexLifecycleIntegrationTests.cs (WP-11) exercises the corrupted-segment ->
/// full-rebuild trigger in isolation, sequentially, with no concurrent readers during the rebuild
/// window. Neither test calls <see cref="SearchService"/> at all -- both go directly through
/// <see cref="IndexBuilder"/>/<see cref="SearchIndexLifecycleService"/>. This test is the first to
/// combine all three: real concurrent callers hitting the actual DI-wired public search entry
/// point, during a real rebuild-triggering warm-start, while the background reindex catches up.
/// </para>
///
/// <para>
/// <b>Finding:</b> no new gap found. Every concurrent <see cref="SearchService.SearchIndexedContentAsync"/>
/// call completes without throwing throughout the corrupted-segment discovery, the full-rebuild
/// re-flagging, and the subsequent catch-up reindexing; results are always either empty/reduced
/// (while re-flagged articles are still being reprocessed) or fully correct (once reprocessing
/// catches up), never partial/wrong data for an article that IS returned. This matches the design:
/// <see cref="IndexBuilder"/>'s copy-on-write sealed-segment list and per-call locking (verified
/// safe under concurrent churn by WP-10's own test) are exactly what
/// <see cref="IndexBuilder.SearchRanked"/> reads from, and <see cref="SearchService.SearchIndexedContentAsync"/>
/// adds nothing on top beyond a DB hydrate + ACL filter that cannot itself corrupt data (worst case
/// it returns a stale, since-rebuilt article's OLD ranked position, never a wrong article
/// altogether, since <c>GetByIdsAsync</c> only ever returns rows that genuinely exist).
/// </para>
/// </summary>
public class SearchServiceRebuildResilienceTests : IAsyncLifetime
{
    private const string Password = "wp13-task3-test-password";
    private string _dbPath = null!;
    private string _segmentsDir = null!;

    public Task InitializeAsync()
    {
        DapperConfig.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"bmb_wp13_t3_{Guid.NewGuid():N}.db");
        _segmentsDir = Path.Combine(Path.GetTempPath(), $"bmb_wp13_t3_segments_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext); } catch { /* best-effort */ }
        }
        if (Directory.Exists(_segmentsDir))
        {
            try { Directory.Delete(_segmentsDir, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentSearchIndexedContentAsync_DuringFullRebuildTriggeredByCorruptedSegment_NeverThrowsNeverReturnsCorruptedData()
    {
        const int articleCount = 90; // multiple GetIndexPendingAsync(50) batches, so reindexing spans several ProcessPendingAsync calls
        const string sharedTerm = "resiliencesharedmarker";

        var node1 = await CreateNode(initialize: true);
        var articleIds = new List<Guid>();
        for (int i = 0; i < articleCount; i++)
        {
            var article = await node1.ArticleService.CreateAsync(
                $"Doc {i}", "/", [], $"{sharedTerm} uniqueword{i} filler content for article number {i}");
            articleIds.Add(article.Id);
        }

        await DrainPendingAsync(node1.Processor);
        node1.Builder.SealCount.Should().BeGreaterThan(0, "test setup must persist real segments to corrupt");

        // Corrupt one persisted segment on disk -- same technique as
        // SearchIndexLifecycleIntegrationTests.CorruptedSegmentFile_TriggersFullRebuild... .
        var manifests = await node1.ManifestRepo.GetAllManifestsAsync();
        manifests.Should().NotBeEmpty();
        byte[] bytes = await File.ReadAllBytesAsync(manifests[0].FilePath);
        bytes[bytes.Length - 3] ^= 0xFF;
        await File.WriteAllBytesAsync(manifests[0].FilePath, bytes);

        // "Restart": a fresh node against the same DB/segments dir, whose first warm-start attempt
        // will discover the corruption and trigger a full rebuild (re-flagging every article as
        // index_pending), then must catch back up via ordinary ProcessPendingAsync cycles.
        var node2 = await CreateNode(initialize: false);

        var stop = new CancellationTokenSource();
        var searchExceptions = new ConcurrentBag<Exception>();
        var observedNonEmptyResult = false;
        var searchIterations = 0L;

        const int readerCount = 8;
        // Rendezvous before the rebuild starts. The drain below cancels the readers the moment it
        // finishes, and on a loaded CI runner the thread pool may not have started them by then --
        // the loop body would never run, searchIterations would be 0, and the test would fail
        // claiming the readers never overlapped the rebuild (which is what it actually did on
        // GitHub Actions, repeatedly, while passing on every developer machine). Waiting until
        // every reader has completed one real search makes the overlap a fact instead of a hope.
        using var readersRunning = new CountdownEvent(readerCount);

        Task[] readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(async () =>
        {
            var signalledRunning = false;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    List<Article> results = await node2.SearchService.SearchIndexedContentAsync(sharedTerm, topK: articleCount + 10);

                    // Never wrong/corrupted: every returned id must be one this test actually
                    // created, and the count can never exceed what was ever indexed.
                    results.Count.Should().BeLessOrEqualTo(articleCount);
                    foreach (Article article in results)
                    {
                        articleIds.Should().Contain(article.Id, "a search result must never reference an article this test did not create");
                    }

                    if (results.Count > 0)
                    {
                        Volatile.Write(ref observedNonEmptyResult, true);
                    }
                }
                catch (Exception ex)
                {
                    searchExceptions.Add(ex);
                }

                Interlocked.Increment(ref searchIterations);
                if (!signalledRunning)
                {
                    signalledRunning = true;
                    readersRunning.Signal();
                }
            }
        })).ToArray();

        readersRunning
            .Wait(TimeSpan.FromSeconds(60))
            .Should().BeTrue("every reader must have completed a search before the rebuild is triggered, "
                             + "otherwise the concurrency this test exists to cover never happens");

        // Drive warm-start (discovers corruption -> full rebuild) and the catch-up reindex cycles
        // concurrently with the reader loops above -- this is the actual "search fired while a
        // rebuild is in progress" window the brief asks for, not just before/after it.
        Exception? writerException = null;
        try
        {
            await DrainPendingAsync(node2.Processor);
        }
        catch (Exception ex)
        {
            writerException = ex;
        }
        finally
        {
            stop.Cancel();
        }

        bool completedInTime = true;
        try
        {
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            completedInTime = false;
        }

        writerException.Should().BeNull("the rebuild-triggering warm-start plus catch-up reindex must never throw");
        completedInTime.Should().BeTrue("reader loops must observe cancellation and exit promptly, not hang");
        searchExceptions.Should().BeEmpty("no exception of any kind -- including one that only manifests through the full DI-wired SearchService path -- may ever escape a concurrent search call, even mid-rebuild");
        searchIterations.Should().BeGreaterThan(0, "readers must actually have run concurrently with the rebuild, not finish before it started");
        searchIterations.Should().BeGreaterThan(readerCount - 1, "the rendezvous above guarantees at least one completed search per reader");

        // Once everything above has settled, the index must be fully, correctly caught up: every
        // single created article findable by the shared term, none missing, none duplicated.
        List<Article> finalResults = await node2.SearchService.SearchIndexedContentAsync(sharedTerm, topK: articleCount + 10);
        finalResults.Select(a => a.Id).Distinct().Count().Should().Be(articleCount, "after the rebuild and full reindex settle, every originally-created article must be findable again -- exactly once each");
    }

    /// <summary>Runs ProcessPendingAsync repeatedly until no articles remain index_pending, bounded so a real hang fails loudly.</summary>
    private static async Task DrainPendingAsync(PendingIndexProcessor processor)
    {
        var drain = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await processor.ProcessPendingAsync(CancellationToken.None);
            }
        });
        await drain.WaitAsync(TimeSpan.FromSeconds(60));
    }

    // ── Test node construction (mirrors SearchIndexLifecycleIntegrationTests.CreateNode, plus SearchService) ──

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public float[] Generate(string text) => new float[Dimension];
    }

    private sealed record TestNode(
        SessionService Session,
        IArticleRepository ArticleRepo,
        ArticleService ArticleService,
        SegmentManifestRepository ManifestRepo,
        IndexBuilder Builder,
        SearchIndexLifecycleService Lifecycle,
        PendingIndexProcessor Processor,
        SearchService SearchService);

    private async Task<TestNode> CreateNode(bool initialize)
    {
        var factory = new DbConnectionFactory(_dbPath);
        var runner = new MigrationRunner(factory);
        await runner.RunMigrationsAsync();

        var callerScopeHolder = new CallerScopeHolder();
        var articleRepo = new ArticleRepository(factory, callerScopeHolder);
        var bodyRepo = new ArticleBodyRepository(factory);
        var keySlotRepo = new KeySlotRepository(factory);
        var nodeRepo = new NodeIdentityRepository(factory);
        var userRepo = new UserRepository(factory);
        var eventLogRepo = new EventLogRepository(factory);
        var clock = new LamportClock();
        clock.Initialize(await eventLogRepo.GetMaxLamportTimestampAsync());

        var session = new SessionService(keySlotRepo);
        var eventLogger = new EventLogger(nodeRepo, eventLogRepo, clock, new NullActorProvider(), new SyncTrigger(), session);
        var mediaRepo = new MediaRepository(factory, callerScopeHolder);
        var folderRepo = new FolderRepository(factory, callerScopeHolder);
        var versionRepo = new ArticleVersionRepository(factory, callerScopeHolder);
        var conceptTagRepo = new ConceptTagRepository(factory, callerScopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), eventLogger);
        var articleService = new ArticleService(articleRepo, bodyRepo, session, nodeRepo, clock, eventLogger,
            mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, factory);

        if (initialize)
        {
            var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, factory);
            await initService.InitializeAsync("admin", "TestNode", Password, canGenerateEmbeddings: false);
        }

        (await session.UnlockAsync(Password)).Should().BeTrue();

        var manifestRepo = new SegmentManifestRepository(factory);
        var tombstoneRepo = new SegmentTombstoneRepository(factory);
        var segmentStore = new EncryptedSegmentStore(manifestRepo, session, _segmentsDir);

        var builder = new IndexBuilder(hotBufferSealThreshold: 20, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        var runtimeState = new SearchIndexRuntimeState();
        var lifecycle = new SearchIndexLifecycleService(
            builder, runtimeState, manifestRepo, segmentStore, tombstoneRepo, articleRepo,
            NullLogger<SearchIndexLifecycleService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IArticleRepository>(articleRepo);
        services.AddSingleton(articleService);
        services.AddSingleton(lifecycle);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var processor = new PendingIndexProcessor(scopeFactory, NullLogger<PendingIndexProcessor>.Instance);

        var queryCache = new SearchQueryCache();
        var searchService = new SearchService(articleRepo, bodyRepo, folderRepo, session, callerScopeHolder, queryCache, builder);

        return new TestNode(session, articleRepo, articleService, manifestRepo, builder, lifecycle, processor, searchService);
    }
}
