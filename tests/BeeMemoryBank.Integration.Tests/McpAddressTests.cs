extern alias WebApp;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebApp::BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The MCP address the Profile page prints in the snippet for an AI assistant. /mcp lives on the API port,
/// not on the page, so in plain Docker the address the user browses from is a dead one; the resolver
/// says "cannot reach yet" there instead, and prints a real address wherever one exists.
/// </summary>
public class McpAddressTests
{
    private const string Browse = "https://bee.example.com";

    private static McpAddress Resolve(
        string? configured = null, bool front = false, bool proxy = false, string? port = null, string browse = "http://localhost:5301") =>
        McpAddressResolver.Resolve(new McpAddressInputs(configured, front, proxy, port, browse));

    [Fact]
    public void PlainDocker_NothingKnown_SaysAssistantsCannotReachTheNode_AndPrintsNoUrl()
    {
        // The default docker-compose.yml: browsed at localhost:5301, API port not published.
        var address = Resolve();

        address.IsAvailable.Should().BeFalse();
        address.Url.Should().BeNull();
        address.Source.Should().Be(McpAddressSource.None);
        address.Message.Should().Be(McpAddressResolver.UnavailableMessage)
            .And.Contain("cannot reach").And.Contain("docker-compose.reverse-proxy.yml").And.Contain("docs/deployment.md");
    }

    [Theory]
    [InlineData("https://bee.example.com", "https://bee.example.com/mcp")]
    [InlineData("https://bee.example.com/", "https://bee.example.com/mcp")]
    [InlineData("https://bee.example.com/mcp", "https://bee.example.com/mcp")]
    [InlineData("https://bee.example.com/mcp/", "https://bee.example.com/mcp")]
    [InlineData("  https://bee.example.com/mcp  ", "https://bee.example.com/mcp")]
    [InlineData("http://127.0.0.1:5300/mcp", "http://127.0.0.1:5300/mcp")]
    [InlineData("http://127.0.0.1:5300", "http://127.0.0.1:5300/mcp")]
    [InlineData("https://bee.example.com/bmb/mcp", "https://bee.example.com/bmb/mcp")]
    [InlineData("HTTPS://BEE.example.com:8443/mcp", "https://bee.example.com:8443/mcp")]
    public void ConfiguredUrl_IsUsedAsGiven_WithMcpAppendedToABareOrigin(string configured, string expected)
    {
        var address = Resolve(configured);

        address.Url.Should().Be(expected);
        address.Source.Should().Be(McpAddressSource.Configured);
        address.Message.Should().BeNull();
    }

