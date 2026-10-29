using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>/Admin/OsAutoUnlock</c> by what the node says about itself. On a server the page does not switch anything: it says where "Start unlocked after a restart" is, and that
/// answer must not depend on whether the operating system has a secret store. A Linux server has none (the status says <c>supported: false</c>), and it is the server that
/// most needs the sentence; a desktop without a secret store keeps the generic card. The two answers of the node are faked; the page is the real one.
/// </summary>
public sealed class OsAutoUnlockPageTests : IAsyncLifetime
{
    private const string Password = "OsAutoUnlockPage123";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private readonly NodeSays _node = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "OsNode", password: Password);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        _node.SetInner(_api.Server.CreateHandler());
        _web.RouteOutboundHttpThrough(_node);
        _admin = await WebLogin.LoginAsync(_web, "admin", Password);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The real node, except for what it says about its role and its secret store.</summary>
    private sealed class NodeSays : DelegatingHandler
    {
        public string Role { get; set; } = "desktop";
        public bool Supported { get; set; }
        public bool Enabled { get; set; }

        public void SetInner(HttpMessageHandler inner) => InnerHandler = inner;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                var json = request.RequestUri!.AbsolutePath switch
                {
                    "/api/keys/auto-unlock/status" => $$"""{"enabled":{{Enabled.ToString().ToLowerInvariant()}},"supported":{{Supported.ToString().ToLowerInvariant()}}}""",
                    "/api/vault/status" => $$$"""{"mode":"plain","state":"Open","auto_unlock":{"role":"{{{Role}}}","os":{{{Supported.ToString().ToLowerInvariant()}}},"server":false}}""",
                    _ => null,
                };
                if (json is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return base.SendAsync(request, ct);
        }
    }

    private async Task<string> PageAsync(string role, bool supported, bool enabled = false)
    {
        (_node.Role, _node.Supported, _node.Enabled) = (role, supported, enabled);
        using var response = await _admin.GetAsync("/Admin/OsAutoUnlock");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        return html[html.IndexOf("id=\"admin-page-body\"", StringComparison.Ordinal)..];
    }

    [Theory]
    [InlineData(false)]   // a Linux server: no secret store, so the status says unsupported
    [InlineData(true)]    // a server on Windows or macOS
    public async Task AServer_SaysWhereTheSwitchIs_WhetherOrNotTheSystemHasASecretStore(bool supported)
    {
        var body = await PageAsync("server", supported);

        body.Should().Contain("id=\"os-auto-unlock-server\"").And.Contain("This node is a server: it has no switch here.").And.Contain("Start unlocked after a restart");
        body.Should().Contain("href=\"/Admin/StorageProtection\"");
        body.Should().NotContain("os-auto-unlock-unsupported").And.NotContain("Enable auto-unlock");
    }

    [Fact]
    public async Task ADesktopWithoutASecretStore_KeepsTheGenericCard()
    {
        var body = await PageAsync("desktop", supported: false);

        body.Should().Contain("id=\"os-auto-unlock-unsupported\"").And.Contain("has no secret store");
        body.Should().NotContain("os-auto-unlock-server").And.NotContain("Start unlocked after a restart");
    }

    [Fact]
    public async Task ADesktopWithASecretStore_OffersTheSwitch()
    {
        var body = await PageAsync("desktop", supported: true);

        body.Should().Contain("Enable auto-unlock").And.Contain("handler=EnableAutoUnlock");
        body.Should().NotContain("os-auto-unlock-server").And.NotContain("os-auto-unlock-unsupported");
    }

    [Fact]
    public async Task AServerThatAlreadyHasASlot_ShowsItSoThatItCanBeTurnedOff()
    {
        // A slot made earlier (a node that was a desktop, or set up by an older version) stays visible on a server: the page lets it be turned off.
        var body = await PageAsync("server", supported: true, enabled: true);

        body.Should().Contain("Disable auto-unlock").And.Contain("handler=DisableAutoUnlock");
        body.Should().NotContain("os-auto-unlock-server");
    }

    [Fact]
    public async Task ADesktopWithASecretStore_SaysTheDangerOnce_AndTheExplanationLinksToIt()
    {
        (_node.Role, _node.Supported, _node.Enabled) = ("desktop", true, false);
        using var response = await _admin.GetAsync("/Admin/OsAutoUnlock");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        System.Text.RegularExpressions.Regex.Matches(html, "Anyone with access to this").Should().HaveCount(1, "one warning, in the card of the switch");
        html.Should().NotContain("admin-warning").And.Contain("id=\"os-auto-unlock-risk\"").And.Contain("href=\"#os-auto-unlock-risk\"");
        System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ").Should().Contain("a stolen disk is only as safe as that account", "what the old explanation said about the disk is in the one warning");
        html.Should().Contain("Turning it off takes effect at the next start");
        System.Text.RegularExpressions.Regex.Match(html, "data-confirm=\"Enable OS auto-unlock[^\"]*\"").Value.Should().NotContain("Anyone with access", "the confirmation does not repeat the warning a third time");
    }

    [Fact]
    public async Task AServer_HasNoWarningToLinkTo()
    {
        (_node.Role, _node.Supported, _node.Enabled) = ("server", false, false);
        using var response = await _admin.GetAsync("/Admin/OsAutoUnlock");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        html.Should().NotContain("os-auto-unlock-risk", "the page offers no switch here, so there is no warning and no link to it");
    }
}
