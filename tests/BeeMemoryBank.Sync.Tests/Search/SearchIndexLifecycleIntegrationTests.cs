using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Search.Segment;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests.Search;

/// <summary>
/// WP-11's Definition of Done integration tests: restart-recovery (unlock warm-start reloads a
/// persisted segment and its content is immediately findable), tombstone-survives-restart (Gap 2),
/// corrupted-segment-triggers-rebuild (the conservative full-rebuild-on-any-failure path), and
/// locked-session no-op.
///
/// <para>
/// "Restart" is simulated literally: each "process" is an entirely separate object graph (its own
/// <see cref="DbConnectionFactory"/>, <see cref="SessionService"/>, <see cref="IndexBuilder"/>,
/// etc., built by <see cref="CreateNode"/>) pointed at the SAME on-disk SQLite file and segments
/// directory a prior "process" used -- exactly what happens across a real process restart, since
/// <see cref="DbConnectionFactory"/>'s public constructor (unlike its <c>CreateInMemory</c> test
/// helper) always backs onto a real file. The second "process" unlocks with the same password
/// against the already-initialized DB, deriving the identical master DEK the first process used --
/// the same thing a real re-login after a restart does.
/// </para>
/// </summary>
public class SearchIndexLifecycleIntegrationTests : IAsyncLifetime
{
    private const string Password = "wp11-test-password";
    private string _dbPath = null!;
    private string _segmentsDir = null!;

    public Task InitializeAsync()
    {
        DapperConfig.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"bmb_wp11_{Guid.NewGuid():N}.db");
        _segmentsDir = Path.Combine(Path.GetTempPath(), $"bmb_wp11_segments_{Guid.NewGuid():N}");
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

    // ── DoD test 1: restart recovery ────────────────────────────────────────────────

    [Fact]
    public async Task Restart_WarmStartReloadsPersistedSegment_ContentImmediatelyFindableWithoutReindexing()
    {
        var node1 = await CreateNode(initialize: true);
        var article = await node1.ArticleService.CreateAsync("Doc 1", "/", [], "unique restartable content alpha");

        // Force at least one seal: threshold is set low in CreateNode (see its comment), so a
        // single article already crosses it.
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        node1.Builder.SealCount.Should().BeGreaterThan(0, "test setup must force a real seal+persist, not just a hot-buffer add");

        var freshArticle = await node1.ArticleRepo.GetByIdAsync(article.Id);
        freshArticle!.IndexPending.Should().BeFalse("PendingIndexProcessor must have cleared it after indexing");

        // Simulate a full process restart: brand-new object graph, same DB file + segments dir.
        var node2 = await CreateNode(initialize: false);
        await node2.Processor.ProcessPendingAsync(CancellationToken.None); // warm-start only; nothing pending to reindex

        node2.Builder.Lookup(Stem("restartable")).Should().Contain(article.Id, "warm-start must have adopted the persisted segment, making its content immediately findable");
        node2.Builder.SealCount.Should().Be(0, "content came from warm-start adoption, not a fresh reindex/seal");
    }

    // ── DoD test 2: tombstone survives restart ──────────────────────────────────────

    [Fact]
    public async Task Tombstone_UpdateBeforeRestart_StaleContentDoesNotReappearAfterWarmStart()
    {
        var node1 = await CreateNode(initialize: true);
        var article = await node1.ArticleService.CreateAsync("Doc 2", "/", [], "original staleterm content");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        node1.Builder.SealCount.Should().BeGreaterThan(0);
        node1.Builder.Lookup(Stem("staleterm")).Should().Contain(article.Id);

        // Update the article's content -- this tombstones the old sealed-segment occurrence (in
        // memory) and durably persists that tombstone (Gap 2's fix) before the process "restarts".
        await node1.ArticleService.UpdateAsync(article.Id, plaintext: "replacement freshterm content");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        node1.Builder.Lookup(Stem("staleterm")).Should().BeEmpty("tombstoned in-process, before any restart");
        node1.Builder.Lookup(Stem("freshterm")).Should().Contain(article.Id);

        var node2 = await CreateNode(initialize: false);
        await node2.Processor.ProcessPendingAsync(CancellationToken.None);

        node2.Builder.Lookup(Stem("staleterm")).Should().BeEmpty("the durable tombstone must have survived the restart -- stale content must not be resurrected");
        node2.Builder.Lookup(Stem("freshterm")).Should().Contain(article.Id, "the fresh content (indexed after the update) must still be findable");
    }

