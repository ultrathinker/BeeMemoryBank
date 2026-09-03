namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Concurrent writes to one article must not silently discard each other.
///
/// <para>
/// The MCP append/prepend/replace tools and the chat dispatcher's equivalents were plain
/// read-modify-write: fetch the body, mutate it in memory, save the whole thing back, with nothing
/// serializing the pair. Two agents appending at once — or an agent appending while someone edits
/// in the browser — both read the same body and the second save overwrote the first's change,
/// recoverable only from version history if anyone noticed. The version-number allocation had a
/// sharper failure: both writers computed the same next number, and the unique index on
/// (article_id, version_number) made the loser throw AFTER its metadata UPDATE had committed,
/// leaving a bumped timestamp on an unchanged body with no event logged.
/// </para>
///
/// <para>
/// Scope note, so nobody reads more into these than they prove: they do NOT reproduce the race.
/// The in-memory SQLite harness serializes writes on its own and every await here completes
/// synchronously, so the read-modify-write window never opens and these pass with the lock removed.
/// What they pin is the BEHAVIOR of the append/prepend/replace operations now that they live on
/// the service — including the no-op replace contract. The mutual exclusion they rely on is tested
/// directly, under real concurrency, in <see cref="ArticleWriteLockTests"/>.
/// </para>
/// </summary>
public class ConcurrentArticleWriteTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    [Fact]
    public async Task ParallelAppends_AllSurvive()
    {
        var article = await ArticleService.CreateAsync("Log", "/Concurrency", [], "start");

        const int writers = 12;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            ArticleService.AppendAsync(article.Id, $"entry-{i}")));

        var content = await ArticleService.GetContentAsync(article.Id);
        for (int i = 0; i < writers; i++)
            content.Should().Contain($"entry-{i}", $"append {i} must not be lost to a concurrent one");
    }

    [Fact]
    public async Task ParallelAppendAndPrepend_BothSurvive()
    {
        var article = await ArticleService.CreateAsync("Both Ends", "/Concurrency", [], "middle");

        await Task.WhenAll(
            ArticleService.AppendAsync(article.Id, "tail"),
            ArticleService.PrependAsync(article.Id, "head"));

        var content = await ArticleService.GetContentAsync(article.Id);
        content.Should().Contain("head").And.Contain("middle").And.Contain("tail");
    }

    /// <summary>
    /// Version numbers are allocated as max+1 under the same lock, so a burst of writes produces a
    /// contiguous history rather than a collision on the unique index.
    /// </summary>
    [Fact]
    public async Task ParallelWrites_ProduceOneVersionPerWriteWithNoDuplicateNumbers()
    {
        var article = await ArticleService.CreateAsync("Versioned", "/Concurrency", [], "v0");

        const int writers = 10;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            ArticleService.AppendAsync(article.Id, $"line-{i}")));

        var versionRepo = new Storage.Sqlite.ArticleVersionRepository(Factory, ScopeHolder);
        var versions = await versionRepo.GetByArticleIdAsync(article.Id);

        versions.Should().HaveCount(writers, "each write snapshots the body it replaced");
        versions.Select(v => v.VersionNumber).Should().OnlyHaveUniqueItems();
        versions.Select(v => v.VersionNumber).OrderBy(n => n)
            .Should().Equal(Enumerable.Range(1, writers));
    }

    [Fact]
    public async Task ParallelReplaces_EachAppliesToTheResultOfTheLast()
    {
        var article = await ArticleService.CreateAsync(
            "Counter", "/Concurrency", [], string.Concat(Enumerable.Repeat("x", 8)));

        // Every replace turns exactly one 'x' into 'y'. Serialized, that is 8 replacements and no
        // 'x' left; with a lost update, some run against a stale body and 'x' survives.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ArticleService.ReplaceInAsync(article.Id, "x", "y")));

        var content = await ArticleService.GetContentAsync(article.Id);
        content.Should().Be(string.Concat(Enumerable.Repeat("y", 8)));
    }

    [Fact]
    public async Task ReplaceWithNoMatch_LeavesTheArticleCompletelyUntouched()
    {
        var article = await ArticleService.CreateAsync("Untouched", "/Concurrency", [], "body");
        var before = await ArticleService.GetMetadataAsync(article.Id);

        var count = await ArticleService.ReplaceInAsync(article.Id, "absent", "x");

        count.Should().Be(0);
        var after = await ArticleService.GetMetadataAsync(article.Id);
        after!.UpdatedAt.Should().Be(before!.UpdatedAt, "a no-op replace must not bump updatedAt");

        var versionRepo = new Storage.Sqlite.ArticleVersionRepository(Factory, ScopeHolder);
        (await versionRepo.GetByArticleIdAsync(article.Id))
            .Should().BeEmpty("a no-op replace must not create a version");
    }
}
