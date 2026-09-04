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
/// WP-13 Task 4: mirrors this project's target load directly -- 20 concurrent simulated callers
/// continuously issuing <see cref="SearchService.SearchIndexedContentAsync"/> calls while a
/// background writer continuously creates/updates/deletes articles through
/// <see cref="PendingIndexProcessor"/>'s real ingestion path (via <see cref="ArticleService"/>, the
/// same code real production traffic goes through -- not synthetic direct
/// <see cref="IndexBuilder"/> calls), for a bounded wall-clock duration.
///
/// <para>
/// <b>Checked before writing this</b> to confirm this is new coverage: IndexBuilderConcurrencyTests.cs
/// (WP-10) stresses <see cref="IndexBuilder"/> directly with a synthetic writer thread calling
/// <c>AddOrUpdateDocument</c>/<c>RemoveDocument</c> in a tight loop -- no <see cref="ArticleService"/>,
/// no encryption, no DB, no <see cref="PendingIndexProcessor"/>, no <see cref="SearchService"/>.
/// SearchIndexLifecycleIntegrationTests.cs (WP-11) never runs concurrent load at all -- every test
/// there is sequential. This test is the first to combine the real end-to-end write path (article
/// create/update/delete -> index_pending -> PendingIndexProcessor -> IndexBuilder -> persisted
/// segment) with sustained concurrent read load through the real public search entry point, sized
/// to this project's stated ~20-concurrent-caller target.
/// </para>
///
/// <para>
/// <b>Finding:</b> no new gap found. Across repeated runs (this test was run several times
/// back-to-back per the WP-13 brief's flakiness check -- see wp-13-report.md), no exception ever
/// escaped either the writer or any of the 20 reader loops, the run never hung, and every article
/// the writer confirmed as fully processed (index_pending cleared) before the stress window ended
/// remained findable by <see cref="SearchService.SearchIndexedContentAsync"/> once the window
/// closed.
/// </para>
/// </summary>
public class SustainedConcurrentLoadTests : IAsyncLifetime
{
    private const string Password = "wp13-task4-test-password";
    private const int ReaderCount = 20;
    private static readonly TimeSpan StressDuration = TimeSpan.FromSeconds(10);

    private string _dbPath = null!;
    private string _segmentsDir = null!;

