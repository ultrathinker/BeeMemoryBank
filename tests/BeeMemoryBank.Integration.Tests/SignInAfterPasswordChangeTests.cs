using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Changing a password rotates the user's security stamp, and the web caches stamps for five
/// minutes. Signing in with the new password straight after the change used to be rejected on the
/// first page because the cookie carried the new stamp while the cache still held the old one: the
/// user was bounced back to /Login with no error until the cache expired (BMB-31, scenario 17).
/// </summary>
public sealed class SignInAfterPasswordChangeTests : IAsyncLifetime
{
    private const string OldPassword = "AdminPass123";
    private const string NewPassword = "AdminPass456";

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _apiClient = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(password: OldPassword);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = OldPassword }))
            .EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
    }

    public Task DisposeAsync()
    {
        _apiClient.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Fresh_sign_in_with_the_new_password_is_not_bounced_back_to_login()
    {
        // A signed-in page view puts the current (old) stamp into the web's cache.
        using (var before = await WebLogin.LoginAsync(_web, "admin", OldPassword))
        using (var tree = await before.GetAsync("/Tree"))
            tree.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var change = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password")
        {
            Content = JsonContent.Create(new { oldPassword = OldPassword, newPassword = NewPassword })
        })
        {
            change.Headers.Add("X-User-Id", "1");
            change.Headers.Add("X-User-Role", "superadmin");
            (await _apiClient.SendAsync(change)).EnsureSuccessStatusCode();
        }

        using var after = await WebLogin.LoginAsync(_web, "admin", NewPassword);
        using var page = await after.GetAsync("/Tree");
        page.StatusCode.Should().Be(HttpStatusCode.OK,
            "the new sign-in carries the new stamp; a stale cached stamp must not reject it");
    }
}
