namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Covers the query-time stop-word filtering wired into <c>SearchService.SearchIndexedContentAsync</c>'s
/// tokenize/stem pipeline: a query reduced entirely to stop words yields no results, and stripping a
/// stop word from a query must not change how the remaining content term ranks (so "the system"
/// ranks exactly like "system"). Both locales' stop words are stripped, in one mixed query.
/// </summary>
public class SearchStopWordTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    private async Task SeedSystemCorpusAsync()
    {
        // A few articles containing the content term "system" at different term frequencies/lengths,
        // so the ranked result is a non-trivial ordered list (not a single article) -- making an
        // "identical ranking" assertion meaningful rather than vacuous.
        (string title, string body)[] docs =
        [
            ("Strong", "system system system control"),
            ("Medium", "system system control panel"),
            ("Weak", "system control panel dashboard"),
            ("Unrelated", "control panel dashboard only"),
        ];

        foreach ((string title, string body) in docs)
        {
            var article = await ArticleService.CreateAsync(title, "/", [], body);
            IndexBuilder.AddOrUpdateDocument(article.Id, article.FolderId ?? Guid.Empty, body);
        }
    }

    [Fact]
    public async Task SearchIndexedContentAsync_AllStopWordQuery_ReturnsEmpty()
    {
        await SeedSystemCorpusAsync();

        // Every token here is a stop word (English + Russian mixed); after filtering the query has
        // zero terms, so it must return nothing rather than "match everything".
        var results = await SearchService.SearchIndexedContentAsync("the a of and и в не");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchIndexedContentAsync_LeadingStopWord_RanksIdenticallyToTheContentTermAlone()
    {
        await SeedSystemCorpusAsync();

        var withStopWord = await SearchService.SearchIndexedContentAsync("the system");
        var contentOnly = await SearchService.SearchIndexedContentAsync("system");

        contentOnly.Should().NotBeEmpty("the content term must match the seeded corpus");
        withStopWord.Select(a => a.Id).Should().Equal(
            contentOnly.Select(a => a.Id),
            "stripping the stop word 'the' must leave the query, and therefore the ranking, identical to 'system'");
    }

    [Fact]
    public async Task SearchIndexedContentAsync_StopWordsOfBothLocales_AreStripped()
    {
        await SeedSystemCorpusAsync();

        // English "the"/"of" and Russian "и"/"для" must all be dropped, leaving just "system".
        var mixed = await SearchService.SearchIndexedContentAsync("the и system для of");
        var contentOnly = await SearchService.SearchIndexedContentAsync("system");

        contentOnly.Should().NotBeEmpty();
        mixed.Select(a => a.Id).Should().Equal(
            contentOnly.Select(a => a.Id),
            "stop words from both locales must be stripped, yielding the same single-term ranking");
    }
}
