using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Coverage for <see cref="SearchService.SearchWebContentAsync"/> -- the web content-search path
/// (<c>GET /api/search?content=true</c>) now wired to <see cref="SearchService.SearchIndexedContentAsync"/>'s
/// ranked BM25 index, with a fallback restricted to just the <c>index_pending</c> backlog instead of
/// <see cref="SearchService.SearchWithContentAsync"/>'s full linear decrypt-every-body scan.
///
/// <para>
/// Like <see cref="SearchIndexedContentTests"/>, this fixture never runs the actual
/// <c>PendingIndexProcessor</c> background service, so every article this fixture creates starts
/// (and stays) <c>index_pending = 1</c> unless a test explicitly clears it via
/// <see cref="TestFixture.ArticleRepo"/>'s <c>ClearIndexPendingUnscopedAsync</c> -- exactly what that
/// background processor would eventually do, once it has also fed the same content into
/// <see cref="TestFixture.IndexBuilder"/> (which these tests likewise do by hand, mirroring
/// <see cref="SearchIndexedContentTests"/>'s own setup).
/// </para>
/// </summary>
public class SearchWebContentTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    [Fact]
    public async Task SearchWebContentAsync_IndexComplete_ServesFromIndexAlone_WithoutFallingBackToADecryptScan()
    {
        // "Complete" article: indexed AND its index_pending flag cleared, exactly as
        // PendingIndexProcessor would leave it after a real cycle.
        var indexed = await ArticleService.CreateAsync("Indexed", "/", [], "zzzalpha real content, properly indexed");
        IndexBuilder.AddOrUpdateDocument(indexed.Id, indexed.FolderId ?? Guid.Empty, "zzzalpha real content, properly indexed");
        await ArticleRepo.ClearIndexPendingUnscopedAsync(indexed.Id);

        // "Decoy" article: its body also contains the query term, so if SearchWebContentAsync ever
        // fell back to a linear decrypt-and-substring scan despite index_pending being 0 everywhere,
        // this article would be found and this test would catch the regression. Its index_pending
        // flag is cleared too (so the completeness check reports zero backlog) but it is deliberately
        // NEVER fed into IndexBuilder -- simulating, for test purposes only, "the completeness
        // signal says done" being the one and only thing SearchWebContentAsync consults, not
        // IndexBuilder's own (here, intentionally desynced) contents.
        var decoy = await ArticleService.CreateAsync("Decoy", "/", [], "zzzalpha decoy body never actually indexed");
        await ArticleRepo.ClearIndexPendingUnscopedAsync(decoy.Id);

        var results = await SearchService.SearchWebContentAsync("zzzalpha");

        results.Articles.Select(a => a.Id).Should().Equal(
            [indexed.Id],
            "with index_pending == 0 everywhere, only the ranked index's own contents may surface -- " +
            "the decoy article's matching body must NOT be found, because finding it would mean a " +
            "full linear decrypt scan ran despite the index reporting itself complete, which is " +
            "exactly the per-query full-vault-decrypt cost this method exists to avoid");
    }

    [Fact]
    public async Task SearchWebContentAsync_IndexPending_StillFindsThePendingArticle_ViaRestrictedFallback()
    {
        // Already-indexed article: found via the ranked index, same as the test above.
        var indexed = await ArticleService.CreateAsync("Indexed", "/", [], "zzzbeta already indexed content");
        IndexBuilder.AddOrUpdateDocument(indexed.Id, indexed.FolderId ?? Guid.Empty, "zzzbeta already indexed content");
        await ArticleRepo.ClearIndexPendingUnscopedAsync(indexed.Id);

        // Pending article: left at its default index_pending = 1 (ArticleService.CreateAsync does
        // not clear it -- that is PendingIndexProcessor's job, which this fixture does not run), and
        // deliberately never fed into IndexBuilder either, simulating an article the background
        // indexer has not reached yet. It must still be found -- via the restricted linear-scan
        // fallback over just the pending backlog, not via the (empty, for this article) index.
        var pending = await ArticleService.CreateAsync("Pending", "/", [], "zzzbeta not yet indexed at all");

        var results = await SearchService.SearchWebContentAsync("zzzbeta");

        var foundIds = results.Articles.Select(a => a.Id).ToHashSet();
        foundIds.Should().Contain(pending.Id,
            "a still-index_pending article's body must still be reachable through the fallback scan " +
            "restricted to the pending backlog, even though the ranked index has never seen it");
        foundIds.Should().Contain(indexed.Id,
            "the already-indexed article must still be found via the ranked index in the same call");
    }

    [Fact]
    public async Task SearchWebContentAsync_LockedSession_DegradesToMetadataOnly()
    {
        // Same locked-session invariant SearchWithContentAsync already guarantees: no body-derived
        // result at all while locked, regardless of index_pending state or IndexBuilder contents.
        var article = await ArticleService.CreateAsync("Plain", "/", [], "zzzgamma body content");
        IndexBuilder.AddOrUpdateDocument(article.Id, article.FolderId ?? Guid.Empty, "zzzgamma body content");
        await ArticleRepo.ClearIndexPendingUnscopedAsync(article.Id);
        Session.Lock();

        var results = await SearchService.SearchWebContentAsync("zzzgamma");

        results.Articles.Should().BeEmpty("a locked session must not surface any body-derived result");
    }

    [Fact]
    public async Task SearchWebContentAsync_BroadTerm_CapsResultsAtMaxContentResults()
    {
        // Create more matching-and-indexed articles than the reachable-depth cap, all sharing one
        // body term, then clear the pending backlog so the completeness check reports zero and the
        // fallback scan never runs — isolating the index path, whose top-K is what the cap bounds.
        const int overCap = SearchService.MaxContentResults + 25;
        for (int i = 0; i < overCap; i++)
        {
            var a = await ArticleService.CreateAsync($"Bulk {i}", "/", [], "zzzcap shared body term");
            IndexBuilder.AddOrUpdateDocument(a.Id, a.FolderId ?? Guid.Empty, "zzzcap shared body term");
        }

        // Bulk-clear index_pending for every row (far cheaper than one call per article) so
        // GetIndexPendingIdsUnscopedAsync reports an empty backlog and no linear fallback runs.
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_article SET index_pending = 0");
        }

        var results = await SearchService.SearchWebContentAsync("zzzcap");

        results.Articles.Should().HaveCount(SearchService.MaxContentResults,
            "the web content path hydrates at most MaxContentResults ranked matches even when far " +
            "more articles match — the bound that keeps a broad term from hydrating the whole vault " +
            "and overflowing the GetByIdsAsync IN list on a large corpus");
    }
}
