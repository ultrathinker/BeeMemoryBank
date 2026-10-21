extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AdminPageCatalog = WebApp::BeeMemoryBank.Web.Pages.Admin.AdminPageCatalog;
using AdminPageInfo = WebApp::BeeMemoryBank.Web.Pages.Admin.AdminPageInfo;
using AdminPageModel = WebApp::BeeMemoryBank.Web.Pages.Admin.AdminPageModel;
using PublicRateLimitMiddleware = WebApp::BeeMemoryBank.Web.Middleware.PublicRateLimitMiddleware;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Admin area is a list (<c>/Admin</c>) and a page per block (<c>/Admin/&lt;Name&gt;</c>) on ONE template. These are the guards that keep it that way, read from the
/// sources: the catalog and the files agree, no page can leave the template or go without a description, the addresses of the old single page still lead somewhere, and
/// nothing in the Web project links to an anchor that is gone.
/// </summary>
public sealed class AdminPagesSourceTests
{
    private static readonly string Web = Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web");
    private static readonly string AdminDir = Path.Combine(Web, "Pages", "Admin");

    /// <summary>The ids of the cards of the old single Admin page (<c>AdminCards.All</c> of 2.6.4): bookmarks of the form <c>/Admin#card-snapshots</c> exist.</summary>
    private static readonly string[] OldCardIds =
    [
        "card-updates", "card-master-password", "devices-on-my-network", "card-invisible-mode", "card-trusted-nodes", "restored-peers", "card-snapshots",
        "card-compaction", "card-agents", "card-metadata", "card-database-encryption", "card-os-auto-unlock", "card-dek-rotation", "card-reset-node", "card-diagnostics",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> PageFiles() =>
        Directory.EnumerateFiles(AdminDir, "*.cshtml")
            .Where(f => !Path.GetFileName(f).StartsWith('_') && !string.Equals(Path.GetFileName(f), "Index.cshtml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    // ---------------------------------------------------------------- the catalog

    [Fact]
    public void TheCatalog_GivesEveryPageARouteATitleAnIconAndAShortDescription_OnceEach()
    {
        var all = AdminPageCatalog.All;
        all.Count.Should().BeGreaterThanOrEqualTo(25, "one block, one page");

        all.Select(p => p.Route).Should().OnlyHaveUniqueItems();
        all.Select(p => p.Title).Should().OnlyHaveUniqueItems("two items with one title cannot be told apart on the list");
        var sections = AdminPageCatalog.Sections.Select(s => s.Title).ToList();
        sections.Should().OnlyHaveUniqueItems();
        foreach (var page in all)
        {
            page.Route.Should().MatchRegex("^/Admin/[A-Za-z]+$");
            page.Title.Trim().Should().NotBeEmpty();
            page.Icon.Trim().Should().NotBeEmpty();
            sections.Should().Contain(page.Section, $"{page.Route} is under a heading of the index");
            // "a few words up to a couple of sentences"
            page.Summary.Trim().Length.Should().BeInRange(20, 260, $"the short description of {page.Route}");
            Regex.Matches(page.Summary, @"[.!?](\s|$)").Count.Should().BeLessThanOrEqualTo(2, $"the short description of {page.Route} is at most two sentences");
        }
        foreach (var (title, _) in AdminPageCatalog.Sections)
            AdminPageCatalog.In(title).Should().NotBeEmpty($"the heading '{title}' has pages under it");
    }

    [Fact]
    public void EveryPageFileIsInTheCatalog_AndEveryCatalogRowHasItsFile()
    {
        var files = PageFiles().Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        var rows = AdminPageCatalog.All.Select(p => p.Name).ToList();

        files.Should().BeEquivalentTo(rows, "a page that is not in the catalog is not on the index, a row without a page is a dead link");
        foreach (var name in files)
            File.Exists(Path.Combine(AdminDir, name + ".cshtml.cs")).Should().BeTrue($"{name} has its page model");
    }

    // ---------------------------------------------------------------- the template

    /// <summary>What is wrong with the source of one page of the Admin area (the guard, apart from the files, so that it can be tested).</summary>
    internal static IEnumerable<string> TemplateProblems(string name, string text)
    {
        if (!Regex.IsMatch(text, @"\A\s*@page\s*\r?\n")) yield return $"{name}: not a Razor page (the first line is @page)";
        if (!text.Contains($"@model BeeMemoryBank.Web.Pages.Admin.{name}Model", StringComparison.Ordinal)) yield return $"{name}: the model is not {name}Model";
        // A layout is chosen by Pages/Admin/_ViewStart.cshtml, for the whole folder: a page that sets its own has left the template.
        if (Regex.IsMatch(text, @"\bLayout\s*=")) yield return $"{name}: sets its own Layout";
        var about = Section(text, "About");
        if (about is null) { yield return $"{name}: has no @section About"; yield break; }
        var words = Regex.Replace(Regex.Replace(about, @"<[^>]+>", " "), @"@\*.*?\*@|&\w+;|\s+", " ", RegexOptions.Singleline).Trim();
        if (words.Length < 150) yield return $"{name}: the description (section About) has {words.Length} characters of text; say what this is and what it is for";
    }

    /// <summary>The text inside <c>@section name { ... }</c>, braces balanced, or null.</summary>
    private static string? Section(string text, string name)
    {
        var at = text.IndexOf($"@section {name}", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = text.IndexOf('{', at);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[(open + 1)..i];
        }
        return null;
    }

    [Fact]
    public void EveryPage_IsDrawnByTheTemplate_AndSaysWhatItIsFor()
    {
        var problems = new List<string>();
        var seen = 0;
        foreach (var file in PageFiles())
        {
            seen++;
            problems.AddRange(TemplateProblems(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
        }
        seen.Should().BeGreaterThanOrEqualTo(25);
        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("a short description", "@page\n@model BeeMemoryBank.Web.Pages.Admin.XModel\n@section About { <p>Short.</p> }\n", 1)]
    [InlineData("a page that sets its own layout", "@page\n@model BeeMemoryBank.Web.Pages.Admin.XModel\n@{ Layout = \"_Layout\"; }\n@section About { <p>__LONG__</p> }\n", 1)]
    [InlineData("a description outside the section", "@page\n@model BeeMemoryBank.Web.Pages.Admin.XModel\n<p>__LONG__</p>\n", 1)]
    [InlineData("another model", "@page\n@model BeeMemoryBank.Web.Pages.Admin.OtherModel\n@section About { <p>__LONG__</p> }\n", 1)]
    [InlineData("a page with its content", "@page\n@model BeeMemoryBank.Web.Pages.Admin.XModel\n@section About { <p>__LONG__</p> }\n<p>content</p>\n", 0)]
    [InlineData("a description with a condition", "@page\n@model BeeMemoryBank.Web.Pages.Admin.XModel\n@section About { @if (true) { <p>__LONG__</p> } }\n", 0)]
    public void TheTemplateGuard_FlagsAPageThatLeavesTheTemplateOrSaysNothing(string label, string source, int expected)
    {
        var long150 = string.Join(' ', Enumerable.Repeat("This page does something useful and says what.", 5));
        TemplateProblems("X", source.Replace("__LONG__", long150)).Should().HaveCount(expected);
    }

    [Fact]
    public void TheTemplate_DrawsTheTitleTheIconTheBackButtonTheMessageTheDescriptionAndTheContent_AndTheFolderChoosesIt()
    {
        var layout = File.ReadAllText(Path.Combine(AdminDir, "_AdminPage.cshtml"));
        layout.Should().Contain("info.Title").And.Contain("info.Icon").And.Contain("href=\"/Admin\"").And.Contain("Back");
        layout.Should().Contain("<partial name=\"_AdminMessage\"").And.Contain("RenderSectionAsync(\"About\", required: true)").And.Contain("RenderBody()");
        layout.Should().Contain("data-admin-page=\"@info.Route\"").And.Contain("asp-append-version=\"true\"");
        Regex.Replace(File.ReadAllText(Path.Combine(AdminDir, "_ViewStart.cshtml")), @"\s+", "").Should().Contain("Layout=\"_AdminPage\";");
        // The index is the one page that draws itself, and it keeps the list and the message area.
        File.ReadAllText(Path.Combine(AdminDir, "Index.cshtml")).Should().Contain("AdminPageCatalog").And.Contain("_AdminMessage");
    }

    [Fact]
    public void EveryPageModel_DerivesFromTheOneBase_AndIsAuthorizedLikeIt()
    {
        foreach (var file in PageFiles().Append(Path.Combine(AdminDir, "Index.cshtml")))
        {
            var model = File.ReadAllText(file + ".cs");
            model.Should().MatchRegex(@"class \w+Model\([^)]*\) : AdminPageModel\(api\)", $"{Path.GetFileName(file)} has the model of the Admin area");
            model.Should().NotMatchRegex(@"(?m)^\s*\[Authorize", "the superadmin gate is the base class's, in one place");
        }
        var baseSource = File.ReadAllText(Path.Combine(AdminDir, "AdminPageModel.cs"));
        baseSource.Should().Contain("[Authorize(Roles = \"superadmin\")]").And.Contain("abstract class AdminPageModel");
        typeof(AdminPageModel).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Should().ContainSingle(a => a.Roles == "superadmin");
    }

    // ---------------------------------------------------------------- the addresses of the old page

    [Fact]
    public void TheMessagesOfOsAutoUnlock_NameTheAccountOfTheSystemTheNodeRunsOn_NotOfWindowsAlone()
    {
        var model = File.ReadAllText(Path.Combine(AdminDir, "OsAutoUnlock.cshtml.cs"));

        model.Should().NotContain("Windows account", "the same message is read on a Mac").And.Contain("{OsName} account");
    }

    [Fact]
    public void TheLegacyTable_CoversEveryCardOfTheOldPage_AndEachRowLeadsToAPage()
    {
        AdminPageCatalog.LegacyAnchors.Keys.Should().BeEquivalentTo(OldCardIds, "a bookmark of any card of the old page must still lead somewhere");
        foreach (var (id, route) in AdminPageCatalog.LegacyAnchors)
            AdminPageCatalog.Find(route).Should().NotBeNull($"{id} -> {route} is a page of the area");
    }

    [Fact]
    public void TheIndexScript_MovesAKnownFragmentToItsPage_AndOnlyToAPageOfTheArea()
    {
        var js = File.ReadAllText(Path.Combine(Web, "wwwroot", "js", "pages", "admin-index.js"));

        js.Should().Contain("admin-legacy-anchors").And.Contain("location.hash").And.Contain("location.replace");
        js.Should().Contain("hasOwnProperty", "only a key of the table, not something inherited from Object.prototype (#constructor)");
        js.Should().Contain("/^\\/Admin\\/[A-Za-z]+$/", "and nothing but a page of the Admin area is followed");
        js.Should().NotMatchRegex(@"<script|eval\(|innerHTML", "the table is data, never markup");
        // The index loads it, with the version; and renders the table of the server.
        var index = File.ReadAllText(Path.Combine(AdminDir, "Index.cshtml"));
        index.Should().Contain("/js/pages/admin-index.js\" asp-append-version=\"true\"").And.Contain("id=\"admin-legacy-anchors\"").And.Contain("AdminPageCatalog.LegacyAnchors");
    }

    // ---------------------------------------------------------------- nothing points at the old page

    private static IEnumerable<string> WebSources()
    {
        foreach (var pattern in new[] { "*.cs", "*.cshtml", "*.js", "*.css", "*.html" })
            foreach (var file in Directory.EnumerateFiles(Web, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Web, file).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith("wwwroot/lib/")) continue;
                yield return file;
            }
    }

    [Fact]
    public void NoSourceOfTheWebProject_LinksToAnAnchorOfTheOldPage_OrPostsToIt_OrNamesItsTypes()
    {
        var allowed = new HashSet<string> { "Pages/Admin/AdminPageCatalog.cs", "wwwroot/js/pages/admin-index.js" };   // they ARE the table of the old fragments
        var problems = new List<string>();
        var cards = string.Join('|', OldCardIds.Select(Regex.Escape));
        foreach (var file in WebSources())
        {
            var rel = Path.GetRelativePath(Web, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var checks = new (string Name, string Pattern, bool Exempt)[]
            {
                ("a link to /Admin#anchor", @"/Admin#", allowed.Contains(rel)),
                ("a post to the old page (/Admin?handler=)", @"/Admin\?handler=", false),
                ("an id of a card of the old page as the fragment of an Admin address", $"/Admin[A-Za-z/]*#({cards})\\b", allowed.Contains(rel)),
                ("a type of the old page", @"\b(AdminCards|AdminCardMessage|AdminModel)\b", false),
                ("the partial of a block of the old page", @"Shared/Admin/_Admin(Nodes|Snapshots|Agents|Ai|Security|Metadata|Diagnostics)|_CardMessage", false),
            };
            foreach (var (name, pattern, exempt) in checks)
                if (!exempt && Regex.IsMatch(text, pattern))
                    problems.Add($"{rel}: {name}");
        }
        problems.Should().BeEmpty();
    }

    [Fact]
    public void EveryAdminAddressNamedInTheWebSources_IsAPageOfTheArea()
    {
        var known = AdminPageCatalog.All.Select(p => p.Name).Append("Index").ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var found = 0;
        foreach (var file in WebSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"/Admin/([A-Za-z]+)"))
            {
                found++;
                if (!known.Contains(m.Groups[1].Value))
                    problems.Add($"{Path.GetRelativePath(Web, file)}: {m.Value}");
            }
        }
        found.Should().BeGreaterThan(40, "the scan should find the links and redirects to the pages");
        problems.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the node wipe keeps its budget

    private static async Task<int> PostAsync(PublicRateLimitMiddleware middleware, string path, string query, string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task ThePageOfTheNodeWipe_IsThrottledByTheRealMiddleware_AndNoOtherPageOfTheAreaIs()
    {
        var middleware = new PublicRateLimitMiddleware(c => { c.Response.StatusCode = 302; return Task.CompletedTask; }, NullLogger<PublicRateLimitMiddleware>.Instance);
        var wiper = "10.77." + Random.Shared.Next(1, 250) + "." + Random.Shared.Next(1, 250);

        var codes = new List<int>();
        for (var i = 0; i < 7; i++) codes.Add(await PostAsync(middleware, "/Admin/ResetNode", "?handler=ResetNode", wiper));
        codes.Take(5).Should().OnlyContain(c => c == 302, "a legitimate admin needs one or two tries");
        codes.Skip(5).Should().OnlyContain(c => c == 429, "the guess at the master password in front of a wipe has a budget");

        // The same address, a spelling of the path the router resolves to the page, and a handler that is not the wipe: the same budget.
        (await PostAsync(middleware, "//ADMIN/resetnode/", "?handler=Whatever", wiper)).Should().Be(429);

        // Routine configuration of the other pages is not throttled.
        var ordinary = "10.78." + Random.Shared.Next(1, 250) + "." + Random.Shared.Next(1, 250);
        for (var i = 0; i < 30; i++)
            (await PostAsync(middleware, "/Admin/Snapshots", "?handler=DeleteSnapshot", ordinary)).Should().Be(302);
    }
}

/// <summary>
/// The Admin area through the real Web host in front of a real Api: the index lists every page once under its heading, every page answers and is drawn by the
/// template for a superadmin and is refused to everybody else, the alerts of the old top of the page are on the index and on their pages, a page loads only its own
/// data, and every handler of every page works from its page.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class AdminPagesTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPages123";
    private const string BobPassword = "BobPages1234";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private Scripted _scripted = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user" })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_scripted = new Scripted(_api.Server.CreateHandler()));
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

    /// <summary>The real Api, except that the three things the top of the old page warned about can be made to be true, and every call the pages make is written down.</summary>
    private sealed class Scripted(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string? PasswordNotice { get; set; }
        public string? PeerPending { get; set; }
        public string? Restored { get; set; }
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add(request.Method.Method + " " + path);
            if (request.Method == HttpMethod.Get)
            {
                var canned = path switch
                {
                    "/api/keys/password-notice" => PasswordNotice,
                    "/api/dek-rotation/peer-pending" => PeerPending,
                    "/api/recovery/restored-peers" => Restored,
                    _ => null,
                };
                if (canned is not null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(canned, Encoding.UTF8, "application/json") });
            }
            return base.SendAsync(request, ct);
        }
    }

    private async Task<string> GetRawAsync(string url)
    {
        using var response = await _admin.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> GetAsync(string url) => WebUtility.HtmlDecode(await GetRawAsync(url));

    private static string Body(string html) => html[html.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal)..];

    private static string Message(string html)
    {
        var area = Regex.Match(html, "<div id=\"admin-page-message\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
        area.Success.Should().BeTrue("the page has the message area of the template");
        return area.Groups[1].Value;
    }

    // ---------------------------------------------------------------- the index

    [Fact]
    public async Task TheIndex_ListsEveryPageOnceUnderItsHeading_WithItsIconTitleAndShortDescription()
    {
        var html = await GetAsync("/Admin");

        foreach (var page in AdminPageCatalog.All)
        {
            Regex.Matches(html, $"href=\"{Regex.Escape(page.Route)}\"").Should().HaveCount(1, $"{page.Route} is on the index exactly once");
            Regex.Matches(html, $"data-admin-item=\"{page.Name}\"").Should().HaveCount(1);
            var item = Regex.Match(html, $"<a class=\"admin-item\" href=\"{Regex.Escape(page.Route)}\".*?</a>", RegexOptions.Singleline).Value;
            item.Should().Contain($"name=\"{page.Icon}\"").And.Contain(">" + page.Title + "<").And.Contain(page.Summary).And.Contain("name=\"chevron-right\"");
        }
        // The headings are the catalog's, in its order, and each page is under its own.
        var at = -1;
        foreach (var (title, hint) in AdminPageCatalog.Sections)
        {
            var heading = html.IndexOf($">{title}</h2>", StringComparison.Ordinal);
            heading.Should().BeGreaterThan(at, $"'{title}' follows the heading before it");
            html.Should().Contain(hint);
            var end = html.IndexOf("</section>", heading, StringComparison.Ordinal);
            foreach (var page in AdminPageCatalog.In(title))
            {
                var link = html.IndexOf($"href=\"{page.Route}\"", heading, StringComparison.Ordinal);
                (link > heading && link < end).Should().BeTrue($"{page.Title} is under '{title}'");
            }
            at = heading;
        }
        html.Should().Contain("<title>Admin - ");
        html.Should().NotContain("id=\"admin-page-about\"", "the index is the list, not a page of the template");
    }

    [Fact]
    public async Task EveryLinkOfTheIndex_AnswersOkForASuperadmin_AndIsDrawnByTheTemplate()
    {
        var index = await GetAsync("/Admin");
        var links = Regex.Matches(index, "<a class=\"admin-item\" href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        links.Should().BeEquivalentTo(AdminPageCatalog.All.Select(p => p.Route));

        foreach (var page in AdminPageCatalog.All)
        {
            var html = await GetAsync(page.Route);

            html.Should().Contain($"data-admin-page=\"{page.Route}\"", page.Route);
            html.Should().Contain($"<h1 class=\"admin-title\">{page.Title}</h1>", page.Route);
            html.Should().Contain($"<title>{page.Title} - ");
            html.Should().Contain($"<sl-icon class=\"admin-page-icon\" name=\"{page.Icon}\"></sl-icon>");
            html.Should().MatchRegex("<sl-button id=\"admin-back\"[^>]*href=\"/Admin\"", "every page has the Back button to the list");
            var about = html[html.IndexOf("id=\"admin-page-about\"", StringComparison.Ordinal)..html.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal)];
            var text = Regex.Replace(Regex.Replace(about, "<[^>]+>", " "), @"\s+", " ").Trim();
            text.Length.Should().BeGreaterThan(page.Summary.Length, $"{page.Route} says more than its line on the list");
            html.Should().Contain("/js/pages/admin.js?v=");
            // the order the owner asked for: title and icon, then what it is, then the content
            var order = new[] { "admin-title", "id=\"admin-page-message\"", "id=\"admin-page-about\"", "id=\"admin-page-body\"" }.Select(m => html.IndexOf(m, StringComparison.Ordinal)).ToList();
            order.Should().BeInAscendingOrder().And.NotContain(-1);
        }
    }

    [Fact]
    public async Task EveryPage_IsRefusedForARegularUser_AndForAVisitorWhoIsNotSignedIn()
    {
        using var visitor = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var route in AdminPageCatalog.All.Select(p => p.Route).Prepend("/Admin"))
        {
            using var asBob = await _bob.GetAsync(route);
            asBob.StatusCode.Should().NotBe(HttpStatusCode.OK, $"{route} is the superadmin's");
            (await asBob.Content.ReadAsStringAsync()).Should().NotContain("admin-page-about");

            using var anonymous = await visitor.GetAsync(route);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Redirect, route);
            anonymous.Headers.Location!.OriginalString.Should().Contain("/Login");
        }
    }

    [Fact]
    public async Task APostToAPage_IsRefusedForARegularUser_AndChangesNothing()
    {
        // Bob has no business with the wipe, the lock, the rotation or the revocation: the area's gate is the base class, for the handlers too.
        foreach (var (route, handler) in new[] { ("/Admin/ResetNode", "ResetNode"), ("/Admin/DekRotation", "ProposeDekRotation"), ("/Admin/TrustedNodes", "RevokeNode"), ("/Admin/Snapshots", "CreateSnapshot") })
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, $"{route}?handler={handler}") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["masterPassword"] = AdminPassword }) };
            using var response = await _bob.SendAsync(post);
            response.StatusCode.Should().NotBe(HttpStatusCode.OK);
            (response.Headers.Location?.OriginalString ?? "").Should().NotStartWith(route, $"{handler} did not run for bob");
        }
        (await _api.Services.GetRequiredService<BeeMemoryBank.Core.Interfaces.INodeIdentityRepository>().GetAsync()).Should().NotBeNull("the node is still there");
    }

    // ---------------------------------------------------------------- the alerts of the top of the old page

    [Fact]
    public async Task AStaleMasterPassword_IsToldOnTheIndex_AndOnTheMasterPasswordPage_AndNowhereElse()
    {
        _scripted.PasswordNotice = """{"isStale":true,"changedAt":"2026-10-01T10:00:00Z","changedByNode":"Office PC"}""";

        var index = await GetAsync("/Admin");
        var page = await GetAsync("/Admin/MasterPassword");

        foreach (var html in new[] { index, page })
        {
            html.Should().Contain("id=\"master-password-notice\"").And.Contain("This node still uses the old master password.").And.Contain("Office PC");
            Regex.Match(html, "<form[^>]*handler=DismissPasswordNotice[^>]*>").Value.Should().Contain("data-busy");
        }
        index.Should().Contain("href=\"/Admin/MasterPassword\"", "the index sends to the page where it is changed");
        page.Should().Contain("Change it below.").And.NotContain("Change it on the");
        (await GetAsync("/Admin/Snapshots")).Should().NotContain("master-password-notice");
        _scripted.PasswordNotice = null;
        (await GetAsync("/Admin")).Should().NotContain("master-password-notice");
    }

    [Fact]
    public async Task APendingRotationOfAPeer_IsOfferedOnTheIndex_AndOnTheDekRotationPage_AndNowhereElse()
    {
        var id = Guid.NewGuid().ToString();
        _scripted.PeerPending = $$"""[{"eventId":"{{id}}","originatorNodeId":"{{Guid.NewGuid()}}","originatorDisplayName":"Office PC","rotationTs":"2026-10-01T10:00:00Z"}]""";

        var index = await GetAsync("/Admin");
        var page = await GetAsync("/Admin/DekRotation");

        foreach (var html in new[] { index, page })
        {
            html.Should().Contain("Office PC").And.Contain("rotated their DEK on").And.Contain("Reject DEK rotation?");
            Regex.Match(html, "<form[^>]*handler=PeerAcceptDekRotation[^>]*>").Value.Should().Contain("data-busy=\"long\"");
            html.Should().Contain($"value=\"{id}\"").And.Contain("handler=PeerRejectDekRotation");
            html.Should().Contain("id=\"reject-dialog-" + id.Replace("-", "") + "\"");
        }
        (await GetAsync("/Admin/Agents")).Should().NotContain("rotated their DEK on");
    }

    [Fact]
    public async Task ARestoreThatLeftDevicesWaiting_IsToldOnTheIndex_AndTheyAreListedOnTheirPage()
    {
        var phone = Guid.NewGuid();
        _scripted.Restored = $$"""[{"nodeId":"{{phone}}","displayName":"Kitchen tablet","apiAddress":"https://tablet.lan"}]""";

        var index = await GetAsync("/Admin");
        var page = await GetAsync("/Admin/RestoredDevices");

        index.Should().Contain("id=\"restored-devices-notice\"").And.Contain("1 restored device waits for your confirmation.").And.Contain("href=\"/Admin/RestoredDevices\"");
        Body(page).Should().Contain("Kitchen tablet").And.Contain("https://tablet.lan").And.Contain($"value=\"{phone}\"").And.Contain("handler=ConfirmRestoredPeer");
        _scripted.Restored = "[]";
        (await GetAsync("/Admin")).Should().NotContain("restored-devices-notice");
    }

    [Fact]
    public async Task TheIndexLoadsOnlyWhatItWarnsOf_AndAPageLoadsOnlyItsOwnData()
    {
        _scripted.Calls.Clear();
        await GetAsync("/Admin");
        var index = Calls();
        index.Should().NotContain(c => c.Contains("/api/snapshots") || c.Contains("/api/agents") || c.Contains("/api/whitelist") || c.Contains("/api/vault/status")
            || c.Contains("/api/admin/search") || c.Contains("/api/sync/compaction") || c.Contains("/api/session/lock-impact"), "the index is a list, it does not load the data of 25 blocks");
        index.Should().Contain("GET /api/keys/password-notice").And.Contain("GET /api/dek-rotation/peer-pending").And.Contain("GET /api/recovery/restored-peers");

        _scripted.Calls.Clear();
        await GetAsync("/Admin/NodeIdentity");
        var identity = Calls();
        identity.Should().Contain(c => c.Contains("identity", StringComparison.OrdinalIgnoreCase));
        identity.Should().NotContain(c => c.Contains("/api/snapshots") || c.Contains("/api/agents") || c.Contains("/api/whitelist") || c.Contains("/api/vault/status")
            || c.Contains("/api/keys/password-notice") || c.Contains("/api/dek-rotation") || c.Contains("/api/recovery/restored-peers"));

        // A page whose data is read by its own script asks the server for nothing: it makes the calls of the layout (the header), and no more.
        _scripted.Calls.Clear();
        await GetAsync("/Admin/ConnectDevice");
        var layoutOnly = Calls();
        _scripted.Calls.Clear();
        await GetAsync("/Admin/Branding");
        Calls().Should().BeEquivalentTo(layoutOnly, "a page without data makes no call of its own");
    }

    private List<string> Calls() { lock (_scripted.Calls) return [.. _scripted.Calls.Distinct()]; }

    // ---------------------------------------------------------------- every handler works from its new page

    private enum Expect { Message, NoMessage, BadRequest, DownloadRefused }

    private sealed record Case(string Page, string Handler, Expect Expect, Dictionary<string, string>? Fields = null, string Method = "POST");

    private static Dictionary<string, string> F(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static readonly string Nobody = Guid.NewGuid().ToString();

    /// <summary>
    /// Every handler of every page (the 35 of the old page, and the three the index and the pages share), posted with what a browser would send and an answer
    /// the node refuses, so that nothing is changed and everything is said: where the browser is sent back to, and what it is told.
    /// </summary>
    private static readonly Case[] Cases =
    [
        new("/Admin", "DismissPasswordNotice", Expect.Message),
        new("/Admin", "PeerAcceptDekRotation", Expect.Message, F(("eventId", Nobody))),
        new("/Admin", "PeerRejectDekRotation", Expect.Message, F(("eventId", Nobody))),

        new("/Admin/RestoredDevices", "ConfirmRestoredPeer", Expect.Message, F(("nodeId", Nobody))),

        new("/Admin/DevicesOnMyNetwork", "SetDevicesOnMyNetwork", Expect.Message, F(("enabled", "true"))),
        new("/Admin/DevicesOnMyNetwork", "DevicesFirewall", Expect.Message, F(("action", "add"))),

        new("/Admin/InvisibleMode", "SetInvisibleMode", Expect.Message, F(("enabled", "false"))),

        new("/Admin/SoftwareUpdates", "CheckUpdate", Expect.NoMessage, F(("manifestJson", ""), ("manifestSignatureBase64", ""))),
        new("/Admin/SoftwareUpdates", "DownloadUpdate", Expect.NoMessage, F(("manifestJson", ""))),
        new("/Admin/SoftwareUpdates", "ApplyUpdate", Expect.Message),
        new("/Admin/SoftwareUpdates", "ResetUpdate", Expect.Message),

        new("/Admin/TrustedNodes", "SetPeerSuperadmin", Expect.Message, F(("nodeId", Nobody), ("isSuperadmin", "true"))),
        new("/Admin/TrustedNodes", "RevokeNode", Expect.Message, F(("nodeId", Nobody))),
        new("/Admin/TrustedNodes", "ChangeNodeAddress", Expect.Message, F(("nodeId", Nobody), ("newApiAddress", "http://127.0.0.1:1"), ("password", "not the password"))),
        new("/Admin/TrustedNodes", "SetBlindCallable", Expect.Message, F(("nodeId", Nobody), ("trust", "off"), ("password", "not the password"))),
        new("/Admin/TrustedNodes", "SetAutoAcceptRestore", Expect.Message, F(("nodeId", Nobody), ("autoAccept", "true"))),
        new("/Admin/TrustedNodes", "SetAutoAcceptDekRotation", Expect.Message, F(("nodeId", Nobody), ("autoAccept", "true"))),

        new("/Admin/Snapshots", "CreateSnapshot", Expect.Message),
        new("/Admin/Snapshots", "DeleteSnapshot", Expect.Message, F(("fileName", "no-such-snapshot.tar.gz"))),
        new("/Admin/Snapshots", "UploadSnapshot", Expect.BadRequest),
        new("/Admin/Snapshots", "InitiateNetworkRestore", Expect.BadRequest, F(("fileName", "no-such-snapshot.tar.gz"), ("masterPassword", "not the password"))),
        new("/Admin/Snapshots", "DownloadSnapshot", Expect.DownloadRefused, F(("fileName", "no-such-snapshot.tar.gz")), "GET"),
        new("/Admin/Snapshots", "RestoreSnapshot", Expect.Message, F(("fileName", "no-such-snapshot.tar.gz"), ("masterPassword", "not the password"), ("mode", "standalone"))),

        new("/Admin/Compaction", "Compact", Expect.Message),

        new("/Admin/MasterPassword", "ChangeMasterPassword", Expect.Message, F(("oldPassword", "not the password"), ("newPassword", "Another-Pass-9"))),
        new("/Admin/MasterPassword", "DismissPasswordNotice", Expect.Message),

        new("/Admin/OsAutoUnlock", "EnableAutoUnlock", Expect.Message),
        new("/Admin/OsAutoUnlock", "DisableAutoUnlock", Expect.Message),

        new("/Admin/MetadataEncryption", "CheckMetadataReadiness", Expect.NoMessage),
        new("/Admin/MetadataEncryption", "ActivateMetadata", Expect.Message, F(("masterPassword", "not the password"))),

        new("/Admin/DekRotation", "ProposeDekRotation", Expect.Message, F(("masterPassword", "not the password"))),
        new("/Admin/DekRotation", "AcceptDekRotation", Expect.Message, F(("commitEventId", Nobody), ("masterPassword", "not the password"))),
        new("/Admin/DekRotation", "CancelDekRotation", Expect.Message, F(("eventId", Nobody))),
        new("/Admin/DekRotation", "PeerAcceptDekRotation", Expect.Message, F(("eventId", Nobody))),
        new("/Admin/DekRotation", "PeerRejectDekRotation", Expect.Message, F(("eventId", Nobody))),

        new("/Admin/Agents", "DeleteAgent", Expect.Message, F(("agentId", "999999"))),

        new("/Admin/TagGraph", "RebuildGraph", Expect.NoMessage),

        new("/Admin/ResetNode", "ResetNode", Expect.Message, F(("masterPassword", "not the password"))),
    ];

    public static IEnumerable<object[]> CaseKeys() => Cases.Select(c => new object[] { c.Page, c.Handler });

    /// <summary>The handlers the old page had: each of them is served by a page of the area, under the same name.</summary>
    private static readonly string[] OldHandlers =
    [
        "CheckUpdate", "DownloadUpdate", "ApplyUpdate", "ResetUpdate", "ChangeMasterPassword", "SetDevicesOnMyNetwork", "DevicesFirewall", "SetInvisibleMode",
        "DismissPasswordNotice", "SetPeerSuperadmin", "ConfirmRestoredPeer", "RevokeNode", "ChangeNodeAddress", "SetBlindCallable", "SetAutoAcceptRestore",
        "SetAutoAcceptDekRotation", "CreateSnapshot", "DeleteSnapshot", "UploadSnapshot", "InitiateNetworkRestore", "DownloadSnapshot", "RestoreSnapshot", "DeleteAgent",
        "RebuildGraph", "Compact", "CheckMetadataReadiness", "ActivateMetadata", "ProposeDekRotation", "AcceptDekRotation", "CancelDekRotation",
        "PeerAcceptDekRotation", "PeerRejectDekRotation", "EnableAutoUnlock", "DisableAutoUnlock", "ResetNode",
    ];

    [Fact]
    public void TheTableCoversEveryHandlerOfEveryPage_AndEveryHandlerTheOldPageHad()
    {
        var served = new List<(string Page, string Handler)>();
        foreach (var type in typeof(AdminPageModel).Assembly.GetTypes().Where(t => typeof(AdminPageModel).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var name = type.Name[..^"Model".Length];
            var page = name == "Index" ? "/Admin" : "/Admin/" + name;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var match = Regex.Match(method.Name, "^On(Get|Post)(?<h>.+?)(Async)?$");
                if (match.Success && match.Groups["h"].Value != "Async") served.Add((page, match.Groups["h"].Value));
            }
        }

        served.Should().NotBeEmpty();
        Cases.Select(c => (c.Page, c.Handler)).Should().BeEquivalentTo(served, "every handler of every page is posted by a test, and no test posts a handler that is not there");
        served.Select(s => s.Handler).Distinct().Should().Contain(OldHandlers, "the handlers of the old page all moved with their block");
        OldHandlers.Should().HaveCount(35);
    }

    [Theory]
    [MemberData(nameof(CaseKeys))]
    public async Task EveryHandler_WorksFromItsNewPage_AndSaysWhereItEndsAndWhatCameOfIt(string page, string handler)
    {
        var c = Cases.Single(x => x.Page == page && x.Handler == handler);
        var html = await GetAsync(page);
        var token = Token.Match(html).Groups[1].Value;
        token.Should().NotBeNullOrEmpty($"{page} carries the antiforgery token its posts need, whether or not it has a form on it");

        HttpResponseMessage response;
        if (c.Method == "GET")
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, $"{page}?handler={handler}&{string.Join('&', c.Fields!.Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value)))}");
            response = await _admin.SendAsync(get);
        }
        else
        {
            var fields = new Dictionary<string, string>(c.Fields ?? []) { ["__RequestVerificationToken"] = token };
            using var post = new HttpRequestMessage(HttpMethod.Post, $"{page}?handler={handler}");
            if (c.Expect == Expect.BadRequest && handler == "UploadSnapshot")
            {
                var form = new MultipartFormDataContent { { new ByteArrayContent("not a snapshot"u8.ToArray()), "file", "junk.tar.gz" } };
                post.Content = form;
                post.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
            }
            else if (c.Expect == Expect.BadRequest)
            {
                post.Content = new MultipartFormDataContent { { new StringContent(c.Fields!["fileName"]), "fileName" }, { new StringContent(c.Fields["masterPassword"]), "masterPassword" } };
                post.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
            }
            else post.Content = new FormUrlEncodedContent(fields);
            response = await _admin.SendAsync(post);
        }
        using var _ = response;

        switch (c.Expect)
        {
            case Expect.BadRequest:
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{handler} answers a refusal in words, not with the page");
                (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
                break;
            case Expect.NoMessage:
                response.StatusCode.Should().Be(HttpStatusCode.Redirect, handler);
                WebUtility.UrlDecode(response.Headers.Location!.OriginalString).Should().Be(page, $"{handler} comes back to its own page");
                (await GetAsync(response.Headers.Location.OriginalString)).Should().Contain($"data-admin-page=\"{page}\"");
                break;
            default:
                response.StatusCode.Should().Be(HttpStatusCode.Redirect, $"{handler}: {await response.Content.ReadAsStringAsync()}");
                var location = WebUtility.UrlDecode(response.Headers.Location!.OriginalString);
                location.Should().StartWith(page + "?", $"{handler} comes back to its own page");
                var message = Regex.Match(location, "[?&](?:err|msg)=([^&#]*)").Groups[1].Value;
                message.Should().NotBeNullOrWhiteSpace($"{handler} says what came of it: {location}");
                var followed = await GetAsync(response.Headers.Location.OriginalString);
                Message(followed).Should().Contain(message, "and the page shows it, under the title");
                break;
        }
    }

    [Fact]
    public async Task ASnapshotCanBeCreatedDownloadedAndDeleted_FromTheSnapshotsPage()
    {
        var html = await GetAsync("/Admin/Snapshots");
        var token = Token.Match(html).Groups[1].Value;
        async Task<HttpResponseMessage> Post(string handler, Dictionary<string, string> fields)
        {
            fields["__RequestVerificationToken"] = token;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/Admin/Snapshots?handler={handler}") { Content = new FormUrlEncodedContent(fields) };
            return await _admin.SendAsync(request);
        }

        var created = await Post("CreateSnapshot", []);
        WebUtility.UrlDecode(created.Headers.Location!.OriginalString).Should().Be("/Admin/Snapshots?msg=Snapshot created");
        var list = await GetAsync("/Admin/Snapshots");
        var name = Regex.Match(list, @"<code class=""text-sm"">([^<]+\.tar\.gz)</code>").Groups[1].Value;
        name.Should().NotBeNullOrEmpty("the new snapshot is in the list");
        list.Should().Contain("/Admin/Snapshots?handler=DownloadSnapshot&fileName=");

        using var download = await _admin.GetAsync($"/Admin/Snapshots?handler=DownloadSnapshot&fileName={Uri.EscapeDataString(name)}");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.FileName.Should().Be(name);
        (await download.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(100);

        var deleted = await Post("DeleteSnapshot", new() { ["fileName"] = name });
        WebUtility.UrlDecode(deleted.Headers.Location!.OriginalString).Should().Be("/Admin/Snapshots?msg=Snapshot deleted");
        (await GetAsync("/Admin/Snapshots")).Should().NotContain($"<code class=\"text-sm\">{name}</code>");
    }

    [Fact]
    public async Task ASnapshotsPageWithNoSnapshotStillCarriesTheTokenTheScriptSendsWithItsUploadAndRestore()
    {
        // The upload, the restore and the restore of the network are sent by admin.js, which reads the token from the page: an empty list has no form to carry one.
        var html = await GetAsync("/Admin/Snapshots");

        html.Should().Contain("No snapshots");
        Token.IsMatch(html).Should().BeTrue();
        var js = await GetAsync("/js/pages/admin.js");
        js.Should().Contain("/Admin/Snapshots?handler=UploadSnapshot").And.Contain("/Admin/Snapshots?handler=InitiateNetworkRestore").And.Contain("/Admin/Snapshots?handler=RestoreSnapshot");
        js.Should().NotMatchRegex(@"'/Admin\?handler=");
    }

    [Fact]
    public async Task AWrongPasswordForTheWipe_ComesBackToItsPageAndNeverToTheSetupScreen()
    {
        var html = await GetAsync("/Admin/ResetNode");
        var token = Token.Match(html).Groups[1].Value;

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/ResetNode?handler=ResetNode")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["masterPassword"] = "not the password", ["__RequestVerificationToken"] = token }),
        };
        using var response = await _admin.SendAsync(post);

        WebUtility.UrlDecode(response.Headers.Location!.OriginalString).Should().Be("/Admin/ResetNode?err=Invalid master password");
        (await _api.Services.GetRequiredService<BeeMemoryBank.Core.Interfaces.INodeIdentityRepository>().GetAsync()).Should().NotBeNull("nothing was wiped");
        (await GetAsync("/Admin")).Should().Contain("Reset This Node");
    }

    [Fact]
    public async Task ThePagesThatOnlyPointElsewhere_PointToTheRightPlace()
    {
        (await GetAsync("/Admin/ConnectDevice")).Should().Contain("href=\"/Connect\"");
        (await GetAsync("/Admin/BlindNodes")).Should().Contain("href=\"/BlindNodes\"");
        (await GetAsync("/Admin/HardDelete")).Should().Contain("href=\"/HardDelete\"");
        (await GetAsync("/Admin/LockServer")).Should().Contain("href=\"/Lock\"");
        (await GetAsync("/Admin/StorageProtection")).Should().Contain("href=\"/VaultEncryption\"");
        foreach (var target in new[] { "/Connect", "/BlindNodes", "/HardDelete", "/VaultEncryption" })
        {
            using var response = await _admin.GetAsync(target);
            response.StatusCode.Should().Be(HttpStatusCode.OK, target);
        }
    }

    [Fact]
    public async Task TheOldAddressesStillAnswer_AndTheIndexMapsEveryOldFragmentToThePageOfItsBlock()
    {
        var raw = await GetRawAsync("/Admin");
        var index = WebUtility.HtmlDecode(raw);

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(WebUtility.HtmlDecode(Regex.Match(raw, "id=\"admin-legacy-anchors\"[^>]*data-map=\"([^\"]*)\"").Groups[1].Value))!;
        map.Should().BeEquivalentTo(AdminPageCatalog.LegacyAnchors.ToDictionary(p => p.Key, p => p.Value));
        map["card-snapshots"].Should().Be("/Admin/Snapshots");
        map["card-metadata"].Should().Be("/Admin/MetadataEncryption");
        index.Should().Contain("/js/pages/admin-index.js?v=");
        (await GetAsync("/js/pages/admin-index.js")).Should().Contain("location.replace");
        // /Admin?card=... of the old redirects keeps working too: a query value the index does not know is ignored, a message is shown
        var withMessage = await GetAsync("/Admin?msg=Hello%20there&card=card-snapshots");
        Message(withMessage).Should().Contain("Hello there");
    }
}