    [Theory]
    [InlineData("bee.example.com")]
    [InlineData("ftp://bee.example.com/mcp")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@bee.example.com/mcp")]
    [InlineData("https://bee.example.com/mcp?token=1")]
    [InlineData("https://bee.example.com/mcp#frag")]
    [InlineData("https://bee.example.com/a b")]
    [InlineData("https://bee.example.com/\"")]
    [InlineData("https://bee.example.com/'x")]
    [InlineData("https://bee.example.com/$(id)")]
    [InlineData("https://bee.example.com/`id`")]
    [InlineData("https://bee.example.com/mcp\" --header \"X: y")]
    public void ConfiguredUrl_ThatIsNotUsable_IsRefusedWithAClearMessage_AndNothingElseIsGuessed(string configured)
    {
        // Even with every other source available: a value the operator typed wrongly is theirs to fix.
        var address = Resolve(configured, front: true, proxy: true, port: "5300", browse: Browse);

        address.Url.Should().BeNull();
        address.Source.Should().Be(McpAddressSource.None);
        address.Message.Should().Be(McpAddressResolver.InvalidConfiguredMessage);
        address.Message.Should().NotContain("secret", "the bad value is never echoed back into the page");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfiguredUrl_IsTheSameAsNoneAtAll(string? configured)
    {
        // docker-compose passes BMB_MCP_URL=${BMB_MCP_URL:-}, which is an empty string when .env has no value.
        Resolve(configured, port: "5300").Source.Should().Be(McpAddressSource.PublishedLoopbackPort);
        Resolve(configured).Source.Should().Be(McpAddressSource.None);
    }

    [Fact]
    public void TheConfiguredUrl_BeatsEveryOtherSource()
    {
        var address = Resolve("https://fixed.example.com/mcp", front: true, proxy: true, port: "5300", browse: Browse);

        address.Url.Should().Be("https://fixed.example.com/mcp");
        address.Source.Should().Be(McpAddressSource.Configured);
    }

    [Fact]
    public void BehindTheNodeFront_TheBrowseAddressIsRight_BecauseTheFrontServesMcpThere()
    {
        // Desktop app, Windows service, Mac app: 127.0.0.1:5310 serves the page and /mcp together.
        var address = Resolve(front: true, port: "5300", proxy: true, browse: "http://127.0.0.1:5310");

        address.Url.Should().Be("http://127.0.0.1:5310/mcp");
        address.Source.Should().Be(McpAddressSource.NodeFront);
    }

    [Fact]
    public void ThroughAReverseProxy_TheBrowseAddressIsUsed_BeforeTheLoopbackPort()
    {
        // The recipes in docs/internet-access.md forward /mcp to the API, so the public name works for a remote agent.
        var address = Resolve(proxy: true, port: "5300", browse: Browse);

        address.Url.Should().Be("https://bee.example.com/mcp");
        address.Source.Should().Be(McpAddressSource.ReverseProxy);
    }

    [Theory]
    [InlineData("5300", "http://127.0.0.1:5300/mcp")]
    [InlineData(" 5004 ", "http://127.0.0.1:5004/mcp")]
    [InlineData("1", "http://127.0.0.1:1/mcp")]
    [InlineData("65535", "http://127.0.0.1:65535/mcp")]
    public void APublishedApiPort_GivesTheLoopbackAddress(string port, string expected)
    {
        var address = Resolve(port: port);

        address.Url.Should().Be(expected);
        address.Source.Should().Be(McpAddressSource.PublishedLoopbackPort);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("5300/mcp")]
    [InlineData("5300; rm")]
    [InlineData("")]
    public void ANonsensePort_IsIgnored(string port)
    {
        var address = Resolve(port: port);

        address.Url.Should().BeNull();
        address.Source.Should().Be(McpAddressSource.None);
    }

    [Fact]
    public void ThePlainDockerBrowseAddress_IsNeverPrintedAsTheMcpAddress()
    {
        // The bug this replaces: localhost:5301 + /mcp, an address that answers 404 because /mcp is on 5300.
        foreach (var address in new[] { Resolve(), Resolve(port: "bad"), Resolve(browse: "http://192.168.1.20:5301") })
            (address.Url ?? "").Should().NotContain("5301");
    }

    [Fact]
    public void ABrowseOriginThatIsNotAnHttpAddress_IsNotUsed()
    {
        Resolve(front: true, browse: "not a url").Source.Should().Be(McpAddressSource.None);
        Resolve(proxy: true, browse: "ftp://x").Source.Should().Be(McpAddressSource.None);
        Resolve(front: true, browse: "https://bee.example.com\"x").Source.Should().Be(McpAddressSource.None);
    }
}

/// <summary>
/// The same decisions through the real Web host: what the Profile page hands to its script
/// (<c>profile-page-data</c>) for each way a node can be run, and what a signed-in user's request looks like.
/// </summary>
public sealed class ProfileMcpAddressPageTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";

    private static readonly Regex PageData = new(
        "<script type=\"application/json\" id=\"profile-page-data\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonElement> ProfileDataAsync(Action<HttpRequestMessage>? shape = null)
    {
        using var client = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Profile");
        shape?.Invoke(request);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = PageData.Match(html);
        match.Success.Should().BeTrue("the Profile page hands its MCP address to profile.js as JSON");
        return JsonDocument.Parse(match.Groups[1].Value).RootElement.Clone();
    }

    [Fact]
    public async Task PlainDocker_ThePageSaysAssistantsCannotReachTheNode_InsteadOfADeadUrl()
    {
        var data = await ProfileDataAsync();

        data.GetProperty("mcpUrl").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("mcpUnavailableMessage").GetString().Should().Be(McpAddressResolver.UnavailableMessage);
    }

    [Fact]
    public async Task APublishedApiPort_GivesTheLoopbackMcpAddress()
    {
        _web.WithSetting("BMB_API_PUBLISHED_PORT", "5300");

        var data = await ProfileDataAsync();

        data.GetProperty("mcpUrl").GetString().Should().Be("http://127.0.0.1:5300/mcp");
        data.GetProperty("mcpUnavailableMessage").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AConfiguredMcpUrl_IsWhatThePagePrints()
    {
        _web.WithSetting("BMB_MCP_URL", "https://bee.example.com");
        _web.WithSetting("BMB_API_PUBLISHED_PORT", "5300");

        var data = await ProfileDataAsync();

        data.GetProperty("mcpUrl").GetString().Should().Be("https://bee.example.com/mcp");
    }

    [Fact]
    public async Task BehindTheNodeFront_ThePagePrintsTheAddressTheUserBrowsesFrom()
    {
        _web.WithSetting("BMB_BEHIND_LOOPBACK_PROXY", "1");

        var data = await ProfileDataAsync();

        data.GetProperty("mcpUrl").GetString().Should().Be("http://localhost/mcp", "the test host is http://localhost");
    }

    [Fact]
    public async Task ThroughAReverseProxy_ThePagePrintsThePublicAddress()
    {
        var data = await ProfileDataAsync(r =>
        {
            r.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            r.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
        });

        data.GetProperty("mcpUrl").GetString().Should().Be("https://localhost/mcp");
    }
}
