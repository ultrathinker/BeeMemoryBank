using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Integration tests for migration 004 agent permission boundaries.
///
/// Tests that agents (bearer-token callers) are blocked from:
///   - session/lock and session/unlock (human-only operations)
///   - user management endpoints
///   - agent management endpoints
///
/// The answer is 404, not the 403 these originally asserted. An agent presents a bee_ key and no
/// internal key, and the REST admin surface is not published to keyless callers at all
/// (PublicSurface) — so the request is refused before it reaches the handler that would have said
/// "forbidden". That is the stronger answer: an agent should not be able to tell which of these
/// endpoints exist. What matters to these tests is unchanged — an agent cannot unlock the vault,
/// create a user, or mint another agent — so they assert exactly that, at whichever layer stops it.
///
/// NOTE: These tests are written but not run until explicitly requested.
/// Run with: dotnet test --filter "FullyQualifiedName~AgentPermissionsTests"
/// </summary>
public class AgentPermissionsTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _adminClient = null!;
    private const string Password = "testPassword123";

    public async Task InitializeAsync()
    {
        _adminClient = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a test agent owned by the bootstrap superadmin.
    /// Returns the agent API key.
    /// Requires: node initialized, session unlocked (or will unlock).
    /// </summary>
    private async Task<string> CreateTestAgentAsync()
    {
        // Unlock session
        var unlock = await _adminClient.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        // Bootstrap login: create the first superadmin user
        var login = await _adminClient.PostAsJsonAsync("/api/session/login", new
        {
            username = "admin",
            password = Password
        });
        login.EnsureSuccessStatusCode();
        var loginData = await login.Content.ReadFromJsonAsync<JsonElement>();
        var userId = loginData.GetProperty("userId").GetInt32();

        // Get master DEK from the shared (singleton) session service
        var session = _factory.Services.GetRequiredService<SessionService>();
        var masterDek = session.GetMasterDek();

        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);

        // Insert agent directly via IAgentRepository to set OwnerUserId
        using var scope = _factory.Services.CreateScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agent = new Agent
        {
            Name = "Test Agent",
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext,
            DekIV = iv,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = userId
        };
        await agentRepo.CreateAsync(agent);

        return apiKey;
    }

    /// <summary>Creates an HTTP client that authenticates as the given agent.</summary>
    private HttpClient CreateAgentClient(string apiKey)
    {
        var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    // ─────────────────────────────────────────────────────────────
    // Blocked endpoints
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Agent_CannotCall_SessionLock_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.PostAsJsonAsync("/api/session/lock", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_CannotCall_SessionUnlock_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_CannotCreateUser_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.PostAsJsonAsync("/api/users", new
        {
            username = "newuser",
            password = "pass",
            displayName = "New User",
            role = "user"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_CannotDeleteUser_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.DeleteAsync("/api/users/1");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_CannotCreateAgent_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.PostAsJsonAsync("/api/agents", new { name = "another-agent" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_CannotDeleteAgent_IsRefused()
    {
        var apiKey = await CreateTestAgentAsync();
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.DeleteAsync("/api/agents/1");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_WithDeactivatedOwner_Returns401()
    {
        // Arrange: create user, create agent, then deactivate the user
        var apiKey = await CreateTestAgentAsync();

        // Get the owner user and deactivate them directly
        using (var scope = _factory.Services.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var users = await userRepo.ListActiveAsync();
            var owner = users.First(u => u.Username == "admin");
            owner.IsActive = false;
            await userRepo.UpdateAsync(owner);
        }

        // Act: agent attempts an MCP call. Deliberately /mcp and not a REST path — the REST surface
        // is no longer reachable by a keyless caller at all (it answers 404 before auth runs), which
        // would make this test pass without ever exercising the deactivated-owner check it is about.
        using var agentClient = CreateAgentClient(apiKey);
        var resp = await agentClient.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8, "application/json"));

        // Assert: the key is rejected because its owner is gone
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─────────────────────────────────────────────────────────────
    // Owner-based scope: agent inherits owner's folder restrictions
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Agent_WithOwnerDenyList_CannotAccess_RestrictedFolder()
    {
        // Arrange: unlock, create article in /Secret, restrict /Secret for owner
        await _adminClient.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        var createArticle = await _adminClient.PostAsJsonAsync("/api/articles", new
        {
            title = "Secret Article",
            treePath = "/Secret",
            content = "Top secret"
        });
        createArticle.EnsureSuccessStatusCode();
        var article = await createArticle.Content.ReadFromJsonAsync<JsonElement>();
        var articleId = article.GetProperty("id").GetString();

        // Create a regular user whose scope has DenyList /Secret
        var createUser = await _adminClient.PostAsJsonAsync("/api/users", new
        {
            username = "restricteduser",
            password = Password,
            displayName = "Restricted User",
            role = "user"
        });
        createUser.EnsureSuccessStatusCode();
        var user = await createUser.Content.ReadFromJsonAsync<JsonElement>();
        var restrictedUserId = user.GetProperty("id").GetInt32();

        // Add /Secret to the user's deny list (must create the folder first)
        var createFolder = await _adminClient.PostAsJsonAsync("/api/folders", new
        {
            path = "/Secret",
            name = "Secret"
        });
        // (folder may already exist — ignore 409)

        await _adminClient.PostAsJsonAsync($"/api/users/{restrictedUserId}/folder-restrictions",
            new { path = "/Secret" });

        // Create agent owned by the restricted user
        var session = _factory.Services.GetRequiredService<SessionService>();
        var masterDek = session.GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);

        using var scope = _factory.Services.CreateScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agent = new Agent
        {
            Name = "Restricted Agent",
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext,
            DekIV = iv,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = restrictedUserId
        };
        await agentRepo.CreateAsync(agent);

        // Act: agent tries to access the secret article
        using var agentClient = CreateAgentClient(apiKey);
        var resp = await agentClient.GetAsync($"/api/articles/{articleId}/content");

        // Assert: denied (403 or 404 since the article is filtered out of the scope)
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }
}
