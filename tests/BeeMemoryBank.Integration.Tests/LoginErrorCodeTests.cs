using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// POST /api/session/login refuses a non-superadmin while the vault is locked, and the Login page
/// renders a different screen for that refusal than for a wrong password — it tells the user to
/// ask an administrator to unlock rather than to try again.
///
/// <para>
/// ApiClient used to decide which of the two it was by testing the prose:
/// <c>error.Contains("locked")</c>. That made a human-readable sentence into a wire contract
/// across a process boundary — rewording the message, or translating it, would silently turn the
/// "ask an administrator" screen back into "wrong password". This is the same failure the typed
/// exceptions removed inside the API (see ExceptionStatusMap); the response now carries a
/// machine-readable <c>code</c> beside the prose, and these tests pin both halves: the code is
/// present and stable, and the sentence is free to change.
/// </para>
/// </summary>
public class LoginErrorCodeTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string AdminPassword = "AdminPass123";
    private const string UserPassword = "RegularUserPass1";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: AdminPassword);

        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<UserService>();
        await userService.CreateUserAsync("bob", "Bob", UserPassword, UserRoles.User);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private void LockVault()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SessionService>().Lock();
    }

    [Fact]
    public async Task Login_NonSuperadminWhileLocked_Returns403WithSessionLockedCode()
    {
        LockVault();

        var resp = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "bob", password = UserPassword });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("code", out var code).Should().BeTrue(
            "the Web layer branches on this, and without it it has nothing to branch on but prose");
        code.GetString().Should().Be("session_locked");

        // The prose is deliberately NOT asserted word for word — the point of the code is that
        // this sentence can be reworded or translated without moving anything.
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401WithNoCode()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "bob", password = "definitelyNotTheRightOne1" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // No code: nothing branches on a wrong password beyond the status, and a code nobody
        // reads is a contract nobody maintains. The set stays small on purpose.
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("code", out var code))
            code.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
