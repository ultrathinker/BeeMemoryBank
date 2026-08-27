using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// End-to-end cover for promoting an existing user to superadmin over HTTP. The promoting
/// admin does not know the target's password, so the vault key slot cannot be built at
/// promotion time — the login endpoint provisions it the next time that user signs in.
/// </summary>
public class SuperadminPromotionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string AdminPassword = "AdminPass123";
    private const string BobPassword = "BobPass123";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: AdminPassword);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> CreateRegularUserAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/users", new
        {
            username = "bob",
            password = BobPassword,
            displayName = "Bob",
            role = "user"
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int?> GetKeySlotIdAsync(int userId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        return (await repo.GetByIdAsync(userId))!.KeySlotId;
    }

    [Fact]
    public async Task Promote_WithoutPassword_Succeeds()
    {
        var bobId = await CreateRegularUserAsync();

        var resp = await _client.PutAsJsonAsync($"/api/users/{bobId}", new
        {
            displayName = "Bob",
            role = "superadmin"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the promoting admin has no way to supply the target's password");
        (await GetKeySlotIdAsync(bobId)).Should().BeNull("the slot waits for Bob's next login");
    }

    [Fact]
    public async Task PromotedUser_GetsKeySlotOnNextLogin()
    {
        var bobId = await CreateRegularUserAsync();
        (await _client.PutAsJsonAsync($"/api/users/{bobId}", new { displayName = "Bob", role = "superadmin" }))
            .EnsureSuccessStatusCode();

        var login = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "bob", password = BobPassword });

        login.EnsureSuccessStatusCode();
        (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString()
            .Should().Be("superadmin");
        (await GetKeySlotIdAsync(bobId)).Should().NotBeNull("login is the first moment the plaintext password is available");
    }

    [Fact]
    public async Task PromotedUser_KeepsTheirOwnPassword()
    {
        var bobId = await CreateRegularUserAsync();
        (await _client.PutAsJsonAsync($"/api/users/{bobId}", new { displayName = "Bob", role = "superadmin" }))
            .EnsureSuccessStatusCode();

        var login = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "bob", password = BobPassword });

        login.StatusCode.Should().Be(HttpStatusCode.OK, "promotion must not silently reset the password");
    }

    [Fact]
    public async Task RejectedRoleChange_Returns409_NotServerError()
    {
        // The bootstrap admin is the only superadmin, so demoting them is refused.
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var adminId = (await repo.GetByUsernameAsync("admin"))!.Id;

        var resp = await _client.PutAsJsonAsync($"/api/users/{adminId}", new
        {
            displayName = "admin",
            role = "user"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("last superadmin");
    }
}
