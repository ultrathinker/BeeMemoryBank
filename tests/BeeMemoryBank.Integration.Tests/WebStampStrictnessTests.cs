extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SuperadminRoutes = WebApp::BeeMemoryBank.Web.Services.SuperadminRoutes;
using WebSignIn = WebApp::BeeMemoryBank.Web.Services.WebSignIn;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web layer's half of H3. A cookie that was issued to a superadmin is checked against the API on EVERY request to a superadmin page or proxy route, with no cache,
/// and a check that cannot be answered is "sign in again", never access. Other routes keep the five-minute cache and fail open on an API hiccup (what they may DO is
/// the API's call, which reads the role from its own table: <see cref="CallerIdentityLookupTests"/>).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class WebStampStrictnessTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminStamp12345";
    private const string BobPassword = "BobStamp1234567";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private Scripted _scripted = null!;
    private HttpClient _machine = null!;
    private HttpClient _bob = null!;
    private int _bobId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "StampNode", password: AdminPassword);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        var created = await _machine.PostAsJsonAsync("/api/users", new
        {
            username = "bob", password = BobPassword, displayName = "Bob", role = "superadmin", passwordHint = "the colour of the sea",
        });
        created.EnsureSuccessStatusCode();
        _bobId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        _web.RouteOutboundHttpThrough(_scripted = new Scripted(_api.Server.CreateHandler()));
        _bob = await WebLogin.LoginAsync(_web, "bob", BobPassword);
    }

    public Task DisposeAsync()
    {
        _bob.Dispose();
        _machine.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The real Api, except for the stamp lookup a test makes fail.</summary>
    private sealed class Scripted(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool StampFails;
        public int StampCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/users/me/stamp")
            {
                Interlocked.Increment(ref StampCalls);
                if (StampFails)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            }
            return base.SendAsync(request, ct);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url) => await _bob.GetAsync(url);

    private static void ShouldSendToLogin(HttpResponseMessage response, string what)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, what);
        response.Headers.Location!.OriginalString.Should().StartWith("/Login", what);
    }

    private void ForgetCachedStamp() =>
        _web.Services.GetRequiredService<IMemoryCache>().Remove(WebSignIn.StampCacheKey(_bobId.ToString()));

    /// <summary>
    /// The administrator as the Web forwards them (2.12: demoting or deleting a superadmin is a removal process that starts with the administrator's own
    /// password, and whose first step ends every session of the person at once). A sign-in makes their door a sealed one, which the process asks for.
    /// </summary>
    private async Task<HttpClient> AdministratorAsync()
    {
        using (var scope = _api.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IUserRepository>().GetByUsernameAsync("admin"))!;
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.UserService>().UpgradeKeySlotDoorAsync(admin, AdminPassword);
            var client = _api.CreateClient();
            client.DefaultRequestHeaders.Remove("X-User-Role");
            client.DefaultRequestHeaders.Add("X-User-Id", admin.Id.ToString());
            return client;
        }
    }

    private async Task DemoteBobAsync()
    {
        using var admin = await AdministratorAsync();
        (await admin.PutAsJsonAsync($"/api/users/{_bobId}", new { displayName = "Bob", role = "user", allowOpenRole = true, adminPassword = AdminPassword }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted, "the removal starts, and its lock-out changes the stamp at once");
    }

    private async Task DeleteBobAsync()
    {
        using var admin = await AdministratorAsync();
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{_bobId}") { Content = JsonContent.Create(new { adminPassword = AdminPassword }) };
        (await admin.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the removal starts, and its lock-out changes the stamp at once");
    }

    [Fact]
    public async Task A_superadmin_with_a_good_cookie_gets_the_admin_page_and_the_proxy_route()
    {
        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/api-proxy/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/Admin/Devices")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_demoted_superadmin_with_a_valid_cookie_is_refused_by_the_very_next_admin_page_and_proxy_route()
    {
        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK, "this fills the five-minute stamp cache the demotion has to get past");

        await DemoteBobAsync();

        ShouldSendToLogin(await GetAsync("/Users"), "the very next superadmin page");
        ShouldSendToLogin(await GetAsync("/Admin/Devices"), "the Admin area is superadmin pages too");
        (await GetAsync("/api-proxy/users")).StatusCode.Should().NotBe(HttpStatusCode.OK, "nor does the proxy route answer");
    }

    [Fact]
    public async Task A_deleted_superadmin_with_a_valid_cookie_is_refused_by_the_very_next_admin_page()
    {
        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK);

        await DeleteBobAsync();

        ShouldSendToLogin(await GetAsync("/Users"), "the very next superadmin page");
        (await GetAsync("/api-proxy/users")).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task While_the_stamp_endpoint_fails_a_superadmin_page_is_refused_and_an_ordinary_page_is_not()
    {
        ForgetCachedStamp();
        _scripted.StampFails = true;

        ShouldSendToLogin(await GetAsync("/Users"), "a superadmin page is not served on a guess");
        ShouldSendToLogin(await GetAsync("/Admin/Devices"), "nor an Admin page");
        (await GetAsync("/api-proxy/users")).StatusCode.Should().NotBe(HttpStatusCode.OK, "nor a superadmin proxy route");

        // An API hiccup does not sign the whole site out: a page that is not for superadmins fails open (and the API itself decides what the caller may do there).
        var profile = await GetAsync("/Profile");
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_superadmin_route_asks_the_api_every_time_and_another_route_uses_the_cache()
    {
        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK);   // the sign-in left a stamp in the cache
        var before = _scripted.StampCalls;

        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/Users")).StatusCode.Should().Be(HttpStatusCode.OK);
        _scripted.StampCalls.Should().Be(before + 2, "one lookup per request for a superadmin page, whatever the cache holds");

        var mid = _scripted.StampCalls;
        (await GetAsync("/Profile")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/Profile")).StatusCode.Should().Be(HttpStatusCode.OK);
        _scripted.StampCalls.Should().BeLessThanOrEqualTo(mid + 1, "an ordinary page reads the cache");
    }

    [Fact]
    public async Task A_regular_users_request_for_a_superadmin_route_is_refused_by_the_role_without_asking_the_api_for_a_stamp()
    {
        // The strict check protects a cookie that was issued to a superadmin. A cookie that does not carry the role is turned away by the role gate whatever its stamp
        // says, so the stamp lookup would only cost the API a call per refused request - and the refusal is local: the API is not asked at all.
        (await _machine.PostAsJsonAsync("/api/users", new { username = "carol", password = "CarolStamp12345", displayName = "Carol", role = "user", passwordHint = "the colour of the sea" }))
            .EnsureSuccessStatusCode();
        using var carol = await WebLogin.LoginAsync(_web, "carol", "CarolStamp12345");
        var before = _scripted.StampCalls;

        var page = await carol.GetAsync("/Users");
        var proxy = await carol.GetAsync("/api-proxy/users");

        page.StatusCode.Should().NotBe(HttpStatusCode.OK, "the Users page is for superadmins");
        proxy.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the proxy route is for superadmins");
        _scripted.StampCalls.Should().Be(before, "the role refuses before the stamp is asked for");
    }

    [Fact]
    public async Task WhileTheNodeIsInMaintenance_TheAdministratorIsNotSignedOutOfThePagesThatShowHowItIsGoing()
    {
        // A DEK rotation or a restore puts the node in maintenance, and the pages that matter then are superadmin pages. The stamp is checked on every one of them, so the
        // check has to be answerable while everything else is 503 - otherwise "fail closed" would sign the administrator out of the progress page.
        var maintenance = _api.Services.GetRequiredService<BeeMemoryBank.Core.Services.MaintenanceModeService>();
        maintenance.Enter("a test of the stamp check");
        try
        {
            (await _machine.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "maintenance is on: the node answers little");
            (await GetAsync("/Admin/DekRotation")).StatusCode.Should().Be(HttpStatusCode.OK, "the stamp check is among the things it still answers");
            (await GetAsync("/api-proxy/snapshots")).StatusCode.Should().NotBe(HttpStatusCode.Found, "and the principal was not rejected");
        }
        finally { maintenance.Exit(); }
    }

    // ---------------------------------------------------------------- which routes are superadmin routes

    private HttpContext ContextFor(string method, string path)
    {
        var data = _web.Services.GetRequiredService<EndpointDataSource>();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        // The endpoint routing would pick for this path: the page or the route whose pattern is the path itself (the catch-all forwarder has none of its own).
        var endpoint = data.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e =>
            string.Equals("/" + e.RoutePattern.RawText?.TrimStart('/'), path, StringComparison.OrdinalIgnoreCase)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>() is not { } m || m.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
            ?? data.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e => e.RoutePattern.RawText == "/api-proxy/{**path}");
        if (endpoint is not null) ctx.SetEndpoint(endpoint);
        return ctx;
    }

    [Theory]
    [InlineData("GET", "/Users")]
    [InlineData("GET", "/Roles")]
    [InlineData("GET", "/Network")]
    [InlineData("GET", "/Admin/Devices")]
    [InlineData("GET", "/Admin/Snapshots")]
    [InlineData("PUT", "/api-proxy/branding")]
    [InlineData("PUT", "/api-proxy/session/settings")]
    [InlineData("GET", "/api-proxy/users")]
    [InlineData("POST", "/api-proxy/users")]
    [InlineData("DELETE", "/api-proxy/users/7")]
    [InlineData("POST", "/api-proxy/keys/add-recovery")]
    [InlineData("GET", "/api-proxy/vault/seal")]
    public void A_superadmin_page_or_proxy_route_is_recognised_as_one(string method, string path)
    {
        SuperadminRoutes.IsSuperadminRoute(ContextFor(method, path)).Should().BeTrue($"{method} {path} is for superadmins only");
    }

    [Theory]
    [InlineData("GET", "/Tree")]
    [InlineData("GET", "/Profile")]
    [InlineData("GET", "/Search")]
    [InlineData("GET", "/api-proxy/tree")]
    [InlineData("GET", "/api-proxy/search")]
    [InlineData("POST", "/api-proxy/favorites")]
    [InlineData("GET", "/api-proxy/branding")]
    [InlineData("GET", "/api-proxy/vault/status")]
    public void An_ordinary_page_or_proxy_route_is_not(string method, string path)
    {
        SuperadminRoutes.IsSuperadminRoute(ContextFor(method, path)).Should().BeFalse($"{method} {path} is open to every signed-in user");
    }

    [Fact]
    public void The_progress_poll_of_the_encryption_migration_is_not_strict_because_the_migration_closes_the_database_it_asks()
    {
        SuperadminRoutes.IsSuperadminRoute(ContextFor("GET", "/api-proxy/vault/seal/progress")).Should().BeFalse();
        SuperadminRoutes.IsSuperadminRoute(ContextFor("POST", "/api-proxy/vault/seal/progress")).Should().BeFalse("it is a GET-only exemption; the table has no POST there at all");
        SuperadminRoutes.IsSuperadminRoute(ContextFor("GET", "/api-proxy/vault/seal")).Should().BeTrue("the rest of the vault/seal prefix stays strict");
    }
}
