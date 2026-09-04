using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>/api/keys/*</c> operates on the master key itself: /change-password re-wraps the master DEK,
/// /add-recovery mints a key that opens the entire vault. Both were gated only by "internal key +
/// unlocked session" — the Web layer happened to require superadmin on its own proxy route, which
/// is the wrong layer for the rule to live at, and left the API answering to any caller that could
/// present the internal key. The gate is now on the API group itself.
/// </summary>
public class KeyEndpointsAuthTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "keyEndpointsPassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddRecovery_FromANonSuperadmin_IsForbidden()
    {
        using var plain = UserClient();

        var resp = await plain.PostAsync("/api/keys/add-recovery", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddRecovery_WithNoRoleHeader_IsForbidden()
    {
        using var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Remove("X-User-Role");

        var resp = await anon.PostAsync("/api/keys/add-recovery", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangeMasterPassword_FromANonSuperadmin_IsForbidden()
    {
        using var plain = UserClient();

        var resp = await plain.PostAsJsonAsync("/api/keys/change-password",
            new { oldPassword = Password, newPassword = "someOtherPassword1" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddRecovery_AsSuperadmin_StillWorks()
    {
        var resp = await _client.PostAsync("/api/keys/add-recovery", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<RecoveryKeyResponseDto>();
        body!.RecoveryKey.Should().NotBeNullOrWhiteSpace();
    }

    private HttpClient UserClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Remove("X-User-Role");
        c.DefaultRequestHeaders.Add("X-User-Role", "user");
        return c;
    }

    private sealed record RecoveryKeyResponseDto(string RecoveryKey);
}
