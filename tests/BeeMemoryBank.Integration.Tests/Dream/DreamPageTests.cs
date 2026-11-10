using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package U1, the Dream page, through the real Web host in front of a real Api: it is the superadmin's and nobody else's, it is the page of the other pages
/// (the sidebar and <c>#page-content</c>, which the layout's navigation needs), it carries the places its script draws into, its files are versioned like every
/// script, and it is made only of Shoelace components and the styles of its own file. No sentence of it, and no file of it, holds a Cyrillic letter (the repository
/// is public and its text is English).
/// </summary>
public sealed class DreamPageTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private const string BobPassword = "BobPass1234";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user", passwordHint = "A test hint" })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        _bob = await WebLogin.LoginAsync(_web, "bob", BobPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASuperadmin_IsGivenThePage_WithTheEveryPartTheScriptDrawsInto()
    {
        var response = await _admin.GetAsync("/Dream");
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Should().Contain("<title>Dream", "the page has its own title");
        // the parts of the plan 3.9: the state card with Start and Stop, the feed, the open advice, the history, and the two dialogs the buttons of a card open
        foreach (var id in new[]
        {
            "dream-state", "dream-start", "dream-stop", "dream-start-form", "dream-warnings", "dream-feed", "dream-feed-list", "dream-activity",
            "dream-open-list", "dream-open-pager", "dream-f-kind", "dream-f-score", "dream-f-sort", "dream-history", "dream-history-pager", "dream-h-mode",
            "dlg-dream-apply", "dream-apply-confirm", "dlg-dream-dismiss", "dream-dismiss-scope", "dream-dismiss-confirm",
        })
            page.Should().Contain($"id=\"{id}\"", $"the script draws into #{id}");
        // the short description, and the long one behind an expander
        page.Should().Contain("It never changes anything by itself");
        page.Should().Contain("<sl-details");
        page.Should().Contain("What leaves this node");
        page.Should().Contain("href=\"/Admin/Dream\"", "a link to the settings");
        page.Should().Contain("name=\"moon-stars\"");
    }

    [Fact]
    public async Task ThePageIsBuiltLikeTheOtherPages_SoTheSinglePageNavigationAndTheSidebarWork()
    {
        var page = await (await _admin.GetAsync("/Dream")).Content.ReadAsStringAsync();

        page.Should().Contain("id=\"app-sidebar\"", "the sidebar partial");
        page.Should().Contain("id=\"page-content\"", "the container the layout swaps");
        page.Should().MatchRegex(@"/js/pages/dream-model\.js\?v=[\w-]+");
        page.Should().MatchRegex(@"/js/pages/dream\.js\?v=[\w-]+");
        page.Should().MatchRegex(@"/css/dream\.css\?v=[\w-]+");
        page.IndexOf("dream-model.js", StringComparison.Ordinal).Should().BeLessThan(page.IndexOf("/js/pages/dream.js", StringComparison.Ordinal), "the model is loaded before the page that uses it");
    }

    [Fact]
    public async Task ARegularUser_IsNotLetIn()
    {
        var response = await _bob.GetAsync("/Dream");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("id=\"dream-start\"", "nothing of the page is drawn for them");
    }

    [Fact]
    public async Task ASignedOutVisitor_IsSentToSignIn()
    {
        using var anonymous = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await anonymous.GetAsync("/Dream");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Login");
    }

    [Theory]
    [InlineData("/js/pages/dream-model.js")]
    [InlineData("/js/pages/dream.js")]
    [InlineData("/css/dream.css")]
    public async Task TheFilesOfThePageAreServed(string path)
    {
        using var anonymous = _web.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ThePageUsesOnlyShoelaceComponents_AndOnlyItsOwnCssClasses()
    {
        // "no own components": every custom element in the markup and in what the script writes is one of Shoelace's
        var custom = new Regex(@"<([a-z][a-z0-9]*-[a-z0-9-]+)");
        foreach (var file in new[] { DreamPageScripts.Markup, DreamPageScripts.PageJs, DreamPageScripts.ModelJs })
        {
            var text = File.ReadAllText(file);
            custom.Matches(text).Select(m => m.Groups[1].Value).Distinct().Where(tag => !tag.StartsWith("sl-", StringComparison.Ordinal))
                .Should().BeEmpty($"{Path.GetFileName(file)} may use Shoelace's components only");
        }

        // and what the stylesheet styles is the page's own, or a Shoelace element inside it
        var css = File.ReadAllText(DreamPageScripts.Css);
        Regex.Matches(css, @"(?m)^(\.[\w-]+|#[\w-]+|sl-[\w-]+)").Select(m => m.Groups[1].Value).Distinct()
            .Where(s => !(s.StartsWith(".dream-") || s.StartsWith("#dream-") || s == "#dream-dismiss-scope" || s.StartsWith("@")))
            .Should().BeEmpty("dream.css styles .dream-* and its own ids, and does not restyle the pages of others");
    }

    [Fact]
    public void EveryChangeTheScriptSends_RunsBehindTheOneBusyHelper()
    {
        // the repository-wide guard (BusyHelperGuardTests) scans every script; this says it for this one by name
        BusyHelperGuardTests.ScriptProblems("dream.js", File.ReadAllText(DreamPageScripts.PageJs)).Should().BeEmpty();
        BusyHelperGuardTests.ScriptProblems("dream-model.js", File.ReadAllText(DreamPageScripts.ModelJs)).Should().BeEmpty();
        var markup = File.ReadAllText(DreamPageScripts.Markup);
        Regex.IsMatch(markup, @"<form\b", RegexOptions.IgnoreCase).Should().BeFalse("the page sends through its script and the busy helper, not through a form");
    }

    [Fact]
    public void ThePollIsARead_AndOnlyTheScriptsCallsGoToTheDreamRoutes()
    {
        var script = File.ReadAllText(DreamPageScripts.PageJs);

        script.Should().Contain("var API = '/api-proxy/dream';");
        Regex.Matches(script, @"['""]/api-proxy").Count.Should().Be(1, "one place knows the address of the API");
        script.Should().NotContain("EventSource", "the live feed is a poll, not SSE (plan 3.8)");
        script.Should().Contain("var POLL_MS = 2000;");
    }

    [Fact]
    public void NoFileOfThePackage_HoldsACyrillicLetter()
    {
        var files = new[]
        {
            DreamPageScripts.Markup, DreamPageScripts.PageModel, DreamPageScripts.PageJs, DreamPageScripts.ModelJs, DreamPageScripts.Css,
            DreamPageScripts.ModelHarness, DreamPageScripts.PageHarness,
        }.Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "Dream"), "DreamPage*.cs"));

        foreach (var file in files)
            Regex.IsMatch(File.ReadAllText(file), @"\p{IsCyrillic}").Should().BeFalse($"{Path.GetFileName(file)} is part of a public repository, in English");
    }
}
