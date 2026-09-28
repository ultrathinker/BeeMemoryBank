using System.Buffers.Binary;
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

    /// <summary>
    /// The fallback's other half: a rebuild that only clears the persisted index leaves whatever
    /// <see cref="EnsureWarmStartedAsync"/> had <b>already adopted</b> live in the process-wide
    /// <see cref="IndexBuilder"/>. That is the ordinary case rather than an edge one — the failure
    /// that triggers the fallback (an unreadable segment) is found <i>while adopting</i>, after the
    /// segments before it are already live — and because a rebuild only re-indexes active,
    /// unprotected articles, the adopted content it strands is exactly the content that must never
    /// be served again: an article protected since it was indexed, and one deleted since. Neither is
    /// ever visited again, so their terms and ids stay findable through <c>SearchService</c> for the
    /// rest of the process's life.
    ///
    /// <para>
    /// The failure has to be discovered while <b>adopting</b>, not while loading: the warm-start
    /// loads every segment first and adopts them in a second pass, so a segment that will not load
    /// aborts before anything is adopted at all (see
    /// <c>CorruptedSegmentFile_TriggersFullRebuild_...</c> above). The trigger here is the WP-13
    /// case — a segment whose container decrypts fine but whose payload claims an inner format
    /// version from the future — which only fails in the adoption pass. The two articles sit in
    /// separate persisted segments so that <i>whichever</i> one this pass adopts first is a
    /// since-removed article, so the assertions below cannot pass on ordering luck; the unreadable
    /// segment is moved to the last <c>rowid</c> so the pass has certainly adopted before it fails
    /// (asserted below, not assumed).
    /// </para>
    /// </summary>
    [Fact]
    public async Task WarmStartFallback_AfterAdoptingASegment_DropsTheInMemoryIndex_SoRemovedContentStopsBeingServed()
    {
        var node1 = await CreateNode(initialize: true);

        // Each article seals and persists into its own segment (CreateNode's threshold is 1).
        var protectedArticle = await node1.ArticleService.CreateAsync("Doc P", "/", [], "secretterm content alpha");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        var deletedArticle = await node1.ArticleService.CreateAsync("Doc Q", "/", [], "gonefterm content beta");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);

        node1.Builder.Lookup(Stem("secretterm")).Should().Contain(protectedArticle.Id, "test setup: the article about to be protected is in a persisted segment");
        node1.Builder.Lookup(Stem("gonefterm")).Should().Contain(deletedArticle.Id, "test setup: the article about to be deleted is in a persisted segment");

        // Both now leave the set a rebuild re-indexes -- PendingIndexProcessor indexes only active,
        // unprotected articles, and neither of these will ever be visited again -- while their terms
        // stay in the persisted segments (protecting or deleting an article does not tombstone the
        // index; only a later ingest of that article would).
        await node1.ArticleService.ProtectAsync(protectedArticle.Id, "a-passphrase-1", null);
        await node1.ArticleService.DeleteAsync(deletedArticle.Id);

        Guid unreadableSegmentId = await StoreUnreadableSegmentAsync(node1);
        var manifestOrder = await node1.ManifestRepo.GetAllManifestsAsync();
        manifestOrder[^1].SegmentId.Should().Be(unreadableSegmentId,
            "this test's construction depends on the unreadable segment being adopted (and therefore failing) last, after both real segments are already in the builder");

        var node2 = await CreateNode(initialize: false);
        Func<Task> warmStart = () => node2.Lifecycle.EnsureWarmStartedAsync(CancellationToken.None);
        await warmStart.Should().NotThrowAsync("an unreadable segment must trigger a rebuild, never crash");

        node2.Builder.SealedSegmentCount.Should().Be(0, "the rebuild must drop the segments this same pass had already adopted, not just the manifest rows pointing at them");
        node2.Builder.Lookup(Stem("secretterm")).Should().BeEmpty("a rebuild re-indexes neither a protected article nor anything else it does not index, so only dropping the in-memory index removes its terms");
        node2.Builder.Lookup(Stem("gonefterm")).Should().BeEmpty("nor a deleted article's");

        var reloadedProtected = await node2.ArticleRepo.GetByIdAsync(protectedArticle.Id);
        reloadedProtected!.IndexPending.Should().BeTrue("the rebuild re-flags it, but the processor will skip it for being protected and clear the flag again -- it is never coming back");
        (await IndexPendingAsync(node2, deletedArticle.Id)).Should().BeFalse(
            "a deleted article is not re-flagged at all -- the rebuild re-flags only active ones -- so nothing but dropping the in-memory index takes it out of the index");
    }

    /// <summary>
    /// One article's <c>index_pending</c> flag, read straight from the table: a soft-deleted article
    /// is not returned by <see cref="IArticleRepository.GetByIdAsync"/>, and its flag is exactly what
    /// this test has to look at.
    /// </summary>
    private static async Task<bool> IndexPendingAsync(TestNode node, Guid articleId)
    {
        using var conn = node.Factory.CreateConnection();
        // The id goes in as a Guid, as every other statement binds it (SQLite compares TEXT
        // case-sensitively, so the string form would miss the row).
        long pending = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
            conn, "SELECT index_pending FROM tbl_article WHERE id = @id", new { id = articleId });
        return pending == 1;
    }

    /// <summary>
    /// Persists one real, validly-encrypted segment whose <b>decrypted payload</b> claims an inner
    /// format version from the future -- what a newer node's segment looks like to this build -- and
    /// moves its manifest row to the highest <c>rowid</c>, so the warm-start's adoption pass reaches
    /// it last. The container itself loads fine (that is the point: it is not the load pass this test
    /// needs to fail), so it is rejected only by the inner-version check inside
    /// <see cref="EnsureWarmStartedAsync"/>'s <b>adoption</b> pass -- see
    /// <see cref="SearchIndexLifecycleFormatVersionResilienceTests"/> -- by which time every earlier
    /// segment is already live in the builder. Returns the segment's id so the caller can assert it
    /// really is last.
    /// </summary>
    private async Task<Guid> StoreUnreadableSegmentAsync(TestNode node)
    {
        byte[] segmentBytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["futureterm"]),
        ]);
        BinaryPrimitives.WriteInt32LittleEndian(
            segmentBytes.AsSpan(SegmentLayout.HeaderFormatVersionOffset, 4), SegmentLayout.FormatVersion + 1);

        var store = new EncryptedSegmentStore(node.ManifestRepo, node.Session, _segmentsDir);
        var segmentId = Guid.NewGuid();
        await store.StoreAsync(segmentId, segmentBytes, docCount: 1);

        // GetAllManifestsAsync has no ORDER BY, so the scan walks this ordinary rowid table in
        // ascending rowid order; an explicit rowid above every other row is what makes this segment
        // last rather than merely probably-last (the caller asserts the resulting order).
        using var conn = node.Factory.CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(
            conn,
            "UPDATE tbl_search_index_manifest SET rowid = @rowid WHERE segment_id = @id",
            new { rowid = long.MaxValue, id = segmentId.ToString() });
        return segmentId;
    }

    // ── WP-19: merge output survives a restart ──────────────────────────────────────

    /// <summary>
    /// WP-19's own Definition of Done: this is the "restart simulation" scenario the WP was scoped
    /// around, not just "a file exists on disk" -- it forces a REAL merge (not just a seal),
    /// persists it, simulates a full process restart the same literal way every other test in this
    /// class does (see this class's own doc comment), and then asserts on the two things that
    /// actually matter: (1) warm-start does NOT fall back to <see cref="SearchIndexLifecycleService.TriggerFullRebuildAsync"/>
    /// (the bug this WP fixes turns every restart-after-a-merge into exactly that, re-flagging and
    /// re-decrypting every article in the vault), and (2) every article indexed before the restart
    /// is still findable afterward, with zero fresh seals -- i.e. its content came from warm-start
    /// adoption of the already-merged, already-durable segment, not from a reindex the process had
    /// to redo because the merge's own output was lost.
    /// </summary>
    [Fact]
    public async Task Restart_AfterMerge_WarmStartAdoptsMergedSegmentWithoutFullRebuild_AllDocumentsStillFindable()
    {
        // mergeSegmentCountThreshold: 2 means "more than 2 sealed segments" triggers a merge -- so
        // the 3rd article's own seal (hotBufferSealThreshold is 1, see CreateNode) collapses the
        // first three sealed segments into one merged segment.
        var node1 = await CreateNode(initialize: true, mergeSegmentCountThreshold: 2);

        var articles = new List<Article>();
        for (int i = 0; i < 3; i++)
        {
            Article article = await node1.ArticleService.CreateAsync($"Merge Doc {i}", "/", [], $"uniquemergeterm{i} content");
            articles.Add(article);
            await node1.Processor.ProcessPendingAsync(CancellationToken.None);
        }

        node1.Builder.MergeCount.Should().Be(1, "the 3rd seal must have crossed the count-2 threshold and produced exactly one merge");

        // The scenario the bug report's own "Consequence" bullet describes: an article is edited
        // AFTER its content has already moved into a merge's output, not a raw seal. Its prior
        // occurrence now lives in the MERGED segment, not in any of the three original ones -- so
        // whether that tombstone can be made durable depends entirely on whether the merge itself
        // was ever given a persisted Guid to write a tombstone row against. This is deliberately a
        // sharper reproduction than "just create N articles": with no post-merge edit, every raw
        // seal's articleId set is disjoint from every other, and re-merging stale, never-retired
        // raw seals on a restart would succeed (wastefully, but without error) -- it takes an
        // edited article straddling an old (merged-away) and a new occurrence to actually trip
        // IndexBuilder.MergeLocked's "live in more than one sealed segment" invariant check on
        // warm-start, which is this bug's most severe consequence (bullet 4), not just its manifest-
        // bloat symptom (bullets 1-3, also covered by this test's manifest-shrinks assertion below).
        await node1.ArticleService.UpdateAsync(articles[0].Id, plaintext: "updatedmergeterm content");
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);

        // One more fresh article pushes the segment count back over threshold, forcing a SECOND
        // merge that combines the just-updated article's stale (already-merged) occurrence together
        // with its fresh (just-resealed) one in the very same MergeLocked call -- exactly the
        // configuration that call's own invariant check exists to catch. In-process this succeeds
        // without incident (the first merge's in-memory tombstone on the stale occurrence is applied
        // instantly, regardless of whether it was ever made durable) -- the whole point of this test
        // is that a RESTART's warm-start must reach the same non-conflicting result from disk alone.
        Article article4 = await node1.ArticleService.CreateAsync("Merge Doc 4", "/", [], "uniquemergeterm4 content");
        articles.Add(article4);
        await node1.Processor.ProcessPendingAsync(CancellationToken.None);

        node1.Builder.MergeCount.Should().Be(2, "the post-merge edit plus one more seal must have forced a second, distinct merge");

        // The core of WP-19's fix, checked directly (not just indirectly via the restart below):
        // persisting a merge must RETIRE the manifest rows it consumed, not just add the merged
        // output alongside them. Before this WP, every seal's manifest row would still be sitting
        // here untouched (merge output was never persisted at all) -- the manifest would only ever
        // grow, never shrink. Asserting it is now far smaller than the seal count is a direct proof
        // the retire-on-merge transaction actually ran, independent of whatever warm-start does.
        var manifestsAfterMerges = await node1.ManifestRepo.GetAllManifestsAsync();
        manifestsAfterMerges.Count.Should().BeLessThan(
            node1.Builder.SealCount,
            "merge persistence must retire the manifest rows for every input segment it consumed -- a manifest that still lists every seal ever made (this WP's original bug) would never shrink");

        // Simulate a full process restart: brand-new object graph, same DB file + segments dir --
        // see this class's own doc comment for why this is a faithful restart simulation, not a
        // rough approximation.
        var node2 = await CreateNode(initialize: false, mergeSegmentCountThreshold: 2);

        Func<Task> warmStart = () => node2.Lifecycle.EnsureWarmStartedAsync(CancellationToken.None);
        await warmStart.Should().NotThrowAsync("adopting an already-merged, already-retired manifest must never re-trigger the 'live in more than one sealed segment' invariant that used to force a full rebuild here");

        // The assertion that matters most: warm-start must have actually adopted the persisted
        // segment(s), NOT fallen back to TriggerFullRebuildAsync. A full rebuild would (a) clear the
        // manifest entirely and (b) re-flag every article as index_pending -- both checked directly
        // rather than inferred, so a regression here fails loudly instead of coincidentally passing
        // the Lookup checks below via a fast reindex the test's low seal threshold would happily mask.
        (await node2.ManifestRepo.GetAllManifestsAsync()).Should().NotBeEmpty(
            "a full rebuild would have cleared the manifest -- if this is empty, warm-start silently gave up and fell back to a full reindex instead of adopting the merged segment(s)");

        foreach (Article article in articles)
        {
            Article? reloaded = await node2.ArticleRepo.GetByIdAsync(article.Id);
            reloaded!.IndexPending.Should().BeFalse(
                "a full rebuild re-flags EVERY active article as index_pending -- if any of these is still true, warm-start fell back to rebuilding instead of adopting the merged segment");
        }

        // Every article's CURRENT content must still be findable, purely from warm-start adoption --
        // zero fresh seals proves nothing was reindexed from source; it all came from the persisted,
        // already-merged segment(s). Article 0 is checked against its UPDATED term, not its
        // original one -- the whole point of this test is that only the fresh occurrence must
        // survive, never the stale pre-update one.
        node2.Builder.Lookup(Stem("updatedmergeterm")).Should().Contain(articles[0].Id, "article 0's UPDATED content must be findable after warm-start");
        node2.Builder.Lookup(Stem("uniquemergeterm0")).Should().NotContain(articles[0].Id, "article 0's STALE pre-update content must never resurface after warm-start");
        foreach (Article article in articles.Skip(1))
        {
            string term = article.Title.Replace("Merge Doc ", "uniquemergeterm");
            node2.Builder.Lookup(Stem(term)).Should().Contain(article.Id, $"'{term}' must still be findable after warm-start without any reindexing");
        }

        node2.Builder.SealCount.Should().Be(0, "every article's content came from warm-start adopting the already-merged persisted segment(s), not from a fresh reindex/seal");
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
    public string Version => "fake-v1";
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
    /// DB. Hot-buffer threshold is always 1 so a single article's worth of content always forces a
    /// real seal+persist, keeping these tests fast without needing hundreds of articles.
    /// <paramref name="mergeSegmentCountThreshold"/> defaults to 1000 (never trips) to preserve the
    /// original DoD tests' seal/tombstone-only focus -- WP-19's own merge-persistence restart test
    /// passes a low value instead, specifically to force real merges within a handful of articles.
    /// The two "process" object graphs for the same simulated restart MUST be built with the same
    /// threshold value, since it is not itself persisted anywhere (it is an in-memory IndexBuilder
    /// constructor argument, same as every other threshold) -- a real node keeps a fixed
    /// configuration across restarts, so these tests do too.
    /// </summary>
    private async Task<TestNode> CreateNode(bool initialize, int mergeSegmentCountThreshold = 1000)
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
        var eventLogger = new EventLogger(nodeRepo, eventLogRepo, clock, new NullActorProvider(), new SyncTrigger(), session, new BlobRepository(factory));
        var mediaRepo = new MediaRepository(factory, new CallerScopeHolder());
        var folderRepo = new FolderRepository(factory, new CallerScopeHolder());
        var versionRepo = new ArticleVersionRepository(factory, new CallerScopeHolder());
        var conceptTagRepo = new ConceptTagRepository(factory, new CallerScopeHolder());
        var conceptTagService = new ConceptTagService(conceptTagRepo, new FakeEmbeddingGenerator(), eventLogger);
        var articleService = new ArticleService(articleRepo, bodyRepo, session, nodeRepo, clock, eventLogger,
            mediaRepo, folderRepo, versionRepo, new NullActorProvider(), conceptTagService, factory);

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
        // real seal+persist cycle with a single article instead of hundreds. Tombstone-fraction
        // threshold is fixed at 1.0 (never trips on its own) across every test in this class --
        // only the segment-COUNT threshold varies per-test (see this method's own doc comment),
        // since that is the trigger WP-19's merge-persistence test needs to force deliberately.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: mergeSegmentCountThreshold, mergeTombstoneFractionThreshold: 1.0);
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
