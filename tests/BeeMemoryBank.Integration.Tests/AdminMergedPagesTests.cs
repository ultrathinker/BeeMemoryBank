using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Release 2.7.2: every function an Admin page is about happens ON that page. The pages that used to hold a text and a button to a "second page" (<c>/BlindNodes</c>,
/// <c>/AndroidBlindCopy</c>, <c>/Connect</c>, <c>/HardDelete</c>, <c>/VaultEncryption</c>, <c>/PromotePasswordPeers</c>) are folded into their Admin page; the old addresses stay and
/// answer a temporary redirect with the query they were given.
/// </summary>
public sealed class AdminMergedPagesTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminMerged123";
    private const string BobPassword = "BobMerged1234";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "MergedNode", password: AdminPassword);
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

    // ---------------------------------------------------------------- the old addresses

    [Theory]
    [InlineData("/BlindNodes", "/Admin/BlindNodes")]
    [InlineData("/AndroidBlindCopy", "/Admin/BlindNodes#android-blind-copy")]
    [InlineData("/Connect", "/Admin/Devices#connect-device")]
    [InlineData("/HardDelete", "/Admin/HardDelete")]
    [InlineData("/VaultEncryption", "/Admin/StorageProtection")]
    [InlineData("/PromotePasswordPeers", "/Admin/Devices#promote-password-peers")]
    public async Task AnOldAddress_AnswersATemporaryRedirectToThePageThatHoldsItsFunction(string oldAddress, string newAddress)
    {
        using var response = await _admin.GetAsync(oldAddress);

        response.StatusCode.Should().Be(HttpStatusCode.Found, "a temporary redirect, not a permanent one: the address may be used for something else some day");
        response.Headers.Location!.OriginalString.Should().Be(newAddress);
    }

    [Theory]
    [InlineData("/BlindNodes", "/Admin/BlindNodes")]
    [InlineData("/AndroidBlindCopy", "/Admin/BlindNodes")]
    [InlineData("/Connect", "/Admin/Devices")]
    [InlineData("/HardDelete", "/Admin/HardDelete")]
    [InlineData("/VaultEncryption", "/Admin/StorageProtection")]
    [InlineData("/PromotePasswordPeers", "/Admin/Devices")]
    public async Task AnOldAddress_KeepsTheMessageAndTheErrorOfItsQuery_AndDropsAHandlerOfTheOldPage(string oldAddress, string newPage)
    {
        using var response = await _admin.GetAsync(oldAddress + "?msg=Blind%20node%20added%20and%20seeded.&err=It%20failed&detail=Because&warn=Careful&handler=Reseed");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location!.OriginalString;
        var query = WebUtility.UrlDecode(location);
        query.Should().StartWith(newPage + "?").And.Contain("msg=Blind node added and seeded.").And.Contain("err=It failed")
            .And.Contain("detail=Because").And.Contain("warn=Careful");
        location.Should().NotContainEquivalentOf("handler", "the handler named a handler of the old page, which is gone");
    }

    [Theory]
    [InlineData("/BlindNodes")]
    [InlineData("/AndroidBlindCopy")]
    [InlineData("/Connect")]
    [InlineData("/HardDelete")]
    [InlineData("/VaultEncryption")]
    [InlineData("/PromotePasswordPeers")]
    public async Task AnOldAddress_IsStillForASuperadminOnly(string oldAddress)
    {
        using var response = await _bob.GetAsync(oldAddress);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.Headers.Location?.OriginalString.Should().NotContain("/Admin/", "a regular user is not sent on to the Admin area by the old address");
    }

    [Fact]
    public async Task TheFormsOfAnOldAddress_AreGone_APostToItDoesNothing()
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, "/BlindNodes?handler=Add") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "x" }) };

        using var response = await _admin.SendAsync(post);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(300, "the old page has no handler for a POST: the forms post to the Admin page");
    }

    [Fact]
    public async Task Lock_GetOnlyShowsTheConfirmation_AndPostLocksTheNode()
    {
        using var get = await _admin.GetAsync("/Lock");

        get.StatusCode.Should().Be(HttpStatusCode.OK, "a GET must never lock the node");
        var html = await get.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeNullOrEmpty("the confirmation form must use antiforgery protection");

        using var post = await _admin.PostAsync("/Lock", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));

        post.StatusCode.Should().Be(HttpStatusCode.Found);
        post.Headers.Location!.OriginalString.Should().Be("/Login");
    }

    [Fact]
    public async Task TheTagsPage_OfALockedNode_SendsThePersonToTheSignIn_NotToTheLockConfirmation()
    {
        // It used to redirect to /Lock, which locked and signed out on a GET; /Lock now asks first, and a person who only wants to sign in must not be asked to lock.
        _api.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>().Lock();

        using var response = await _admin.GetAsync("/Tags");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        WebUtility.UrlDecode(response.Headers.Location!.OriginalString).Should().Be("/Login?returnUrl=/Tags");
    }

    [Fact]
    public async Task ThePagesNothingLinkedTo_AreReachedFromTheAdminPageWhereAPersonLooksForThem()
    {
        // /RemoteAccounts (read-only mirrors from another node) from Devices; /RekeyReport (the checklist after an offline re-key) from DEK Rotation.
        (await (await _admin.GetAsync("/Admin/Devices")).Content.ReadAsStringAsync()).Should().Contain("href=\"/RemoteAccounts\"");
        (await (await _admin.GetAsync("/Admin/DekRotation")).Content.ReadAsStringAsync()).Should().Contain("href=\"/RekeyReport\"");
        (await _admin.GetAsync("/RemoteAccounts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync("/RekeyReport")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- Hard Delete

    [Fact]
    public async Task TheHardDeletePage_HoldsTheListTheConfirmationAndTheAuditLog_NotAButtonToAnotherPage()
    {
        var html = WebUtility.HtmlDecode(await (await _admin.GetAsync("/Admin/HardDelete")).Content.ReadAsStringAsync());

        html.Should().NotContain("href=\"/HardDelete\"");
        // the list with its filter and its paging
        html.Should().Contain("id=\"filter-input\"").And.Contain("id=\"status-select\"").And.Contain("id=\"list-body\"").And.Contain("id=\"btn-next\"");
        // the step that shows what the deletion will remove, and then asks: the typed phrase and the tick, and a button that stays off until both are given
        html.Should().Contain("id=\"dlg-confirm-delete\"").And.Contain("id=\"delete-preview\"").And.Contain("id=\"confirm-input\"").And.Contain("id=\"confirm-checkbox\"");
        Regex.Match(html, "<sl-button[^>]*id=\"btn-final-delete\"[^>]*>").Value.Should().Contain("disabled");
        html.Should().Contain("I understand this cannot be undone").And.Contain("This operation is permanent and cannot be undone");
        // the audit log and the script that drives all of it
        html.Should().Contain("id=\"btn-open-audit\"").And.Contain("id=\"dlg-audit\"").And.Contain("/js/pages/hard-delete.js");
        // its table is the direct child of a card: the wide layout of the Admin area applies, the page does not scroll sideways inside a card 880px wide
        html.Should().MatchRegex(@"<div class=""card"" id=""table-container"">\s*<table");
    }

    [Fact]
    public async Task TheHardDeleteScript_StillTalksToTheProxyOfTheApi_NotToAPageOfTheOldAddress()
    {
        var js = await (await _admin.GetAsync("/js/pages/hard-delete.js")).Content.ReadAsStringAsync();

        js.Should().Contain("/api-proxy/hard-delete/list").And.Contain("/api-proxy/hard-delete/folder/preview").And.Contain("/api-proxy/hard-delete/audit");
        js.Should().NotContain("'/HardDelete").And.NotContain("\"/HardDelete");
    }

    [Fact]
    public async Task HardDeleteOfAnEmptyVault_ListsNothing_AndTheScriptSaysThereIsNothingToDelete()
    {
        // The list of a vault without an article or a folder used to fail with a 409 (a Dapper error), and the page never loaded.
        using var list = await _admin.GetAsync("/api-proxy/hard-delete/list?page=1&pageSize=100&filter=&status=All");

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("totalCount").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);

        var js = await (await _admin.GetAsync("/js/pages/hard-delete.js")).Content.ReadAsStringAsync();
        js.Should().Contain("Nothing to delete: this vault has no articles or folders yet.");
    }

    // ---------------------------------------------------------------- Devices: the card that grants superadmin to the devices that joined with the password

    private async Task<Guid> AddPlainPeerAsync()
    {
        var nodeId = Guid.NewGuid();
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = "OldPhone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsSuperadmin = false,
        });
        return nodeId;
    }

    [Fact]
    public async Task ThePromoteCard_IsOnDevices_OnlyWhileThereAreCandidates_AndTheFormPromotesFromThePage()
    {
        var none = WebUtility.HtmlDecode(await (await _admin.GetAsync("/Admin/Devices")).Content.ReadAsStringAsync());
        none.Should().NotContain("id=\"promote-password-peers\"", "nothing to offer, no card");

        var peer = await AddPlainPeerAsync();
        var html = await (await _admin.GetAsync("/Admin/Devices")).Content.ReadAsStringAsync();
        var page = WebUtility.HtmlDecode(html);
        page.Should().Contain("id=\"promote-password-peers\"").And.Contain("device(s) joined with the master password are not superadmins yet.")
            .And.Contain("Whoever knows the master password is a superadmin.").And.Contain("If you demoted one of these devices on purpose, untick it. Blind nodes never appear here.")
            .And.Contain($"value=\"{peer}\"").And.Contain("Grant superadmin to the ticked devices").And.Contain("handler=PromotePasswordPeers");
        page.Should().NotContain("href=\"/PromotePasswordPeers\"", "the button to the second page is gone");

        var token = Regex.Match(html, "__RequestVerificationToken[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/Devices?handler=PromotePasswordPeers")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["nodeIds"] = peer.ToString(), ["__RequestVerificationToken"] = token }),
        };
        using var response = await _admin.SendAsync(post);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.UrlDecode(response.Headers.Location!.OriginalString).Should().Be("/Admin/Devices?msg=1 device(s) granted superadmin.");
        using (var scope = _api.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer))!.IsSuperadmin.Should().BeTrue();
        var after = WebUtility.HtmlDecode(await (await _admin.GetAsync(response.Headers.Location.OriginalString)).Content.ReadAsStringAsync());
        after.Should().Contain("1 device(s) granted superadmin.").And.NotContain("id=\"promote-password-peers\"", "nothing left to offer");
    }

    [Fact]
    public async Task ARowOfTheDevicesList_NamesTheDevice_LinksToItsPage_AndKeepsItsButtonInTheRow()
    {
        var peer = await AddPlainPeerAsync();

        var page = WebUtility.HtmlDecode(await (await _admin.GetAsync("/Admin/Devices")).Content.ReadAsStringAsync());

        page.Should().Contain("<ul class=\"device-list\" id=\"device-list\">");
        var row = Regex.Match(page, $"<li[^>]*data-node-id=\"{peer}\".*?</li>", RegexOptions.Singleline).Value;
        row.Should().Contain("<li class=\"device-row ").And.Contain($"<a class=\"device-name\" href=\"/Admin/Device/{peer}\">OldPhone</a>");
        row.Should().Contain("class=\"device-open\"", "the Details button is in the row, after the sentence");
    }

    // ---------------------------------------------------------------- one id, one element

    private static readonly Regex Id = new(@"\sid=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>The ids of the markup, in order: a page with two elements of one id is a page whose script and whose anchors reach the wrong one.</summary>
    private static List<string> IdsOf(string html) => Id.Matches(html).Select(m => m.Groups[1].Value).ToList();

    [Theory]
    [InlineData("/Admin/BlindNodes")]
    [InlineData("/Admin/Devices")]
    [InlineData("/Admin/NetworkSettings")]
    [InlineData("/Admin/HardDelete")]
    [InlineData("/Admin/StorageProtection")]
    [InlineData("/Admin/OsAutoUnlock")]
    public async Task AMergedPage_HasEveryIdOnce(string page)
    {
        var html = await (await _admin.GetAsync(page)).Content.ReadAsStringAsync();

        var ids = IdsOf(html);

        ids.Should().NotBeEmpty();
        ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty($"{page} must not repeat an id");
    }
}

