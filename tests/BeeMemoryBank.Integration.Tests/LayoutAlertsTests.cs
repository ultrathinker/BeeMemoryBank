using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The node's notices (a node that cannot catch up, a peer on a newer protocol, changes waiting for a key, the one-time banners) sit
/// in a strip ABOVE the page. They used to be rendered inside .app-body, the row of the tree, the splitter and the page: there an
/// alert became a column of its own, the tree was pushed to the right and the page squeezed. Each notice is now one closable line
/// with "Details" for a long sentence and a link to the page that helps, and a closed one asks the node again later and comes back
/// while the problem lasts. The sentence itself is the node's and is not changed here.
/// </summary>
public sealed class LayoutAlertsTests : IAsyncLifetime
{
    private const string Password = "LayoutAlerts123";

    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "server", "BeeMemoryBank.Web");

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", Password);
    }

    public Task DisposeAsync()
    {
        _apiClient.Dispose();
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ANodeThatCannotCatchUp_IsSaidInTheStripAboveThePage_NotInTheRowOfTheTree()
    {
        _api.Services.GetRequiredService<SnapshotRequiredState>()
            .Set(new SnapshotRequiredException("https://host.example", 8, 14, "410"));

        var html = WebUtility.HtmlDecode(await PageAsync());

        var strip = html.IndexOf("class=\"app-alerts\"", StringComparison.Ordinal);
        var alert = html.IndexOf("data-testid=\"snapshot-required-alert\"", StringComparison.Ordinal);
        var body = html.IndexOf("<div class=\"app-body\">", StringComparison.Ordinal);
        strip.Should().BeGreaterThan(0);
        alert.Should().BeGreaterThan(strip, "the notice is in the strip");
        body.Should().BeGreaterThan(alert, "and the strip ends before the row of the tree and the page begins");
        html[body..].Should().MatchRegex(@"^<div class=""app-body"">\s*<div class=""sidebar""",
            "the row starts with the tree, as it does without a notice: nothing of the layout's notices is left in it");

        var tag = html[html.LastIndexOf("<sl-alert", alert, StringComparison.Ordinal)..html.IndexOf('>', alert)];
        tag.Should().Contain("closable").And.Contain("data-reshow-after=\"600\"").And.Contain("data-reshow-state=\"snapshotRequired\"");
        html.Should().Contain("This node cannot catch up: https://host.example compacted its log past this node's position",
            "the node's sentence is shown as it is");
        html.Should().Contain("data-alert-details").And.Contain("href=\"/Admin/Devices\"");
    }

    [Fact]
    public async Task WithNothingToSay_TheStripIsEmpty()
    {
        var html = await PageAsync();

        var strip = html.IndexOf("class=\"app-alerts\"", StringComparison.Ordinal);
        var body = html.IndexOf("<div class=\"app-body\">", StringComparison.Ordinal);
        strip.Should().BeGreaterThan(0);
        html[strip..body].Should().NotContain("<sl-alert");
    }

    [Fact]
    public void TheStyleAndTheScript_KeepANoticeToOneLine_AndBringAClosedOneBack()
    {
        var css = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "css", "site.css"));
        css.Should().Contain(".app-alerts {").And.Contain(".app-alerts:not(:has(sl-alert[open])) { display: none; }");
        css.Should().Contain(".layout-alert-text { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }");

        var script = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "js", "layout.js"));
        script.Should().Contain("'sl-after-hide'").And.Contain("data-reshow-after").And.Contain("/api-proxy/sync/status");
        script.Should().Contain("[data-alert-details]");
    }

    private async Task<string> PageAsync()
    {
        var resp = await _admin.GetAsync("/Admin/Devices");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
