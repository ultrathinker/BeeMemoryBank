using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Admin -> Security -> Change master password, done by a signed-in web user, must move the user's
/// sign-in to the new password along with the key slot. It used to rotate the slot only: the new
/// password was refused at sign-in and the old one signed in without unlocking, so after the next
/// lock the node could not be opened from the web at all (BMB-31, scenario 18; bug B1 of the plan).
/// </summary>
public sealed class MasterPasswordChangeSignInTests : IAsyncLifetime
{
    private const string OldPassword = "AdminPass123";
    private const string NewPassword = "AdminPass456";

    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync(password: OldPassword);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = OldPassword }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task After_a_web_users_master_password_change_the_new_password_signs_in_and_unlocks()
    {
        using (var change = new HttpRequestMessage(HttpMethod.Post, "/api/keys/change-password")
        {
            Content = JsonContent.Create(new { oldPassword = OldPassword, newPassword = NewPassword })
        })
        {
            // What the web forwards for a signed-in superadmin.
            change.Headers.Add("X-User-Id", "1");
            change.Headers.Add("X-User-Role", "superadmin");
            (await _client.SendAsync(change)).EnsureSuccessStatusCode();
        }

        (await _client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        using var oldLogin = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "admin", password = OldPassword });
        oldLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old password must stop working");

        using var newLogin = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "admin", password = NewPassword });
        newLogin.StatusCode.Should().Be(HttpStatusCode.OK, "the new password is the user's password now");
        (await newLogin.Content.ReadFromJsonAsync<LoginBody>())!.IsUnlocked.Should().BeTrue(
            "signing in with it unlocks the vault, as before the change");
    }

    private sealed record LoginBody(bool IsUnlocked);
}