/// <summary>
/// Release 2.7.2, read from the sources: an Admin page is never "a text and a button to a second page". These guards keep the stubs from coming back.
/// </summary>
public sealed class AdminNoSecondPageSourceTests
{
    private static readonly string Pages = Path.Combine(DiSnapshot.RepoRoot(), "server", "BeeMemoryBank.Web", "Pages");
    private static readonly string AdminDir = Path.Combine(Pages, "Admin");

    /// <summary>The pages outside /Admin an Admin page may lead to, and why: other people's functions, or an action that is not a page of the area.</summary>
    private static readonly string[] Allowed =
    [
        "/Users",          // who the users are and what each may see: the Users page
        "/Profile",        // a person makes their own agent key on their own Profile
        "/Network",        // the network map in the main menu (read-only)
        "/Login",          // the sign-in page
        "/Lock",           // confirmation page for the lock action
        "/RemoteAccounts", // mirrors folders read-only from ANOTHER Bee Memory Bank: a function of any signed-in user, linked from Devices
        "/RekeyReport",    // the checklist after an offline re-key: the desktop app opens it once, this is the way back to it
        "/Setup",          // where a wiped node goes (Reset This Node)
        "/connect/ca.crt", // a file, the certificate of this node, not a page
    ];

    private static readonly Regex Link = new(@"\b(?:href|asp-page)=""(/[^""@#?]*)", RegexOptions.Compiled);
    private static readonly Regex Redirect = new(@"\bRedirect(?:ToPage)?\(\s*""(/[^""?#]*)", RegexOptions.Compiled);

