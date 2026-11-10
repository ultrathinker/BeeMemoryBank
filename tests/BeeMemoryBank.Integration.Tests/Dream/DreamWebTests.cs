using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Dream through the real Web host in front of a real Api (plan 3.8, 3.9): the header names Dream right after Search for a superadmin and
/// for nobody else, the Dream page is the superadmin's, and /api-proxy/dream reaches the Api for the superadmin only, with the page's
/// anti-forgery token on every change.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class DreamWebTests : IAsyncLifetime
{
    private const string AdminPassword = "DreamWeb12345";
    private const string BobPassword = "BobDream12345";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "DreamWebNode", password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user", passwordHint = "A test hint" }))
            .EnsureSuccessStatusCode();
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

    private static List<string> HeaderLinks(string html)
    {
        var nav = Regex.Match(html, "<nav class=\"header-nav\".*?</nav>", RegexOptions.Singleline);
        nav.Success.Should().BeTrue("the page has the header");
        return Regex.Matches(nav.Value, "<a href=\"([^\"]+)\" class=\"header-link\"").Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public async Task TheHeader_NamesDreamRightAfterSearch_ForTheSuperadminOnly()
    {
        var asAdmin = HeaderLinks(await _admin.GetStringAsync("/Tags"));
        asAdmin.IndexOf("/Dream").Should().Be(asAdmin.IndexOf("/Search") + 1, string.Join(" ", asAdmin));
        (await _admin.GetStringAsync("/Tags")).Should().Contain("<sl-icon name=\"moon-stars\"></sl-icon><span class=\"header-link-label\">Dream</span>");

        HeaderLinks(await _bob.GetStringAsync("/Tags")).Should().NotContain("/Dream").And.Contain("/Search");
    }

    [Fact]
    public async Task TheDreamPage_IsTheSuperadmins()
    {
        using var asAdmin = await _admin.GetAsync("/Dream");
        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await asAdmin.Content.ReadAsStringAsync();
        html.Should().Contain("id=\"page-content\"", "the single-page navigation swaps #page-content").And.Contain("moon-stars");

        using var asBob = await _bob.GetAsync("/Dream");
        asBob.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await asBob.Content.ReadAsStringAsync()).Should().NotContain("id=\"dream-page\"");

        using var visitor = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var anonymous = await visitor.GetAsync("/Dream");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Redirect);
        anonymous.Headers.Location!.OriginalString.Should().Contain("/Login");
    }

    [Fact]
    public async Task TheProxy_ReachesDream_ForTheSuperadmin_AndWantsTheTokenOnAChange()
    {
        using var status = await _admin.GetAsync("/api-proxy/dream/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean().Should().BeTrue();

        (await _bob.GetAsync("/api-proxy/dream/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var noToken = await _admin.PostAsJsonAsync("/api-proxy/dream/runs", new { mode = "normal" });
        noToken.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a change made by a script carries the page's RequestVerificationToken");
        (await noToken.Content.ReadAsStringAsync()).Should().Contain("could not be verified");

        (await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api-proxy/dream/settings"))).StatusCode
            .Should().Be(HttpStatusCode.MethodNotAllowed, "only GET, POST, PUT and DELETE are forwarded");
    }
}
