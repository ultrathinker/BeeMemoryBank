extern alias WebApp;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

using ProxyMethodRule = WebApp::BeeMemoryBank.Web.Services.ProxyMethodRule;
using ProxyRouteTable = WebApp::BeeMemoryBank.Web.Services.ProxyRouteTable;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// One API host + one Web host (its outbound ApiClient routed through the API's TestServer) for
/// the whole forwarder suite: building both hosts and signing in twice costs several Argon2id
/// runs, which no individual test should pay.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProxyForwarderCollection : ICollectionFixture<ProxyForwarderFixture>
{
    public const string Name = "ProxyForwarder";
}

/// <summary>Shared node: unlocked vault, an admin and a regular user, both signed in through the
/// real /Login flow, one article in <see cref="FixtureFolder"/> and one media upload.</summary>
public sealed class ProxyForwarderFixture : IAsyncLifetime
{
    public const string AdminPassword = "AdminPass123";
    public const string BobPassword = "UserPass123";
    public const string FixtureFolder = "/proxy-made";
    public const string FixtureArticleTitle = "Forwarder fixture article";

    public BmbWebApplicationFactory Api { get; } = new();
    public BmbWebHostFactory Web { get; } = new();

    // A real 1x1 PNG, not a bare signature: the API's inline-image path decodes and re-encodes
    // image uploads (ImageSharp), so a truncated file dies with a non-mapped exception -> 500.
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public HttpClient Admin { get; private set; } = null!;
    public HttpClient Bob { get; private set; } = null!;
    public Guid ArticleId { get; private set; }
    public Guid MediaId { get; private set; }

    public async Task InitializeAsync()
    {
        using var api = Api.CreateClient(); // builds the API host first
        await Api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();

        (await api.PostAsJsonAsync("/api/users", new
        {
            username = "bob",
            password = BobPassword,
            displayName = "Bob",
            role = "user",
        })).EnsureSuccessStatusCode();

        (await api.PostAsJsonAsync("/api/folders", new { path = FixtureFolder }))
            .EnsureSuccessStatusCode();

        var article = await api.PostAsJsonAsync("/api/articles", new
        {
            title = FixtureArticleTitle,
            treePath = FixtureFolder,
            content = "fixture body",
        });
        article.EnsureSuccessStatusCode();
        ArticleId = (await article.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Only now point the Web host's outbound calls at the API host, then sign in through the
        // real Razor login so both clients carry the cookie InternalKeyHandler reads claims from.
        Web.RouteOutboundHttpThrough(Api.Server.CreateHandler());
        Admin = await WebLogin.LoginAsync(Web, "admin", AdminPassword);
        Bob = await WebLogin.LoginAsync(Web, "bob", BobPassword);

        // Diagnostic split kept from debugging: the same multipart straight to the API proves the
        // endpoint itself accepts this shape before the proxy is blamed for anything.
        using var direct = await UploadAsync(api, "direct.png", "image/png", TinyPng, apiBase: true);
        direct.IsSuccessStatusCode.Should().BeTrue(
            $"DIRECT upload must succeed; status {(int)direct.StatusCode}; body: {await direct.Content.ReadAsStringAsync()}");

        using var upload = await UploadAsync(Admin, "fixture.png", "image/png", TinyPng);
        upload.IsSuccessStatusCode.Should().BeTrue(
            $"media upload must succeed; status {(int)upload.StatusCode}; body: {await upload.Content.ReadAsStringAsync()}");
        MediaId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public Task DisposeAsync()
    {
        Admin.Dispose();
        Bob.Dispose();
        ((IDisposable)Api).Dispose();
        ((IDisposable)Web).Dispose();
        return Task.CompletedTask;
    }

    internal static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string fileName, string contentType, byte[] bytes, bool apiBase = false)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent { { content, "file", fileName } };
        return await client.PostAsync(apiBase ? "/api/media/" : "/api-proxy/media/upload", form);
    }
}

