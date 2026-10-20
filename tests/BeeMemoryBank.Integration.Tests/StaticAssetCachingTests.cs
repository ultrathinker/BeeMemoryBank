using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// After an update a browser must not keep running the page script of the version before. The files of a packaged app all carry the same old "last modified"
/// date (1980 in the installer), so a browser told nothing about freshness keeps a script for years: on the show stand the editor of 2.6.4 ran the 2.6.3
/// <c>article-edit.js</c> from the cache and the "change" link of the strip did nothing but add "#" to the address. Two rules keep that from happening:
/// every script and style of a page is asked for as <c>?v=&lt;hash&gt;</c> (asp-append-version: the address changes when the file does), and the server tells
/// the browser how long it may keep what it is given (a year for ?v=, "ask again" for a script or style asked for without it).
/// </summary>
public sealed class StaticAssetCachingTests : IDisposable
{
    private readonly BmbWebHostFactory _web = new();

    public void Dispose() => ((IDisposable)_web).Dispose();

    [Theory]
    [InlineData("/js/pages/article-edit.js")]
    [InlineData("/js/pages/article-view.js")]
    [InlineData("/css/site.css")]
    public async Task AScriptOrStyleAskedForWithoutAVersion_IsAskedAboutAgainEachTime(string path)
    {
        using var client = _web.CreateClient();

        var response = await client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        response.Headers.CacheControl.Should().NotBeNull($"{path} says nothing about how long it may be kept, so a browser decides by its old date");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.ETag.Should().NotBeNull("the question is answered with a 304 when the file did not change");
    }

    [Theory]
    [InlineData("/js/pages/article-edit.js?v=abc123")]
    [InlineData("/css/site.css?v=abc123")]
    public async Task AScriptOrStyleAskedForWithAVersion_MayBeKeptForAYear(string path)
    {
        using var client = _web.CreateClient();

        var response = await client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromDays(365));
        response.Headers.CacheControl.Extensions.Select(e => e.Name).Should().Contain("immutable");
    }

    [Fact]
    public async Task AnUnchangedScriptIsAnswered304_WhenItIsAskedAboutAgain()
    {
        using var client = _web.CreateClient();
        var first = await client.GetAsync("/js/pages/article-edit.js");

        using var again = new HttpRequestMessage(HttpMethod.Get, "/js/pages/article-edit.js");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(again);

        second.StatusCode.Should().Be(System.Net.HttpStatusCode.NotModified);
    }

    // The source side: no page names a script or a stylesheet of the app without the version.
    private static readonly Regex LocalAsset = new(@"<(?:script|link)\b[^>]*?\s(?:src|href)=""(?:~)?/(?:js|lib|css)/[^""]+""[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void EveryScriptAndStylesheetOfAPage_IsAskedForWithItsVersion()
    {
        var pages = Path.Combine(FindRepoRoot(), "server", "BeeMemoryBank.Web", "Pages");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in LocalAsset.Matches(text))
            {
                if (m.Value.Contains("rel=\"icon\"", StringComparison.Ordinal)) continue;
                if (!m.Value.Contains("asp-append-version", StringComparison.Ordinal))
                    missing.Add($"{Path.GetRelativePath(pages, file)}: {m.Value.Trim()}");
            }
        }

        missing.Should().BeEmpty("a script or stylesheet without asp-append-version can be a copy of the version before an update, kept by the browser");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) || File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
