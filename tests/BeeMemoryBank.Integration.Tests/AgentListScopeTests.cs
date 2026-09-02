using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// GET /api/agents is shared by two very different views: the Profile page's
/// "My AI Agents" list and the Admin page's node-wide agent table. It used to
/// return every agent on the node whenever the caller was a superadmin, so an
/// admin's own Profile page listed other people's agents as if they were theirs
/// (complete with a delete button and a wrong "used N of 20" quota).
///
/// The scope is now owner-only by default; only an explicit ?all=true from a
/// superadmin widens it.
/// </summary>
public class AgentListScopeTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "testPassword123";

    private int _adminUserId;
    private int _otherUserId;

    public async Task InitializeAsync()
    {
        using var client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);

        var unlock = await client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        _adminUserId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetInt32();

        using var scope = _factory.Services.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _otherUserId = await userRepo.CreateAsync(new User
        {
            Username = "colleague",
            DisplayName = "Colleague",
            Role = UserRoles.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        await agentRepo.CreateAsync(NewAgent("admin-agent", "bee_admin1", _adminUserId));
        await agentRepo.CreateAsync(NewAgent("colleague-agent", "bee_other1", _otherUserId));
        await agentRepo.CreateAsync(NewAgent("colleague-agent-2", "bee_other2", _otherUserId));
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static Agent NewAgent(string name, string keyPrefix, int ownerUserId) => new()
    {
        Name = name,
        KeyPrefix = keyPrefix,
        KeyHash = "hash-" + keyPrefix,
        EncryptedDek = [1, 2, 3],
        DekIV = [4, 5, 6],
        Status = "A",
        CreatedAt = DateTime.UtcNow,
        OwnerUserId = ownerUserId
    };

    private HttpClient ClientAs(int userId, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private static async Task<List<string>> AgentNamesAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var items = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return items.EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public async Task Superadmin_DefaultScope_SeesOnlyOwnAgents()
    {
        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);

        var names = await AgentNamesAsync(await client.GetAsync("/api/agents"));

        names.Should().BeEquivalentTo(["admin-agent"]);
    }

    [Fact]
    public async Task Superadmin_WithAllTrue_SeesEveryAgent()
    {
        using var client = ClientAs(_adminUserId, UserRoles.Superadmin);

        var names = await AgentNamesAsync(await client.GetAsync("/api/agents?all=true"));

        names.Should().BeEquivalentTo(["admin-agent", "colleague-agent", "colleague-agent-2"]);
    }

    [Fact]
    public async Task RegularUser_WithAllTrue_StillSeesOnlyOwnAgents()
    {
        using var client = ClientAs(_otherUserId, UserRoles.User);

        var names = await AgentNamesAsync(await client.GetAsync("/api/agents?all=true"));

        names.Should().BeEquivalentTo(["colleague-agent", "colleague-agent-2"]);
    }

    [Fact]
    public async Task MissingCallerId_IsRejected()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/agents");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
