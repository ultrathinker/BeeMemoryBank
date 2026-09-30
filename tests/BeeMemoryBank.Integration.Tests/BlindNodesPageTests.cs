using System.Net;
using System.Net.Http.Json;
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

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
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
}
