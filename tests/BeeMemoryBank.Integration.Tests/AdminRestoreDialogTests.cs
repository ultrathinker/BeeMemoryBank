using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Admin → Snapshots → Restore, through the real Web host in front of a real Api. The dialog has to say
/// what a restore does and let the user choose between the two kinds (this node only / the whole
/// network); the choice has to reach the server as a named mode; and after a restore of this node the page
/// the user lands on has to say that this is a new node and what is left to do.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class AdminRestoreDialogTests : IAsyncLifetime
{
    private const string Password = "AdminPass123";

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _admin = null!;
    private string _snapshot = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "WebNode", password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var created = await _apiClient.PostAsync("/api/snapshots", null);
        created.EnsureSuccessStatusCode();
        _snapshot = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;

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

    // ── the dialog ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDialog_OffersBothKinds_WithThisNodeOnlyAsTheDefault()
    {
        var html = await AdminPageAsync();

        html.Should().Contain("id=\"restore-mode\"");
        Regex.IsMatch(html, "<sl-radio-group[^>]*id=\"restore-mode\"[^>]*value=\"standalone\"").Should().BeTrue(
            "the default is the one that touches this node only");
        html.Should().Contain("<sl-radio value=\"standalone\">Restore this node only");
        html.Should().Contain("it leaves the network and becomes a new node");
        html.Should().Contain("<sl-radio value=\"network\">Restore the whole network from this snapshot");
    }

    [Fact]
    public async Task TheDialog_ListsWhatARestoreOfThisNodeDoes_InsteadOfSayingItReplacesEverything()
    {
        var html = WebUtility.HtmlDecode(await AdminPageAsync());

        html.Should().Contain("This node becomes a new node.");
        html.Should().Contain("new identity");
        html.Should().Contain("new node id and a new signing key");
        html.Should().Contain("forgets");
        html.Should().Contain("blind copies");
        html.Should().Contain("sync history");
        html.Should().Contain("join them to this node again");
        html.Should().Contain("Pair the blind copies again");
        html.Should().NotContain("This will replace ALL data", "that sentence left the identity reset out");
    }

    [Fact]
    public async Task TheDialog_SaysWhatTheWholeNetworkRestoreNeeds()
    {
        var html = WebUtility.HtmlDecode(await AdminPageAsync());

        html.Should().Contain("Every node of the network is asked to restore this snapshot.");
        html.Should().Contain("keeps its identity");
        html.Should().Contain("Auto-restore");
        html.Should().Contain("Keep this node running and reachable");
        html.Should().Contain("Only a superadmin can start it, and the vault must be unlocked.");
        html.Should().Contain("id=\"restore-mode-network\" hidden", "it is shown only when that option is chosen");
        html.Should().NotContain("id=\"restore-mode-standalone\" hidden");
    }

    [Fact]
    public async Task TheDialog_KeepsTheBackupTickedByDefault_AndAsksForThePassword()
    {
        var html = await AdminPageAsync();

        Regex.IsMatch(html, "<sl-checkbox[^>]*id=\"restore-backup-checkbox\"[^>]*\\bchecked\\b").Should().BeTrue();
        html.Should().Contain("id=\"restore-master-password\"");
        html.Should().Contain("id=\"restore-snapshot-name-display\"", "the dialog names the snapshot being restored");
        html.Should().Contain("docs/snapshot-restore.md");
    }

    [Fact]
    public void TheScript_ReadsTheRealControl_AndPostsAModeNotALegacyFlag()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "admin.js"));

        js.Should().Contain("getElementById('restore-mode')");
        js.Should().Contain("'&mode=' + encodeURIComponent(mode)");
        js.Should().NotContain("standaloneMode", "the flag was what the missing control always defaulted");
        js.Should().Contain("return restoreModeEl && restoreModeEl.value === 'network' ? 'network' : 'standalone';",
            "anything that is not the network option is this node only — the safe default");
        js.Should().Contain("restoreModeEl.value = 'standalone';", "every opening of the dialog starts from the default");
    }

    // ── the restore, and where it lands ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ARestoreOfThisNode_LandsOnASignInPageThatSaysItIsANewNode()
    {
        var before = await NodeIdAsync();

        var resp = await PostAdminAsync("RestoreSnapshot", new()
        {
            ["fileName"] = _snapshot, ["masterPassword"] = Password, ["createBackupFirst"] = "false", ["mode"] = "standalone"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.OriginalString.Should().Be("/Login?restored=standalone");
        var after = await NodeIdAsync();
        after.Should().NotBe(before, "this node became a new node");

        var page = WebUtility.HtmlDecode(await (await _admin.GetAsync(resp.Headers.Location.OriginalString)).Content.ReadAsStringAsync());
        page.Should().Contain("id=\"restored-new-node-notice\"");
        page.Should().Contain("The snapshot was restored. This node is now a new node.");
        page.Should().Contain("WebNode");
        page.Should().Contain(after.ToString(), "the notice names the node id it has now");
        page.Should().Contain("Join your other devices again", "what is left to do");
        page.Should().Contain("Pair the blind copies");
        page.Should().Contain("docs/snapshot-restore.md");
    }

    [Fact]
    public async Task TheSignInPage_SaysNothingAboutARestoreUnlessItIsTheOneKnownValue()
    {
        foreach (var query in new[] { "", "?restored=network", "?restored=<script>alert(1)</script>", "?restored=STANDALONE", "?restore=true" })
        {
            var page = await (await _admin.GetAsync("/Login" + query)).Content.ReadAsStringAsync();
            page.Should().NotContain("restored-new-node-notice", $"'{query}' is a link, not a message");
        }
        (await (await _admin.GetAsync("/Login?restored=standalone")).Content.ReadAsStringAsync())
            .Should().Contain("restored-new-node-notice");
    }

    [Fact]
    public async Task AMistypedPassword_OnTheRestoredPage_KeepsTheNoticeOnScreen()
    {
        // Anonymous, as the user is when they get there: the restore locked the vault and ended every session.
        using var visitor = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = await visitor.GetAsync("/Login?restored=standalone");
        var html = await form.Content.ReadAsStringAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Login?restored=standalone")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = "admin", ["Password"] = "wrong-password", ["ReturnUrl"] = "/Tree",
                ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value
            })
        };
        post.Headers.TryAddWithoutValidation("Cookie", AntiforgeryCookies(form));

        var resp = await visitor.SendAsync(post);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("restored-new-node-notice");
    }

    [Theory]
    [InlineData("keep-identity")]
    [InlineData("everything")]
    [InlineData("network")]
    public async Task APostedModeThatIsNotThisNodeOnly_IsRefusedByThePage_AndChangesNothing(string mode)
    {
        var before = await NodeIdAsync();

        var resp = await PostAdminAsync("RestoreSnapshot", new()
        {
            ["fileName"] = _snapshot, ["masterPassword"] = Password, ["createBackupFirst"] = "false", ["mode"] = mode
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.UrlDecode(resp.Headers.Location!.OriginalString).Should().StartWith("/Admin?err=Unknown restore mode");
        (await NodeIdAsync()).Should().Be(before);
    }

    [Fact]
    public async Task ARestoreWithoutThePassword_IsRefusedByThePage()
    {
        var before = await NodeIdAsync();

        var resp = await PostAdminAsync("RestoreSnapshot", new()
        {
            ["fileName"] = _snapshot, ["createBackupFirst"] = "false", ["mode"] = "standalone"
        });

        WebUtility.UrlDecode(resp.Headers.Location!.OriginalString).Should().Contain("Enter the master password");
        (await NodeIdAsync()).Should().Be(before);
    }

    [Fact]
    public async Task ARestoreWithAWrongPassword_ComesBackToTheAdminPageWithTheReason()
    {
        var resp = await PostAdminAsync("RestoreSnapshot", new()
        {
            ["fileName"] = _snapshot, ["masterPassword"] = "wrong-password", ["createBackupFirst"] = "false", ["mode"] = "standalone"
        });

        WebUtility.UrlDecode(resp.Headers.Location!.OriginalString).Should().Be("/Admin?err=Invalid master password");
    }

    // ── the whole network, from the same dialog ─────────────────────────────────────────────────

    [Fact]
    public async Task TheNetworkRestore_NeedsThePasswordAgain_AndReportsTheApisOwnSentence()
    {
        var none = await PostAdminAsync("InitiateNetworkRestore", new() { ["fileName"] = _snapshot });
        var wrong = await PostAdminAsync("InitiateNetworkRestore", new() { ["fileName"] = _snapshot, ["masterPassword"] = "wrong-password" });

        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await none.Content.ReadAsStringAsync()).Should().Contain("Enter the master password");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("Invalid master password");
    }

    [Fact]
    public async Task TheNetworkRestore_OfASnapshotThisNodeMade_StartsFromTheDialog()
    {
        var before = await NodeIdAsync();

        var resp = await PostAdminAsync("InitiateNetworkRestore", new() { ["fileName"] = _snapshot, ["masterPassword"] = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString().Should().NotBeNullOrEmpty();
        for (var i = 0; i < 200; i++)
        {
            var progress = await (await _apiClient.GetAsync("/api/snapshots/restore/progress")).Content.ReadFromJsonAsync<JsonElement>();
            if (progress.GetProperty("currentStep").ToString() is "Completed" or "Failed")
            {
                progress.GetProperty("currentStep").ToString().Should().Be("Completed", progress.ToString());
                break;
            }
            await Task.Delay(100);
        }
        (await NodeIdAsync()).Should().Be(before, "a restore of the whole network leaves the originator the same node");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<string> AdminPageAsync()
    {
        var resp = await _admin.GetAsync("/Admin");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<Guid> NodeIdAsync()
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
    }

    private static string AntiforgeryCookies(HttpResponseMessage page) =>
        page.Headers.TryGetValues("Set-Cookie", out var set) ? string.Join("; ", set.Select(c => c.Split(';')[0])) : "";

    /// <summary>Posts a handler of the Admin page the way the browser does: the page's antiforgery token and cookie, then the session.</summary>
    private async Task<HttpResponseMessage> PostAdminAsync(string handler, Dictionary<string, string> fields)
    {
        var page = await _admin.GetAsync("/Admin");
        var html = await page.Content.ReadAsStringAsync();
        var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value };
        using var post = new HttpRequestMessage(HttpMethod.Post, $"/Admin?handler={handler}") { Content = new FormUrlEncodedContent(body) };
        post.Headers.TryAddWithoutValidation("Cookie", session + "; " + AntiforgeryCookies(page));
        return await _admin.SendAsync(post);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the tests run from inside the repository");
    }
}
