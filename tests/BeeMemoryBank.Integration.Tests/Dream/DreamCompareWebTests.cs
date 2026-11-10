using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package C1 through the real Web host in front of a real Api: the Dream page the superadmin is given carries the "Compare trial runs" section with every place its
/// script draws into, loads the section's files (versioned like every script, after the page's own script, which makes the <c>bmbDream</c> calls they use), and
/// serves them; the comparison's own call, <c>/api-proxy/dream/compare</c>, is a read the proxy lets the superadmin make and nobody else.
/// </summary>
public sealed class DreamCompareWebTests : IAsyncLifetime
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
    public async Task TheServedPage_CarriesTheSection_AndLoadsItsFilesAfterThePagesOwnScript()
    {
        var page = await (await _admin.GetAsync("/Dream")).Content.ReadAsStringAsync();

        foreach (var id in new[] { "dream-compare-section", "dream-cmp-picker", "dream-cmp-go", "dream-cmp-reveal", "dream-cmp-result", "dream-cmp-summary", "dream-cmp-rows" })
            page.Should().Contain($"id=\"{id}\"");
        page.Should().Contain("Compare trial runs").And.Contain("Show model names");
        page.Should().MatchRegex(@"/css/dream-compare\.css\?v=[\w-]+");
        page.Should().MatchRegex(@"/js/pages/dream-compare-model\.js\?v=[\w-]+");
        page.Should().MatchRegex(@"/js/pages/dream-compare\.js\?v=[\w-]+");
        page.IndexOf("/js/pages/dream.js", StringComparison.Ordinal).Should().BeLessThan(page.IndexOf("/js/pages/dream-compare-model.js", StringComparison.Ordinal));
        page.IndexOf("/js/pages/dream-compare-model.js", StringComparison.Ordinal).Should().BeLessThan(page.IndexOf("/js/pages/dream-compare.js", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/js/pages/dream-compare-model.js")]
    [InlineData("/js/pages/dream-compare.js")]
    [InlineData("/css/dream-compare.css")]
    public async Task TheFilesOfTheSectionAreServed(string path)
    {
        using var anonymous = _web.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheComparisonCall_GoesThroughTheProxyForTheSuperadmin_AndIsRefusedToAnOrdinaryUser()
    {
        // No such runs on this node: the Api's own answer (400 on a single run), which only a superadmin's request reaches.
        var asAdmin = await _admin.GetAsync($"/api-proxy/dream/compare?runs={Guid.NewGuid()}");
        var asBob = await _bob.GetAsync($"/api-proxy/dream/compare?runs={Guid.NewGuid()},{Guid.NewGuid()}");

        asAdmin.StatusCode.Should().Be(HttpStatusCode.BadRequest, await asAdmin.Content.ReadAsStringAsync());
        asBob.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
