using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// WP-12 integration coverage for <see cref="SearchService.SearchIndexedContentAsync"/>: the
/// wiring between <c>IndexBuilder.SearchRanked</c>, <c>ArticleRepository.GetByIdsAsync</c>, and
/// folder-scope ACL filtering (<c>ICallerScope.FilterArticles</c>). The BM25 ranking math itself is
/// covered exhaustively (and independently hand-checked) by
/// <c>BeeMemoryBank.Search.Tests.Indexing.IndexBuilderSearchRankedTests</c> -- these tests instead
/// prove <see cref="SearchService"/> hydrates and filters <c>IndexBuilder</c>'s results correctly.
///
/// <para>
/// This fixture does not run <c>PendingIndexProcessor</c> (WP-11's background indexer), so each
/// test feeds <see cref="TestFixture.IndexBuilder"/> directly with the same plaintext it just wrote
/// via <see cref="TestFixture.ArticleService"/> -- simulating what that background processor would
/// eventually do, without depending on it.
/// </para>
/// </summary>
public class SearchIndexedContentTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    [Fact]
    public async Task SearchIndexedContentAsync_ReturnsMatchingArticles_InDescendingScoreOrder()
    {
        var strongMatch = await ArticleService.CreateAsync("Strong", "/", [], "zzzneedle zzzneedle zzzneedle zzzfiller");
        var weakMatch = await ArticleService.CreateAsync("Weak", "/", [], "zzzneedle zzzfiller zzzfiller zzzfiller");
        var noMatch = await ArticleService.CreateAsync("NoMatch", "/", [], "zzzunrelated content only");

        IndexBuilder.AddOrUpdateDocument(strongMatch.Id, strongMatch.FolderId ?? Guid.Empty, "zzzneedle zzzneedle zzzneedle zzzfiller");
        IndexBuilder.AddOrUpdateDocument(weakMatch.Id, weakMatch.FolderId ?? Guid.Empty, "zzzneedle zzzfiller zzzfiller zzzfiller");
        IndexBuilder.AddOrUpdateDocument(noMatch.Id, noMatch.FolderId ?? Guid.Empty, "zzzunrelated content only");

        var results = await SearchService.SearchIndexedContentAsync("zzzneedle");

        results.Select(a => a.Id).Should().Equal(
            [strongMatch.Id, weakMatch.Id],
            "results must come back in descending BM25 score order (the higher-term-frequency article "
                + "first), and the article that does not contain the query term must be excluded entirely");
    }

    [Fact]
    public async Task SearchIndexedContentAsync_AppliesFolderScopeAclFiltering()
    {
        var workArticle = await ArticleService.CreateAsync("Work note", "/Work", [], "zzzsecretproject details");
        var personalArticle = await ArticleService.CreateAsync("Personal note", "/Personal", [], "zzzsecretproject plans");

        IndexBuilder.AddOrUpdateDocument(workArticle.Id, workArticle.FolderId ?? Guid.Empty, "zzzsecretproject details");
        IndexBuilder.AddOrUpdateDocument(personalArticle.Id, personalArticle.FolderId ?? Guid.Empty, "zzzsecretproject plans");

        // Both articles were created under the default (unrestricted) scope above. Now restrict
        // the caller -- shared by SearchService and every repository under this fixture -- to
        // /Work only, and verify the search result respects it exactly like every other read path.
        ScopeHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" });

        var results = await SearchService.SearchIndexedContentAsync("zzzsecretproject");

        results.Select(a => a.Id).Should().Equal(
            [workArticle.Id],
            "folder-scope ACL filtering must exclude the /Personal article even though it matches the query -- "
                + "IndexBuilder has no concept of folder ACLs at all, so this must come from FilterArticles");
    }

    [Fact]
    public async Task SearchIndexedContentAsync_NoDocumentMatches_ReturnsEmpty()
    {
        var article = await ArticleService.CreateAsync("Something", "/", [], "zzzhello world");
        IndexBuilder.AddOrUpdateDocument(article.Id, article.FolderId ?? Guid.Empty, "zzzhello world");

        var results = await SearchService.SearchIndexedContentAsync("zzznonexistentterm");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchIndexedContentAsync_EmptyQuery_ReturnsEmpty_NotException()
    {
        var article = await ArticleService.CreateAsync("Something", "/", [], "zzzhello world");
        IndexBuilder.AddOrUpdateDocument(article.Id, article.FolderId ?? Guid.Empty, "zzzhello world");

        var act = async () => await SearchService.SearchIndexedContentAsync("   ");

        var results = await act();
        results.Should().BeEmpty();
    }
}