    public Task InitializeAsync()
    {
        DapperConfig.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"bmb_wp13_t4_{Guid.NewGuid():N}.db");
        _segmentsDir = Path.Combine(Path.GetTempPath(), $"bmb_wp13_t4_segments_{Guid.NewGuid():N}");
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
    public async Task TwentyConcurrentSearchers_PlusRealIngestionWriter_SustainedLoad_NoExceptionsNoHangEventuallyConsistent()
    {
        var node = await CreateNode();

        // Pre-seed content BEFORE the stress window starts, fully processed, so readers always
        // have at least something guaranteed-findable to query from iteration 1.
        const int preseedCount = 15;
        var preseedTerms = new List<string>();
        for (int i = 0; i < preseedCount; i++)
        {
            string term = $"preseedmarker{i}";
            await node.ArticleService.CreateAsync($"Preseed {i}", "/", [], $"{term} preseeded content");
            preseedTerms.Add(term);
        }
        await node.Processor.ProcessPendingAsync(CancellationToken.None);
        await node.Processor.ProcessPendingAsync(CancellationToken.None);

        foreach (string term in preseedTerms)
        {
            (await node.SearchService.SearchIndexedContentAsync(term, topK: 5)).Should().NotBeEmpty(
                "pre-seed sanity: content indexed before the stress window starts must already be findable");
        }

        // Confirmed-findable set: only ever added to by the single writer thread, immediately after
        // it has verified (via IndexPending == false) that PendingIndexProcessor actually finished
        // indexing that specific article -- this is the "fully processed before the window ends"
        // set the final assertion checks.
        var confirmed = new ConcurrentBag<(string Term, Guid ArticleId)>();
        var writerExceptions = new ConcurrentBag<Exception>();
        var readerExceptions = new ConcurrentBag<Exception>();
        long readerIterations = 0;

        // Shared, thread-safe pool of terms readers can pick from -- starts with the pre-seed
        // terms so readers always have something to query even before the writer confirms its
        // first tracked article.
        var queryablePool = new ConcurrentBag<string>(preseedTerms);

        using var stop = new CancellationTokenSource();

        Task writer = Task.Run(async () =>
        {
            try
            {
                var random = new Random(20260812);

                // Two DELIBERATELY SEPARATE pools: "tracked" articles are created once and never
                // touched again (they are the content whose eventual findability the final
                // assertion checks -- update/delete churn must never land on one of these, or a
                // later delete of a "confirmed findable" article would fail the final check for a
                // reason that has nothing to do with the search index itself). "churn" articles are
                // a disposable pool that only exists to stress the update/delete write path
                // concurrently with search reads; their eventual findability is deliberately not
                // tracked/asserted.
                var churnPool = new List<Guid>();
                DateTime deadline = DateTime.UtcNow + StressDuration;
                int trackedIndex = 0;

                while (DateTime.UtcNow < deadline)
                {
                    double roll = random.NextDouble();
                    if (roll < 0.6)
                    {
                        // Create a "tracked" article with a unique, never-reused term -- this is
                        // the content whose eventual findability the final assertion checks.
                        string term = $"trackedmarker{trackedIndex++}_{Guid.NewGuid():N}";
                        Article article = await node.ArticleService.CreateAsync(
                            $"Tracked {term}", "/", [], $"{term} tracked stress content");

                        await node.Processor.ProcessPendingAsync(CancellationToken.None);

                        Article? reloaded = await node.ArticleRepo.GetByIdAsync(article.Id);
                        if (reloaded is { IndexPending: false })
                        {
                            confirmed.Add((term, article.Id));
                            queryablePool.Add(term);
                        }
                    }
                    else if (roll < 0.7 || churnPool.Count == 0)
                    {
                        // Create a disposable "churn" article -- never added to the tracked set,
                        // deliberately eligible for the update/delete churn below.
                        Article churnArticle = await node.ArticleService.CreateAsync(
                            "Churn", "/", [], $"churn seed content {Guid.NewGuid():N}");
                        churnPool.Add(churnArticle.Id);
                        if (churnPool.Count > 40)
                        {
                            churnPool.RemoveAt(0); // bound the pool so churn stays on recent articles
                        }

                        await node.Processor.ProcessPendingAsync(CancellationToken.None);
                    }
                    else if (roll < 0.85)
                    {
                        // Update an existing CHURN article's content through the real write path --
                        // pure stress on the ingestion pipeline; this WP does not track/assert the
                        // post-update term's findability (article soft-delete/update-driven
                        // tombstoning of the search index is WP-11's documented, separately-scoped
                        // concern -- see SearchIndexLifecycleIntegrationTests.cs's own remarks).
                        Guid id = churnPool[random.Next(churnPool.Count)];
                        await node.ArticleService.UpdateAsync(id, plaintext: $"updated content {Guid.NewGuid():N}");
                        await node.Processor.ProcessPendingAsync(CancellationToken.None);
                    }
                    else
                    {
                        // Delete an existing CHURN article through the real write path -- never a
                        // tracked one.
                        int index = random.Next(churnPool.Count);
                        Guid id = churnPool[index];
                        churnPool.RemoveAt(index);
                        await node.ArticleService.DeleteAsync(id);
                    }
                }
            }
            catch (Exception ex)
            {
                writerExceptions.Add(ex);
            }
            finally
            {
                stop.Cancel();
            }
        });

        Task[] readers = Enumerable.Range(0, ReaderCount).Select(_ => Task.Run(async () =>
        {
            var random = new Random(Guid.NewGuid().GetHashCode());
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    string[] snapshot = queryablePool.ToArray();
                    if (snapshot.Length == 0)
                    {
                        continue;
                    }

                    string term = snapshot[random.Next(snapshot.Length)];
                    try
                    {
                        List<Article> results = await node.SearchService.SearchIndexedContentAsync(term, topK: 20);
                        results.Should().NotBeNull();
                    }
                    catch (Exception ex)
                    {
                        readerExceptions.Add(ex);
                    }

                    Interlocked.Increment(ref readerIterations);
                }
            }
            catch (Exception ex)
            {
                // Should be unreachable (the inner try/catch above handles the actual search call),
                // but captured defensively so a bug in the loop scaffolding itself is not confused
                // with a hang.
                readerExceptions.Add(ex);
            }
        })).ToArray();

        bool completedInTime = true;
        try
        {
            // This bound is a hang detector, not a performance budget: a deadlock never finishes,
            // while 21 tasks doing real indexing and search work on a shared two-core CI runner
            // legitimately take minutes — a sibling test in this same assembly was observed taking
            // over four minutes in the run where 60 seconds failed here. Generous enough that only
            // a genuine hang trips it.
            await Task.WhenAll([writer, .. readers]).WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException)
        {
            completedInTime = false;
        }

        completedInTime.Should().BeTrue("a hang anywhere in the write or search path must fail this test loudly via the explicit bounded timeout, not hang the whole test run");
        writerExceptions.Should().BeEmpty("the real create/update/delete ingestion path must never throw under concurrent search load");
        readerExceptions.Should().BeEmpty("no concurrent SearchIndexedContentAsync call may ever throw while a background writer is churning content");
        readerIterations.Should().BeGreaterThan(0, "readers must have actually run concurrently with the writer");
        confirmed.Should().NotBeEmpty("the writer must have actually confirmed at least one tracked article as fully processed during the stress window");

        // The after-the-fact consistency check (same class as IndexBuilderOracleTests'
        // independent-oracle comparison and SearchIndexLifecycleIntegrationTests' post-restart
        // Lookup checks): every article the writer itself confirmed as fully indexed before the
        // window closed must still be findable now.
        foreach ((string term, Guid articleId) in confirmed)
        {
            List<Article> finalResult = await node.SearchService.SearchIndexedContentAsync(term, topK: 5);
            finalResult.Select(a => a.Id).Should().Contain(articleId,
                $"article {articleId} was confirmed fully processed (index_pending cleared) before the stress window ended -- it must remain findable by term '{term}'");
        }
    }

    // ── Test node construction (single long-lived "process", no restart needed for this test) ──

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public float[] Generate(string text) => new float[Dimension];
    }

    private sealed record TestNode(
        IArticleRepository ArticleRepo,
        ArticleService ArticleService,
        PendingIndexProcessor Processor,
        SearchService SearchService);

    private async Task<TestNode> CreateNode()
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
        var eventLogger = new EventLogger(nodeRepo, eventLogRepo, clock, new NullActorProvider(), new SyncTrigger(), session, new BlobRepository(factory));
        var mediaRepo = new MediaRepository(factory, callerScopeHolder);
        var folderRepo = new FolderRepository(factory, callerScopeHolder);
        var versionRepo = new ArticleVersionRepository(factory, callerScopeHolder);
        var conceptTagRepo = new ConceptTagRepository(factory, callerScopeHolder);
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), eventLogger);
        var articleService = new ArticleService(articleRepo, bodyRepo, session, nodeRepo, clock, eventLogger,
            mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, factory);

        var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, factory);
        await initService.InitializeAsync("admin", "TestNode", Password, canGenerateEmbeddings: false);
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var manifestRepo = new SegmentManifestRepository(factory);
        var tombstoneRepo = new SegmentTombstoneRepository(factory);
        var segmentStore = new EncryptedSegmentStore(manifestRepo, session, _segmentsDir);

        var builder = new IndexBuilder(hotBufferSealThreshold: 25, mergeSegmentCountThreshold: 6, mergeTombstoneFractionThreshold: 0.25);
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

        return new TestNode(articleRepo, articleService, processor, searchService);
    }
}