    [Fact]
    public async Task Tombstone_RemoveDocumentAgainstPersistedSegment_DurablyPersistedAndSurvivesReload()
    {
        // Exercises Gap 2's persistence primitive directly against a delete-shaped tombstone
        // (IndexBuilder.RemoveDocument), independent of whatever future product hook eventually
        // calls it for a real article soft-delete (out of this WP's declared scope -- see
        // wp-11-report.md) -- this proves the durability plumbing itself is correct for both the
        // "update" and "delete" shapes of a tombstone.
        var node1 = await CreateNode(initialize: true);
        var article = await node1.ArticleService.CreateAsync("Doc 3", "/", [], "deleteme uniqueterm content");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        node1.Builder.SealCount.Should().BeGreaterThan(0);

        var tombstoneEvents = node1.Builder.RemoveDocument(article.Id);
        await node1.Lifecycle.PersistTombstonesAsync(tombstoneEvents, CancellationToken.None);
        node1.Builder.Lookup(Stem("uniqueterm")).Should().BeEmpty();

        var node2 = await CreateNode(initialize: false);
        await node2.Processor.ProcessPendingAsync(CancellationToken.None);

        node2.Builder.Lookup(Stem("uniqueterm")).Should().BeEmpty("the durable delete-tombstone must survive the restart");
    }

    // ── DoD test 3: corrupted segment triggers full rebuild ─────────────────────────

    [Fact]
    public async Task CorruptedSegmentFile_TriggersFullRebuild_ResetsIndexPendingInsteadOfCrashingOrPartialIndex()
    {
        var node1 = await CreateNode(initialize: true);
        var article1 = await node1.ArticleService.CreateAsync("Doc A", "/", [], "content alpha");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        node1.Builder.SealCount.Should().BeGreaterThan(0, "test setup must persist at least one real segment to corrupt");

        // A second article that is NOT touched by the corruption below -- proves the rebuild is a
        // broad "re-flag everything" response, not a partial recovery scoped to just the bad segment.
        var article2 = await node1.ArticleService.CreateAsync("Doc B", "/", [], "content beta");

        var manifests = await node1.ManifestRepo.GetAllManifestsAsync();
        manifests.Should().NotBeEmpty();
        byte[] bytes = await File.ReadAllBytesAsync(manifests[0].FilePath);
        bytes[bytes.Length - 3] ^= 0xFF; // flip a byte inside the last block's ciphertext/tag
        await File.WriteAllBytesAsync(manifests[0].FilePath, bytes);

        var node2 = await CreateNode(initialize: false);

        // Call the warm-start step directly first (rather than the whole ProcessPendingAsync
        // cycle) so the rebuild's effects can be observed before this WP's own low test threshold
        // (hotBufferSealThreshold: 1, see CreateNode) immediately reindexes+reseals the
        // newly-re-flagged articles within the same cycle -- that immediate self-healing is real
        // and desirable, just not what this assertion block is checking.
        Func<Task> warmStart = () => node2.Lifecycle.EnsureWarmStartedAsync(CancellationToken.None);
        await warmStart.Should().NotThrowAsync("a corrupted segment must trigger a rebuild, never crash");

        (await node2.ManifestRepo.GetAllManifestsAsync()).Should().BeEmpty("the full-rebuild path must clear the manifest rather than leave a half-trustworthy state");
        node2.Builder.SealedSegmentCount.Should().Be(0, "nothing should have been adopted from a manifest that included a corrupted segment");

        var reloadedArticle1 = await node2.ArticleRepo.GetByIdAsync(article1.Id);
        var reloadedArticle2 = await node2.ArticleRepo.GetByIdAsync(article2.Id);
        reloadedArticle1!.IndexPending.Should().BeTrue("full rebuild re-flags EVERY active article, including ones with no connection to the corrupted segment");
        reloadedArticle2!.IndexPending.Should().BeTrue();

        // The full processor cycle (warm-start already resolved to "rebuild" above, so this call's
        // own EnsureWarmStartedAsync is a no-op) must also never crash, and self-heals by
        // reindexing the now-re-flagged articles from scratch.
        Func<Task> fullCycle = () => node2.Processor.ProcessPendingAsync(CancellationToken.None);
        await fullCycle.Should().NotThrowAsync();
        (await node2.ArticleRepo.GetByIdAsync(article1.Id))!.IndexPending.Should().BeFalse("the processor must have reindexed the re-flagged article from scratch");
    }

