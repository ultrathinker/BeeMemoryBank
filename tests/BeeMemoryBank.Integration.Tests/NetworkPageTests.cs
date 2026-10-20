using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-322: the "Network" page, through the real Web host in front of a real Api. The header's "Sync Status" title leads to it; it is the superadmin's like the
/// sync status it extends; it is made of the shared script and the model (versioned like every script, see StaticAssetCachingTests); and the proxy serves it the
/// map of the node.
/// </summary>
public sealed class NetworkPageTests : IAsyncLifetime
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
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user" })).EnsureSuccessStatusCode();
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
    public async Task TheSyncStatusTitleInTheHeader_IsALinkToTheNetworkPage_ForASuperadmin()
    {
        var page = await (await _admin.GetAsync("/Admin")).Content.ReadAsStringAsync();

        page.Should().Contain("class=\"sync-dropdown-header\" href=\"/Network\"", "the title of the small sync popup opens the network map");
        page.Should().Contain("Network map");
    }

    [Fact]
    public async Task ARegularUser_NeverSeesTheLink_AndIsNotLetIn()
    {
        var home = await (await _bob.GetAsync("/Search")).Content.ReadAsStringAsync();
        var network = await _bob.GetAsync("/Network");

        home.Should().NotContain("href=\"/Network\"", "the sync status is the superadmin's");
        network.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheNetworkPage_HasTheStageTheScriptDrawsOn_AndLoadsItsScriptsWithTheirVersion()
    {
        var response = await _admin.GetAsync("/Network");
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Should().Contain("id=\"net-stage\"");
        page.Should().Contain("id=\"net-links-body\"");
        page.Should().MatchRegex("/js/pages/network-model\\.js\\?v=[\\w-]+");
        page.Should().MatchRegex("/js/pages/network-map\\.js\\?v=[\\w-]+");
        page.Should().MatchRegex("/css/network\\.css\\?v=[\\w-]+");
    }

    [Theory]
    [InlineData("/js/pages/network-model.js")]
    [InlineData("/js/pages/network-map.js")]
    [InlineData("/css/network.css")]
    public async Task TheFilesOfThePageAreServed(string path)
    {
        using var anonymous = _web.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheProxyGivesASuperadminTheMapOfThisNode_AndARegularUserNothing()
    {
        var asAdmin = await _admin.GetAsync("/api-proxy/sync/network");
        var asBob = await _bob.GetAsync("/api-proxy/sync/network");

        asAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
        var map = await asAdmin.Content.ReadFromJsonAsync<JsonElement>();
        map.GetProperty("self").GetProperty("isSelf").GetBoolean().Should().BeTrue();
        map.GetProperty("peers").ValueKind.Should().Be(JsonValueKind.Array);
        map.GetProperty("links").ValueKind.Should().Be(JsonValueKind.Array);
        asBob.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
