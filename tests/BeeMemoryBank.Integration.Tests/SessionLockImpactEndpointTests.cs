using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// GET /api/session/lock-impact reports what can undo a Lock on this node, so the Admin UI can
/// state the consequence before the click instead of leaving the operator to discover it.
///
/// Lock is advisory (SECURITY.md, "Trust Model"): it wipes the master DEK, but an agent key owned
/// by a superadmin carries its own wrapped copy and AgentAuthMiddleware re-unlocks the whole
/// process on that key's next request. An ordinary user's agent carries no key material and
/// cannot; an agent whose owner has been deactivated is refused with 401 before the unlock is
/// attempted. All three shapes are asserted here, because the count the UI prints is only useful
/// if it matches exactly the set of keys that can actually reopen the vault.
/// </summary>
public class SessionLockImpactEndpointTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "lockImpactPassword";

    private int _adminUserId;
    private int _plainUserId;
    private int _retiredUserId;

    public async Task InitializeAsync()
    {
        using var client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await client.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        _adminUserId = (await userRepo.GetByUsernameAsync("admin"))!.Id;
        _plainUserId = await userRepo.CreateAsync(NewUser("colleague", "Colleague", UserRoles.User));
        _retiredUserId = await userRepo.CreateAsync(NewUser("retired", "Retired Admin", UserRoles.Superadmin));
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static User NewUser(string username, string displayName, string role) => new()
    {
        Username = username,
        DisplayName = displayName,
        Role = role,
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Creates an agent row directly. <paramref name="canAutoUnlock"/> decides whether it carries
    /// wrapped key material — the single difference between a superadmin's agent and everyone
    /// else's (see Agent.EncryptedDek). The blob does not have to be a real wrapped DEK for this
    /// endpoint: it reports what CanAutoUnlock says, which is what the middleware branches on.
    /// </summary>
    private async Task<int> CreateAgentAsync(string name, int ownerUserId, bool canAutoUnlock, string? apiKey = null)
    {
        using var scope = _factory.Services.CreateScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        return await agentRepo.CreateAsync(new Agent
        {
            Name = name,
            KeyPrefix = apiKey != null ? AgentKeyHelper.GetKeyPrefix(apiKey) : "bee_" + name,
            KeyHash = apiKey != null ? AgentKeyHelper.ComputeKeyHash(apiKey) : "hash-" + name,
            EncryptedDek = canAutoUnlock ? [1, 2, 3] : null,
            DekIV = canAutoUnlock ? [4, 5, 6] : null,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = ownerUserId
        });
    }

    private HttpClient ClientAs(int userId, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private static async Task<JsonElement> ReadImpactAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string Name, string? Owner)> AgentsOf(JsonElement impact) =>
        impact.GetProperty("agents").EnumerateArray()
            .Select(a => (
                a.GetProperty("name").GetString()!,
                a.GetProperty("ownerName").GetString()))
            .ToList();

    [Fact]
    public async Task AgentThatCanAutoUnlock_IsListedWithItsNameAndOwner()
    {
        await CreateAgentAsync("claude-desktop", _adminUserId, canAutoUnlock: true);
        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);

        var impact = await ReadImpactAsync(await client.GetAsync("/api/session/lock-impact"));

        AgentsOf(impact).Should().ContainSingle()
            .Which.Should().Be(("claude-desktop", "admin"));
    }

    [Fact]
    public async Task AgentThatCannotAutoUnlock_IsNotListed()
    {
        await CreateAgentAsync("colleagues-agent", _plainUserId, canAutoUnlock: false);
        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);

        var impact = await ReadImpactAsync(await client.GetAsync("/api/session/lock-impact"));

        AgentsOf(impact).Should().BeEmpty(
            "an ordinary user's agent carries no wrapped DEK and cannot reopen a locked vault");
    }

    [Fact]
    public async Task AgentWhoseOwnerIsDeactivated_IsNotListed()
    {
        // The key still holds a wrapped DEK, but AgentAuthMiddleware rejects the request with 401
        // before the unlock is attempted, so it can no longer undo a Lock. Counting it would
        // overstate the number of keys an operator has to revoke.
        await CreateAgentAsync("retired-agent", _retiredUserId, canAutoUnlock: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var retired = (await userRepo.GetByIdAsync(_retiredUserId))!;
            retired.IsActive = false;
            await userRepo.UpdateAsync(retired);
        }

        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);
        var impact = await ReadImpactAsync(await client.GetAsync("/api/session/lock-impact"));

        AgentsOf(impact).Should().BeEmpty();
    }

    [Fact]
    public async Task NoAutoUnlockingAgentsAndNoOsAutoUnlock_ReportsAnEmptyList()
    {
        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);

        var impact = await ReadImpactAsync(await client.GetAsync("/api/session/lock-impact"));

        AgentsOf(impact).Should().BeEmpty();
        impact.GetProperty("osAutoUnlockEnabled").GetBoolean().Should().BeFalse(
            "the fixture never enabled the os_auto_unlock slot");
    }

    [Fact]
    public async Task NonSuperadmin_IsRefused()
    {
        await CreateAgentAsync("claude-desktop", _adminUserId, canAutoUnlock: true);
        using var client = ClientAs(_plainUserId, UserRoles.User);

        var resp = await client.GetAsync("/api/session/lock-impact");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AgentBearerCaller_IsRefused_EvenWhenItsOwnerIsASuperadmin()
    {
        // A superadmin's agent inherits IsSuperadmin, so RequireSuperadmin alone would let a
        // leaked bee_ key read out every other key that opens this vault, and who owns it.
        // RequireNonAgent is what actually stops it — the internal-key header is supplied here on
        // purpose so the 403 proves that rule and not a missing header.
        var apiKey = AgentKeyHelper.GenerateApiKey();
        await CreateAgentAsync("claude-desktop", _adminUserId, canAutoUnlock: false, apiKey: apiKey);

        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await client.GetAsync("/api/session/lock-impact");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