    private static bool IsInsideTheArea(string path) =>
        path.Equals("/Admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Admin/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssetOrFile(string path) =>
        path.StartsWith("/js/", StringComparison.Ordinal) || path.StartsWith("/css/", StringComparison.Ordinal) || path.StartsWith("/lib/", StringComparison.Ordinal);

    [Fact]
    public void NoAdminPage_LeadsToAPageOutsideTheArea_ForItsOwnFunction()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AdminDir, "*.cshtml").Concat(Directory.EnumerateFiles(AdminDir, "*.cs")))
        {
            var text = File.ReadAllText(file);
            var found = file.EndsWith(".cs", StringComparison.Ordinal) ? Redirect.Matches(text) : Link.Matches(text);
            foreach (Match match in found)
            {
                var path = match.Groups[1].Value;
                if (IsInsideTheArea(path) || IsAssetOrFile(path) || Allowed.Contains(path, StringComparer.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)} -> {path}");
            }
        }

        offenders.Should().BeEmpty("a page of the Admin area holds the function it is about; the only links out are to the pages of other functions: " + string.Join(", ", Allowed));
    }

    [Theory]
    [InlineData("BlindNodes")]
    [InlineData("AndroidBlindCopy")]
    [InlineData("Connect")]
    [InlineData("HardDelete")]
    [InlineData("VaultEncryption")]
    [InlineData("PromotePasswordPeers")]
    public void TheOldPage_IsOnlyARedirect_WithNoHandlerOfItsOwn(string name)
    {
        var view = File.ReadAllLines(Path.Combine(Pages, name + ".cshtml")).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var model = File.ReadAllText(Path.Combine(Pages, name + ".cshtml.cs"));

        view.Should().HaveCount(2, "a page and its model, nothing to draw");
        view[0].Trim().Should().Be("@page");
        view[1].Should().Contain($"Pages.{name}RedirectModel");
        model.Should().Contain("LegacyPageRedirectModel(").And.NotContain("OnPost").And.NotContain("OnGet", "the base class answers the GET, and no POST is served");
    }

    [Fact]
    public void EveryAdminPage_DoesSomethingOnItself_ItIsNotOnlyATextAndAButton()
    {
        // The body of a page is what follows its About section. It must hold something that works on this page: a form, a table, a field, a switch, a dialog, a button of
        // its script (a button with no address) or data the page shows (@Model.). A body that is only a link or a button with an address is the stub of the old Blind nodes /
        // Connect / Hard Delete.
        var working = new Regex(@"<form|<table|<sl-input|<sl-switch|<sl-select|<sl-checkbox|<sl-textarea|<sl-dialog|<textarea|<select|<input|<sl-button(?![^>]*\bhref=)|<script|@Model\.", RegexOptions.Compiled);
        // Lock Server is the one exception, decided by the owner: its button is /Lock, an action (it locks the node and signs out) and not a page of a function.
        string[] exempt = ["LockServer.cshtml"];
        var stubs = new List<string>();
        // A page that only redirects (the route of a page the Devices section replaced) draws nothing: it is no page of the area.
        foreach (var file in Directory.EnumerateFiles(AdminDir, "*.cshtml").Where(f => !Path.GetFileName(f).StartsWith('_') && Path.GetFileName(f) != "Index.cshtml" && !exempt.Contains(Path.GetFileName(f))
                     && !File.ReadAllText(f + ".cs").Contains(" : LegacyPageRedirectModel(", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var about = text.IndexOf("@section About", StringComparison.Ordinal);
            var close = about < 0 ? -1 : text.IndexOf("\n}", about, StringComparison.Ordinal);
            var body = close < 0 ? text : text[(close + 2)..];
            if (!working.IsMatch(body)) stubs.Add(Path.GetFileName(file));
        }

        stubs.Should().BeEmpty("these pages hold nothing that works on the page itself");
    }
}
