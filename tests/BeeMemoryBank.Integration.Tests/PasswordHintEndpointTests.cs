using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-336: the password hint at every HTTP entry point (mandatory where a person creates or replaces a password),
/// and the anonymous lookup the sign-in page uses. Users are node-local plaintext rows, so the lookup answers while
/// the vault is LOCKED - the only moment it is worth anything.
/// </summary>
public sealed class PasswordHintEndpointTests : IAsyncLifetime
{
    private const string Password = "integrationPassword";
    private const string Hint = "the blue folder by the window";
    private const string NoHintMessage = "No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.";

    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        await users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User, passwordHint: Hint);
        await users.CreateUserAsync("carol", "Carol", "CarolPass1x", UserRoles.User);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static HttpRequestMessage HintRequest(string username, string clientIp) =>
        new(HttpMethod.Post, "/api/session/password-hint")
        {
            Content = JsonContent.Create(new { username, clientIp })
        };

    // ── the anonymous lookup ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHintIsReadableWhileTheSessionIsLocked_OnAPlainNode()
    {
        // The fixture is a plain node (its database is always readable). The case this feature exists for - a SEALED node
        // that is closed after a restart - is PasswordHintSealedTests; the name here no longer promises more than it proves.
        _factory.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse("the fixture node was never unlocked");

        using var response = await _client.SendAsync(HintRequest("bob", "10.10.0.1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PasswordHintResponse>();
        body!.Hint.Should().Be(Hint);
        body.Message.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownUserAndAUserWithoutAHint_GetTheSameAnswer()
    {
        using var unknown = await _client.SendAsync(HintRequest("nobody-here", "10.10.0.2"));
        using var withoutHint = await _client.SendAsync(HintRequest("carol", "10.10.0.3"));

        unknown.StatusCode.Should().Be(withoutHint.StatusCode).And.Be(HttpStatusCode.OK);
        var unknownJson = await unknown.Content.ReadAsStringAsync();
        var withoutJson = await withoutHint.Content.ReadAsStringAsync();
        unknownJson.Should().Be(withoutJson, "no silence and no shape may tell the two apart");
        unknownJson.Should().Contain(NoHintMessage);
        using var document = JsonDocument.Parse(unknownJson);
        document.RootElement.GetProperty("hint").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ATypedHintIsReturnedAsText_NotEncodedOrExecutedHere()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateUserAsync("dave", "Dave", "DavePass1x", UserRoles.User,
                passwordHint: "<script>alert(1)</script> looks like the blue folder");
        }

        using var response = await _client.SendAsync(HintRequest("dave", "10.10.0.4"));
        var body = await response.Content.ReadFromJsonAsync<PasswordHintResponse>();

        body!.Hint.Should().Be("<script>alert(1)</script> looks like the blue folder",
            "the API returns data; the page that renders it is what encodes it");
    }

    [Fact]
    public async Task LookupsForOneNameFromManyAddresses_AreAllAnswered()
    {
        for (var i = 0; i < PasswordHintState.MaxAttempts; i++)
            (await _client.SendAsync(HintRequest("sweep-name", $"10.11.0.{i + 1}"))).StatusCode.Should().Be(HttpStatusCode.OK);

        // An eleventh address is a fresh bucket, and the name has no bucket of its own: still answered. A per-username
        // counter would refuse this one and let one stranger lock the name out for everybody.
        using var another = await _client.SendAsync(HintRequest("sweep-name", "10.11.0.11"));
        another.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheEleventhLookupFromOneAddress_IsRefused_WhateverTheName()
    {
        const string ip = "10.12.0.1";
        for (var i = 0; i < PasswordHintState.MaxAttempts; i++)
            (await _client.SendAsync(HintRequest($"10.12-name-{i}", ip))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var refused = await _client.SendAsync(HintRequest("bob", ip));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the refusal keys on the address, not on the name it asked about");
    }

    // ── every password-creating entry point refuses a missing hint ─────────────────────────

    [Fact]
    public async Task TheStandaloneSetup_RefusesAMissingHint_AndAcceptsAGoodOne()
    {
        using var fresh = new BmbWebApplicationFactory();
        using var client = fresh.CreateClient();

        using var refused = await client.PostAsJsonAsync("/api/init/standalone",
            new { adminUsername = "admin", displayName = "Fresh", password = "FreshSetupPw1" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("password hint is required");

        using var created = await client.PostAsJsonAsync("/api/init/standalone",
            new { adminUsername = "admin", displayName = "Fresh", password = "FreshSetupPw1", passwordHint = "the fresh hint" });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheJoin_RefusesAMissingHint_BeforeItContactsAnybody()
    {
        using var fresh = new BmbWebApplicationFactory();
        using var client = fresh.CreateClient();

        using var refused = await client.PostAsJsonAsync("/api/init/join",
            new { adminUsername = "admin", displayName = "Joiner", remoteUrl = "http://127.0.0.1:1", password = "JoinSetupPw1" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("password hint is required");
    }

    [Fact]
    public async Task CreatingAUser_RefusesAMissingHint_AndAcceptsAGoodOne()
    {
        using var refused = await _client.PostAsJsonAsync("/api/users",
            new { username = "erin", displayName = "Erin", password = "ErinPass1x", role = "user" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var created = await _client.PostAsJsonAsync("/api/users",
            new { username = "erin", displayName = "Erin", password = "ErinPass1x", role = "user", passwordHint = "the first day of school" });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminPasswordReset_RefusesAMissingHint()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var carol = await users.GetByUsernameAsync("carol");

        using var refused = await _client.PostAsJsonAsync($"/api/users/{carol!.Id}/change-password",
            new { newPassword = "CarolPass2x" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var done = await _client.PostAsJsonAsync($"/api/users/{carol.Id}/change-password",
            new { newPassword = "CarolPass2x", passwordHint = "the house with the red door" });
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SelfServicePasswordChange_RefusesAMissingHint()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var bob = await users.GetByUsernameAsync("bob");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password")
        {
            Content = JsonContent.Create(new { oldPassword = "BobPass1x", newPassword = "BobPass2x" })
        };
        request.Headers.Add("X-User-Id", bob!.Id.ToString());
        using var refused = await _client.SendAsync(request);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var okRequest = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password")
        {
            Content = JsonContent.Create(new { oldPassword = "BobPass1x", newPassword = "BobPass2x", passwordHint = "the road by the lake" })
        };
        okRequest.Headers.Add("X-User-Id", bob.Id.ToString());
        using var done = await _client.SendAsync(okRequest);
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        (await users.GetPasswordHintAsync("bob")).Should().Be("the road by the lake");
    }

    [Fact]
    public async Task TheRecoveryReset_RefusesAMissingHint_AndStoresTheGoodOne()
    {
        string key;
        using (var scope = _factory.Services.CreateScope())
        {
            await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
            key = await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
        }

        using var refused = await _client.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = key, newPassword = "AdminPass2x" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("password hint is required");

        using var done = await _client.PostAsJsonAsync("/api/session/recover-access",
            new { username = "admin", recoveryKey = key, newPassword = "AdminPass2x", passwordHint = "the sign-in note" });
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());

        using var scope2 = _factory.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<IUserRepository>().GetPasswordHintAsync("admin"))
            .Should().Be("the sign-in note");
    }

    [Fact]
    public async Task TheMasterPasswordEndpoint_RefusesAMissingHint()
    {
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);

        using var refused = await _client.PostAsJsonAsync("/api/keys/change-password",
            new { oldPassword = Password, newPassword = "AdminPass2x" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var done = await _client.PostAsJsonAsync("/api/keys/change-password",
            new { oldPassword = Password, newPassword = "AdminPass2x", passwordHint = "the sign-in note" });
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheSoftPrompt_ReadsAndWritesTheCallersOwnHint()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var carol = await users.GetByUsernameAsync("carol");

        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/password-hint");
        read.Headers.Add("X-User-Id", carol!.Id.ToString());
        using var before = await _client.SendAsync(read);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        (await before.Content.ReadFromJsonAsync<CurrentPasswordHintResponse>())!.PasswordHint.Should().BeNull();

        using var write = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/password-hint")
        {
            Content = JsonContent.Create(new { passwordHint = "the house with the red door" })
        };
        write.Headers.Add("X-User-Id", carol.Id.ToString());
        (await _client.SendAsync(write)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var readAgain = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/password-hint");
        readAgain.Headers.Add("X-User-Id", carol.Id.ToString());
        using var after = await _client.SendAsync(readAgain);
        (await after.Content.ReadFromJsonAsync<CurrentPasswordHintResponse>())!.PasswordHint.Should().Be("the house with the red door");
    }
}