/// <summary>
/// The W1 catch-all forwarder over <see cref="ProxyRouteTable"/>, exercised as the browser drives
/// it: real form login, real cookies, Web host -> API host in-process. Covers the representative
/// migrated routes, the deny-by-default denials, the per-entry role gate on every table entry and
/// method, and the two response-shaping special cases (inline media, streamed download).
/// </summary>
[Collection(ProxyForwarderCollection.Name)]
public class ProxyForwarderTests
{
    private readonly ProxyForwarderFixture _fx;

    public ProxyForwarderTests(ProxyForwarderFixture fx) => _fx = fx;

    // ── Representative migrated routes ────────────────────────────────────────────

    [Fact]
    public async Task Get_JsonIsPassedThroughVerbatim()
    {
        var response = await _fx.Admin.GetAsync("/api-proxy/session/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isUnlocked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Get_Tree_IncludesTheFixtureFolder()
    {
        var response = await _fx.Admin.GetAsync("/api-proxy/tree");

        response.IsSuccessStatusCode.Should().BeTrue($"got {(int)response.StatusCode}");
        (await response.Content.ReadAsStringAsync()).Should().Contain(ProxyForwarderFixture.FixtureFolder);
    }

    [Fact]
    public async Task Get_ForwardsTheQueryString()
    {
        var response = await _fx.Admin.GetAsync("/api-proxy/concept-tags?limit=1");

        response.IsSuccessStatusCode.Should().BeTrue($"got {(int)response.StatusCode}");
        var tags = await response.Content.ReadFromJsonAsync<JsonElement>();
        tags.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Post_Json_CreatesAFolderThroughTheProxy()
    {
        var response = await _fx.Admin.PostAsJsonAsync("/api-proxy/folders", new { path = "/proxy-made-by-forwarder" });

        response.IsSuccessStatusCode.Should().BeTrue($"got {(int)response.StatusCode}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Delete_Folder_KeepsTheBrowserContract_QueryStringIdentifiesTheFolder()
    {
        // The UI never addresses the folder as a path segment: it sends
        // DELETE /api-proxy/folders?path=... (Folder.cshtml). The forwarder must relay the
        // query string verbatim for the API's [from-query] path binding to see it.
        const string path = "/proxy-delete-me";
        (await _fx.Admin.PostAsJsonAsync("/api-proxy/folders", new { path })).EnsureSuccessStatusCode();

        var response = await _fx.Admin.DeleteAsync($"/api-proxy/folders?path={Uri.EscapeDataString(path)}");

        response.IsSuccessStatusCode.Should().BeTrue($"got {(int)response.StatusCode}");
        var tree = await _fx.Admin.GetStringAsync("/api-proxy/tree");
        tree.Should().NotContain(path, "the deleted folder must disappear from the tree");
    }

    [Fact]
    public async Task MultipartUpload_StreamsThroughToTheApi()
    {
        var response = await ProxyForwarderFixture.UploadAsync(
            _fx.Admin, "upload-test.png", "image/png", ProxyForwarderFixture.TinyPng);

        // 201 Created with the media descriptor — the API's own response, relayed as-is. The
        // API's inline-image path decodes and re-encodes uploads, so the PNG comes back as
        // .jpg: proof the multipart request arrived byte-exact AND the response travelled
        // back untouched.
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"body: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        body.GetProperty("fileName").GetString().Should().Be("upload-test.jpg");
    }

    [Fact]
    public async Task MediaGet_RendersInline_WithUntrustedContentHeaders()
    {
        var response = await _fx.Admin.GetAsync($"/api-proxy/media/{_fx.MediaId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The fixture PNG was re-encoded to JPEG by the API's inline-image path on upload; the
        // relay must preserve whatever content type the API declared.
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

        // The API serves attachment; the entry strips it — the browser must render inline,
        // exactly what the old hand-written media GET did.
        response.Content.Headers.ContentDisposition.Should().BeNull();

        // Headers the old route applied Web-side: immutable caching plus the untrusted-content
        // sandbox that keeps stored SVGs from running script when opened directly as a document.
        response.Headers.CacheControl!.ToString().Should().Contain("immutable");
        response.Headers.TryGetValues("Content-Security-Policy", out var csp).Should().BeTrue();
        csp!.Should().ContainSingle().Which.Should().Be("sandbox");
        response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).Should().BeTrue();
        nosniff!.Should().Contain("nosniff");
    }

    [Fact]
    public async Task Downloads_TokenFetch_PassesBinaryAndDispositionThrough()
    {
        var prepare = await _fx.Admin.PostAsJsonAsync("/api-proxy/downloads/prepare",
            new { kind = "folder", path = ProxyForwarderFixture.FixtureFolder, withImages = false });
        prepare.IsSuccessStatusCode.Should().BeTrue($"body: {await prepare.Content.ReadAsStringAsync()}");
        var prep = await prepare.Content.ReadFromJsonAsync<JsonElement>();
        var token = prep.GetProperty("token").GetString()!;

        var response = await _fx.Admin.GetAsync($"/api-proxy/downloads/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        var disposition = response.Content.Headers.ContentDisposition;
        disposition.Should().NotBeNull();
        disposition!.DispositionType.Should().Be("attachment");
        disposition.FileName.Should().NotBeNullOrEmpty();
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);

        // The token is single-use; the API's 404 (with its own error body) is relayed verbatim.
        var replay = await _fx.Admin.GetAsync($"/api-proxy/downloads/{token}");
        replay.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("not found or expired");
    }

    [Fact]
    public async Task KeptComposeRoute_StillWinsOverTheCatchAll()
    {
        // GET /api-proxy/article/{id} is a hand-written compose (metadata + content) that stays
        // explicit; it must beat the table's "article" prefix. If it ever fell through to the
        // forwarder, the browser would get the API's bare metadata shape instead.
        var response = await _fx.Admin.GetAsync($"/api-proxy/article/{_fx.ArticleId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("article").GetProperty("id").GetGuid().Should().Be(_fx.ArticleId);
        body.GetProperty("content").GetString().Should().Be("fixture body");
    }

    [Fact]
    public async Task ChatStream_KeepsItsExplicitRoute()
    {
        // POST /api-proxy/chat/stream is not in the table: if the explicit SSE route were gone,
        // the catch-all would answer 404 (unknown prefix). Anything but the two denials proves
        // the request reached the explicit route and was forwarded to the API.
        var response = await _fx.Admin.PostAsJsonAsync("/api-proxy/chat/stream", new { });

        ((int)response.StatusCode).Should().NotBe(404);
        ((int)response.StatusCode).Should().NotBe(405);
    }

    // ── Role gates: both layers kept ─────────────────────────────────────────────

    [Fact]
    public async Task SuperadminOnlyRead_AsRegularUser_IsRefusedByTheWebGate()
    {
        var response = await _fx.Bob.GetAsync("/api-proxy/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("superadmin only");
    }

    [Fact]
    public async Task SuperadminOnlyMutation_AsRegularUser_NeverReachesTheApi()
    {
        var response = await _fx.Bob.PostAsJsonAsync("/api-proxy/users", new
        {
            username = "sneaky",
            password = ProxyForwarderFixture.BobPassword,
            displayName = "Sneaky",
            role = "user",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("superadmin only");

        var users = await _fx.Admin.GetFromJsonAsync<JsonElement>("/api-proxy/users");
        users.EnumerateArray().Select(u => u.GetProperty("username").GetString())
            .Should().NotContain("sneaky");
    }

    [Fact]
    public async Task Superadmin_PassesTheWebGate_AndTheApiEnforcesItsOwnLayer()
    {
        var response = await _fx.Admin.GetAsync("/api-proxy/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await response.Content.ReadFromJsonAsync<JsonElement>();
        users.EnumerateArray().Select(u => u.GetProperty("username").GetString())
            .Should().Contain(["admin", "bob"]);
    }

    // ── Deny-by-default ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownPrefix_Is404_NeverABlindForward()
    {
        (await _fx.Admin.GetAsync("/api-proxy/does-not-exist"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.Admin.PostAsJsonAsync("/api-proxy/does-not-exist", new { x = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PrefixAdjacentToAnEntry_Is404_SegmentAwareMatching()
    {
        (await _fx.Admin.GetAsync("/api-proxy/concept-tagsx"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlistedMethod_Is405_AndAdvertisesTheAllowSet()
    {
        var response = await _fx.Admin.GetAsync("/api-proxy/folders");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        // System.Net.Http classifies Allow as a CONTENT header, so it surfaces on
        // Content.Headers, not on the response's header collection.
        response.Content.Headers.Allow.Should().Contain("POST").And.Contain("PATCH").And.Contain("DELETE");

        var patch = await _fx.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api-proxy/tree"));
        patch.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // ── The table itself ─────────────────────────────────────────────────────────

    [Fact]
    public void Table_EveryListedMethod_Matches_WithTheDeclaredRole()
    {
        foreach (var (prefix, entry) in ProxyRouteTable.Entries)
        {
            entry.Methods.Should().NotBeEmpty(prefix);

            foreach (var rule in entry.Methods)
            {
                var (outcome, hit, hitRule, matched) = ProxyRouteTable.Match(prefix, rule.Method);

                outcome.Should().Be(ProxyRouteTable.MatchOutcome.Matched, $"{rule.Method} {prefix}");
                hit.Should().BeSameAs(entry);
                hitRule!.RequiredRole.Should().Be(rule.RequiredRole, prefix);
            }

            entry.UpstreamPrefix.Should().StartWith("/api/", prefix);
        }
    }

    [Fact]
    public void Table_UnlistedMethods_AreMethodNotAllowed()
    {
        string[] all = ["GET", "POST", "PUT", "DELETE", "PATCH"];

        foreach (var (prefix, entry) in ProxyRouteTable.Entries)
        {
            var listed = entry.Methods.Select(m => m.Method).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var method in all.Except(listed, StringComparer.OrdinalIgnoreCase))
                ProxyRouteTable.Match(prefix, method).Outcome
                    .Should().Be(ProxyRouteTable.MatchOutcome.MethodNotAllowed, $"{method} {prefix}");
        }
    }

    [Fact]
    public void Table_PrefixMatch_IsSegmentAware_AndOverlapsResolveToTheLongest()
    {
        // "concept-tags/graph" resolves to the "concept-tags" entry, "concept-tagsx" to nothing.
        ProxyRouteTable.Match("concept-tags/graph", "GET").MatchedPrefix.Should().Be("concept-tags");
        ProxyRouteTable.Match("concept-tagsx", "GET").Outcome
            .Should().Be(ProxyRouteTable.MatchOutcome.UnknownPrefix);

        // Specific entries override broad ones, and the upstream path swaps the whole prefix.
        var mediaUpload = ProxyRouteTable.Match("media/upload", "POST");
        mediaUpload.MatchedPrefix.Should().Be("media/upload");
        ProxyRouteTable.BuildUpstreamPath("media/upload", "media/upload", mediaUpload.Entry!)
            .Should().Be("/api/media/");

        var mediaChild = ProxyRouteTable.Match("media/00000000-0000-0000-0000-000000000000", "GET");
        mediaChild.MatchedPrefix.Should().Be("media");
        ProxyRouteTable.BuildUpstreamPath(
                "media/00000000-0000-0000-0000-000000000000", "media", mediaChild.Entry!)
            .Should().Be("/api/media/00000000-0000-0000-0000-000000000000");
    }

    // ── Table-driven HTTP gate: every entry x method, as a regular user ──────────

    public static IEnumerable<object[]> ListedEntryMethods()
    {
        foreach (var (prefix, entry) in ProxyRouteTable.Entries)
            foreach (var rule in entry.Methods)
                yield return [prefix, rule.Method, rule.RequiredRole ?? ""];
    }

    [Theory]
    [MemberData(nameof(ListedEntryMethods))]
    public async Task TableGate_RegularUser(string prefix, string method, string requiredRole)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api-proxy/{prefix}");
        var response = await _fx.Bob.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (requiredRole == UserRoles.Superadmin)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                $"{method} /api-proxy/{prefix} is declared superadmin-only");
            body.Should().Contain("superadmin only",
                "the refusal must come from the Web-side gate, not a relayed API error");
        }
        else
        {
            body.Should().NotContain("superadmin only",
                $"{method} /api-proxy/{prefix} is open to any authenticated user at the Web layer");
        }
    }
}
