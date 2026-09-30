using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web page "Blind nodes" (add a blind node by its pair code, reseed, disconnect) through the real
/// Web host in front of a real Api: how the operator gets to it, and what the page tells them.
/// </summary>
public sealed class BlindNodesPageTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private const string BobPassword = "BobPass1234";
    private const string PairCode = "BMBBLIND1.the-code-the-operator-pasted";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private ScriptedApi _scripted = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new
        {
            username = "bob", password = BobPassword, displayName = "Bob", role = "user",
        })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_scripted = new ScriptedApi(_api.Server.CreateHandler()));
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

    /// <summary>The real Api, except that the blind-node routes can be made to answer what a blind node's failures look like.</summary>
    private sealed class ScriptedApi(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public (HttpStatusCode Status, string Body)? Add { get; set; }
        public (HttpStatusCode Status, string Body)? Reseed { get; set; }
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.StartsWith("/api/blind-nodes", StringComparison.Ordinal))
            {
                if (Down) throw new HttpRequestException("Connection refused (localhost:5300)");
                var scripted = path.EndsWith("/reseed", StringComparison.Ordinal) ? Reseed : path.TrimEnd('/') == "/api/blind-nodes" ? Add : null;
                if (scripted is { } s)
                    return Task.FromResult(new HttpResponseMessage(s.Status)
                    {
                        Content = new StringContent(s.Body, Encoding.UTF8, "application/json"),
                    });
            }
            return base.SendAsync(request, ct);
        }
    }

    // ── how the operator gets there ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAdminPage_LinksToBlindNodes_ForASuperadmin()
    {
        var page = await (await _admin.GetAsync("/Admin")).Content.ReadAsStringAsync();

        page.Should().Contain("href=\"/BlindNodes\"",
            "the page that adds a blind node had no link anywhere: the owner had to type its address");
        (await _admin.GetAsync("/BlindNodes")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ARegularUser_GetsNeitherTheAdminPageNorTheBlindNodesPage_SoNeitherLinkIsShown()
    {
        var admin = await _bob.GetAsync("/Admin");
        var blind = await _bob.GetAsync("/BlindNodes");

        admin.StatusCode.Should().NotBe(HttpStatusCode.OK);
        blind.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await admin.Content.ReadAsStringAsync()).Should().NotContain("href=\"/BlindNodes\"");
    }

    // ── what the page says when something fails ─────────────────────────────────────────────────
    // Every message names the device or the cause AND says what to do about it, in words: the owner
    // was told nothing at all, and "protocol 2" alone helps nobody.

    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Posts a form of the page the way the browser does (antiforgery), follows one redirect, returns the page shown.</summary>
    private async Task<(HttpStatusCode Status, string Html)> SubmitAsync(string handler, Dictionary<string, string> fields)
    {
        var form = await _admin.GetAsync("/BlindNodes");
        var html = await form.Content.ReadAsStringAsync();
        var session = string.Join("; ", _admin.DefaultRequestHeaders.GetValues("Cookie"));
        var antiforgery = form.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(c => c.Split(';')[0])) : "";
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = Token.Match(html).Groups[1].Value };
        using var post = new HttpRequestMessage(HttpMethod.Post, $"/BlindNodes?handler={handler}")
        {
            Content = new FormUrlEncodedContent(body),
        };
        post.Headers.TryAddWithoutValidation("Cookie", session + "; " + antiforgery);
        var resp = await _admin.SendAsync(post);
        if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
            resp = await _admin.GetAsync(resp.Headers.Location!.OriginalString);
        return (resp.StatusCode, WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync()));
    }

    private Task<(HttpStatusCode Status, string Html)> AddAsync(string code = PairCode) =>
        SubmitAsync("Add", new Dictionary<string, string> { ["code"] = code });

    private Task<(HttpStatusCode Status, string Html)> ReseedAsync() =>
        SubmitAsync("Reseed", new Dictionary<string, string> { ["nodeId"] = Guid.NewGuid().ToString() });

    private static string Error(string message, string? code = null) =>
        code is null ? $$"""{"error":"{{message}}"}""" : $$"""{"error":"{{message}}","code":"{{code}}"}""";

    private static void ShowsInOrder(string html, params string[] fragments)
    {
        foreach (var fragment in fragments)
            html.Should().Contain(fragment);
        html.Should().Contain("blind-error", "the message is the alert at the top of the page");
    }

    [Fact]
    public async Task APreflightRefusal_NamesTheDevice_SaysWhatToDo_AndKeepsThePairCode()
    {
        _scripted.Add = (HttpStatusCode.Conflict, """
            {"error":"This PC cannot add a blind node yet: \"Galaxy Tab A7 Lite\" has not synced with this build for over 7 days: update it or remove it from the network first.",
             "problems":["\"Galaxy Tab A7 Lite\" has not synced with this build for over 7 days: update it or remove it from the network first."],
             "details":[{"kind":"unknown_protocol","device":"Galaxy Tab A7 Lite","days":7}]}
            """);

        var (status, html) = await AddAsync();

        status.Should().Be(HttpStatusCode.OK, "the page is shown again where the operator is, not redirected away");
        ShowsInOrder(html,
            "Cannot add the blind node yet",
            "The device 'Galaxy Tab A7 Lite' has not been seen on the current version (sync protocol 3) for over 7 days",
            "Update it to the latest version and open it once so it syncs, or remove it under Admin → Nodes",
            "press Add again");
        html.Should().Contain($">{PairCode}</textarea>", "a failed add must not throw the pasted code away");
    }

    [Fact]
    public async Task EveryPreflightProblem_IsListedWithItsDeviceAndItsOwnAction()
    {
        _scripted.Add = (HttpStatusCode.Conflict, """
            {"error":"This PC cannot add a blind node yet: x",
             "problems":["a","b","c"],
             "details":[{"kind":"old_protocol","device":"Old phone","protocol":2},
                        {"kind":"not_superadmin","device":"Hub"},
                        {"kind":"refused","device":"Kitchen tablet"}]}
            """);

        var (_, html) = await AddAsync();

        ShowsInOrder(html,
            "The device 'Old phone' runs an older version (sync protocol 2; the blind node needs 3)",
            "The device 'Hub' does not yet accept this computer as a superadmin",
            "On 'Hub' open Admin → Nodes and make this computer a superadmin",
            "The device 'Kitchen tablet' did not accept this computer's check",
            "remove it under Admin → Nodes");
    }

    [Fact]
    public async Task APreflightRefusal_FromAnApiWithoutDetails_StillShowsItsOwnSentences()
    {
        _scripted.Add = (HttpStatusCode.Conflict,
            """{"error":"This PC cannot add a blind node yet: x","problems":["\"Hub\" does not see this PC as superadmin: promote this PC on \"Hub\" (Admin → Nodes → superadmin) and try again."]}""");

        var (_, html) = await AddAsync();

        ShowsInOrder(html, "Cannot add the blind node yet", "\"Hub\" does not see this PC as superadmin");
    }

    [Theory]
    // a code that is not a pair code at all (BlindPairCode.Parse)
    [InlineData(400, """{"error":"Not a blind node pair code."}""",
        "not a valid pair code|Copy the whole code again|Pairing|Technical detail: Not a blind node pair code.")]
    // the address in the code does not answer
    [InlineData(502, """{"error":"Could not reach the blind node at https://192.168.1.75:5610 with the key in its code (No route to host)."}""",
        "could not reach the blind node at https://192.168.1.75:5610|running|firewall|Renew code|Technical detail: Could not reach")]
    // something else answers at that address
    [InlineData(502, """{"error":"The node at https://192.168.1.75:5610 is not the one in the pair code."}""",
        "answered as a different node|Copy the pair code again|press Add")]
    // the code has expired or was used (the blind node refuses the seed)
    [InlineData(502, """{"error":"The blind node refused the seed (401): {}"}""",
        "did not accept this pair code|expired or was already used|Renew code|Technical detail: The blind node refused the seed (401)")]
    [InlineData(502, """{"error":"The blind node refused the seed (409): {\"error\":\"Another seed is in progress.\"}"}""",
        "still receiving another copy|Wait a few minutes|press Add again")]
    [InlineData(502, """{"error":"The blind node refused the seed (507): A seed of this size needs about 900 MB free here; 100 MB are."}""",
        "free disk space|Free some space|press Add again|900 MB")]
    [InlineData(502, """{"error":"The blind node refused the seed (400): SHA-256 of the received package does not match."}""",
        "refused the copy|Renew code|update the blind node|SHA-256")]
    // not allowed
    [InlineData(403, """{"error":"Session is locked","code":"session_locked"}""", "vault on this computer is locked|Unlock")]
    [InlineData(403, """{"error":"Superadmin required"}""", "Only a superadmin|Sign in as a superadmin")]
    // the Api itself failed
    [InlineData(500, """{"error":"boom"}""", "error 500|Add again|Admin → Diagnostics|Technical detail: boom")]
    public async Task AFailedAdd_SaysWhatIsWrongAndWhatToDo_AndKeepsThePairCode(int status, string body, string expected)
    {
        _scripted.Add = ((HttpStatusCode)status, body);

        var (_, html) = await AddAsync();

        ShowsInOrder(html, expected.Split('|'));
        html.Should().Contain($">{PairCode}</textarea>");
    }

    [Fact]
    public async Task AnAddWithNoCode_SaysWhereToFindIt()
    {
        var (_, html) = await AddAsync(code: "  ");

        ShowsInOrder(html, "Paste the pair code shown on the blind node's console page", "press Add");
    }

    [Fact]
    public async Task WhenTheServerDoesNotAnswer_TheAddSaysSo_InsteadOfAnErrorPage()
    {
        _scripted.Down = true;

        var (status, html) = await AddAsync();

        status.Should().Be(HttpStatusCode.OK);
        ShowsInOrder(html, "server on this computer did not answer", "restart Bee Memory Bank", "Technical detail: Connection refused");
        html.Should().Contain($">{PairCode}</textarea>");
    }

    [Theory]
    [InlineData(404, "{}", "not in the list any more|add it again with a new pair code")]
    [InlineData(502, """{"error":"The blind node refused the seed (401): {}"}""", "no longer accepts this computer|Renew code|press Add")]
    [InlineData(502, """{"error":"The blind node refused the seed (507): no room"}""", "free disk space|press Reseed again")]
    [InlineData(500, """{"error":"Connection refused"}""", "could not reach the blind node|firewall|press Reseed again|Technical detail: Connection refused")]
    [InlineData(403, """{"error":"Session is locked","code":"session_locked"}""", "vault on this computer is locked|Unlock")]
    public async Task AFailedReseed_SaysWhatIsWrongAndWhatToDo(int status, string body, string expected)
    {
        _scripted.Reseed = ((HttpStatusCode)status, body);

        var (_, html) = await ReseedAsync();

        ShowsInOrder(html, expected.Split('|'));
    }

    [Fact]
    public async Task AReseedWhenTheServerDoesNotAnswer_SaysSo()
    {
        _scripted.Down = true;

        var (_, html) = await ReseedAsync();

        ShowsInOrder(html, "server on this computer did not answer", "restart Bee Memory Bank");
    }

    [Fact]
    public async Task ADisconnectThatFails_SaysWhatToTry()
    {
        // A node id the Api does not know: it refuses, and the page must not just say "failed".
        var (_, html) = await SubmitAsync("Disconnect", new Dictionary<string, string> { ["nodeId"] = Guid.NewGuid().ToString() });

        ShowsInOrder(html, "could not be disconnected", "Try again", "superadmin");
    }
}