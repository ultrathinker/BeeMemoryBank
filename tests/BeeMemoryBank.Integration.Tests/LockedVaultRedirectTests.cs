using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A signed-in browser whose node has been locked underneath it (an app update or a reboot
/// restarts the API locked, while the web cookie stays valid) must land on /Login at the first
/// page navigation, not see the tree and get logged out on the first click.
/// </summary>
public sealed class LockedVaultRedirectTests : IAsyncLifetime
{
    private const string Password = "AdminPass123";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;
    private HttpClient _browser = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();

        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _browser = await WebLogin.LoginAsync(_web, "admin", Password);
    }

    public Task DisposeAsync()
    {
        _browser.Dispose();
        _apiClient.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PageRequest_OnALockedVault_RedirectsToLoginBeforeRendering()
    {
        using (var before = await _browser.GetAsync("/Tree"))
            before.StatusCode.Should().Be(HttpStatusCode.OK, "the vault is unlocked and the cookie is valid");

        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        using var after = await _browser.GetAsync("/Tree?path=%2Fnotes");
        after.StatusCode.Should().Be(HttpStatusCode.Redirect);
        after.Headers.Location!.OriginalString.Should().Be("/Login?returnUrl=%2FTree%3Fpath%3D%252Fnotes");
    }

    [Fact]
    public async Task ProxyAndLoginRequests_OnALockedVault_AreNotRedirected()
    {
        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        using var login = await _browser.GetAsync("/Login");
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var proxy = await _browser.GetAsync("/api-proxy/sync/delivery-status");
        proxy.StatusCode.Should().NotBe(HttpStatusCode.Redirect, "XHR calls are not page navigations");
    }
}