    // ── DoD test 4: locked session is a no-op ───────────────────────────────────────

    [Fact]
    public async Task LockedSession_ProcessPendingAsync_DoesNothing_NoException()
    {
        var node = await CreateNode(initialize: true);
        await node.ArticleService.CreateAsync("Doc Locked", "/", [], "content while unlocked");
        node.Session.Lock();

        Func<Task> act = () => node.Processor.ProcessPendingAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        node.Builder.SealedSegmentCount.Should().Be(0, "a locked session must skip the whole cycle, including warm-start");
        node.Builder.HotBufferCount.Should().Be(0);
    }

    // ── Test node construction ──────────────────────────────────────────────────────

    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();
    private static string Stem(string word) => Stemmer.Stem(Tokenizer.Tokenize(word).First());

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public int Dimension => 384;
        public float[] Generate(string text) => new float[Dimension];
    }

    private sealed record TestNode(
        DbConnectionFactory Factory,
        SessionService Session,
        IArticleRepository ArticleRepo,
        ArticleService ArticleService,
        SegmentManifestRepository ManifestRepo,
        SegmentTombstoneRepository TombstoneRepo,
        IndexBuilder Builder,
        SearchIndexLifecycleService Lifecycle,
        PendingIndexProcessor Processor);

    /// <summary>
    /// Builds one full "process" object graph against <see cref="_dbPath"/>/<see cref="_segmentsDir"/>.
    /// <paramref name="initialize"/> true means this is the first process (runs migrations, creates
    /// the node identity + admin user, unlocks with <see cref="Password"/> for the first time);
    /// false means this is a "restarted" process that just re-unlocks against the already-initialized
    /// DB. Hot-buffer/merge thresholds are set to 1 so a single article's worth of content always
    /// forces a real seal+persist, keeping these tests fast without needing hundreds of articles.
    /// </summary>
    private async Task<TestNode> CreateNode(bool initialize)
    {
        var factory = new DbConnectionFactory(_dbPath);
        var runner = new MigrationRunner(factory);
        await runner.RunMigrationsAsync();

        var articleRepo = new ArticleRepository(factory, new CallerScopeHolder());
        var bodyRepo = new ArticleBodyRepository(factory);
        var keySlotRepo = new KeySlotRepository(factory);
        var nodeRepo = new NodeIdentityRepository(factory);
        var userRepo = new UserRepository(factory);
        var eventLogRepo = new EventLogRepository(factory);
        var clock = new LamportClock();
        clock.Initialize(await eventLogRepo.GetMaxLamportTimestampAsync());

        var session = new SessionService(keySlotRepo);
        var eventLogger = new EventLogger(nodeRepo, eventLogRepo, clock, new NullActorProvider(), new SyncTrigger(), session);
        var mediaRepo = new MediaRepository(factory, new CallerScopeHolder());
        var folderRepo = new FolderRepository(factory, new CallerScopeHolder());
        var versionRepo = new ArticleVersionRepository(factory, new CallerScopeHolder());
        var conceptTagRepo = new ConceptTagRepository(factory, new CallerScopeHolder());
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), eventLogger);
        var articleService = new ArticleService(articleRepo, bodyRepo, session, nodeRepo, clock, eventLogger,
            mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService);

        if (initialize)
        {
            var initService = new InitializationService(nodeRepo, keySlotRepo, userRepo, factory);
            await initService.InitializeAsync("admin", "TestNode", Password, canGenerateEmbeddings: false);
        }

        (await session.UnlockAsync(Password)).Should().BeTrue("unlocking with the password used at initialization must always succeed");

        var manifestRepo = new SegmentManifestRepository(factory);
        var tombstoneRepo = new SegmentTombstoneRepository(factory);
        var segmentStore = new EncryptedSegmentStore(manifestRepo, session, _segmentsDir);

        // Threshold 1: every AddOrUpdateDocument call seals immediately, so these tests can force a
        // real seal+persist cycle with a single article instead of hundreds.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
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

        return new TestNode(factory, session, articleRepo, articleService, manifestRepo, tombstoneRepo, builder, lifecycle, processor);
    }
}
