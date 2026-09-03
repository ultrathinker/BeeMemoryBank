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
/// M2 regression tests: an agent bearer-key caller used to bypass BOTH chat kill switches —
/// <c>ChatAccessEndpointFilter</c> and <c>GET /api/chat/access</c> both special-cased
/// <c>caller.AgentId.HasValue</c> to always allow, on the theory that an agent is "a separate,
/// already-authenticated auth path". That conflated authentication with authorization: an agent
/// key is scoped to its owning user, so disabling chat for that user (or for the whole node) did
/// nothing for that user's agent keys. The fix removes the special case entirely — an agent's
/// access now inherits its owner's, exactly like every other permission an agent has (folder ACLs,
/// role, etc. — see AgentPermissionsTests for the equivalent pattern applied to folder scope).
/// </summary>
public class ChatAccessAgentInheritanceTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _adminClient = null!;
    private const string Password = "chatAccessTestPassword123";

    public async Task InitializeAsync()
    {
        _adminClient = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        var unlock = await _adminClient.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Creates a regular (non-superadmin) user with the given chat_access flag and an
    /// agent key owned by them. Returns the agent's plaintext API key.</summary>
    private async Task<string> CreateUserAndOwnedAgentAsync(string username, bool chatAccess)
    {
        var createUser = await _adminClient.PostAsJsonAsync("/api/users", new
        {
            username,
            password = Password,
            displayName = username,
            role = "user",
            chatAccess
        });
        createUser.EnsureSuccessStatusCode();
        var user = await createUser.Content.ReadFromJsonAsync<JsonElement>();
        var ownerUserId = user.GetProperty("id").GetInt32();

        var session = _factory.Services.GetRequiredService<SessionService>();
        var masterDek = session.GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);

        using var scope = _factory.Services.CreateScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        await agentRepo.CreateAsync(new Agent
        {
            Name = $"{username}-agent",
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext,
            DekIV = iv,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = ownerUserId
        });

        return apiKey;
    }

    /// <summary>An HttpClient carrying both the internal-key gate (needed to reach the endpoint at
    /// all) and the agent's bearer token. The internal key alone would default X-User-Role to
    /// "superadmin" (see BmbWebApplicationFactory.CreateClient) — irrelevant here, because
    /// AgentAuthMiddleware pre-builds a full CallerIdentity from the agent's ACTUAL owner in the DB
    /// and CallerIdentity.Extract always prefers that pre-built identity over any header.</summary>
    private HttpClient CreateAgentClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    [Fact]
    public async Task Agent_AccessCheck_ReflectsOwnersChatAccessFalse_NotAlwaysAllowed()
    {
        var apiKey = await CreateUserAndOwnedAgentAsync("m2-no-chat-access", chatAccess: false);
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.GetAsync("/api/chat/access");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("allowed").GetBoolean().Should().BeFalse(
            "an agent's chat access must inherit its owner's chat_access flag, not bypass it");
    }

    [Fact]
    public async Task Agent_AccessCheck_ReflectsOwnersChatAccessTrue_WhenGlobalToggleOn()
    {
        var apiKey = await CreateUserAndOwnedAgentAsync("m2-has-chat-access", chatAccess: true);
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.GetAsync("/api/chat/access");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("allowed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Agent_AccessCheck_RespectsGlobalKillSwitch_EvenWithChatAccessTrue()
    {
        var apiKey = await CreateUserAndOwnedAgentAsync("m2-global-off", chatAccess: true);

        var disableGlobal = await _adminClient.PatchAsJsonAsync("/api/chat/settings/chat-enabled", new { enabled = false });
        disableGlobal.EnsureSuccessStatusCode();
        try
        {
            using var agentClient = CreateAgentClient(apiKey);
            var resp = await agentClient.GetAsync("/api/chat/access");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

            body.GetProperty("allowed").GetBoolean().Should().BeFalse(
                "the node-wide chat_globally_enabled kill switch must bind agent callers too");
        }
        finally
        {
            // Restore so other tests in this collection aren't affected by shared node state.
            await _adminClient.PatchAsJsonAsync("/api/chat/settings/chat-enabled", new { enabled = true });
        }
    }

    [Fact]
    public async Task Agent_CannotUseChatStream_WhenOwnerChatAccessFalse()
    {
        // End-to-end: ChatAccessEndpointFilter (not just the informational /access check) must
        // also block the agent from the group it actually gates.
        var apiKey = await CreateUserAndOwnedAgentAsync("m2-stream-blocked", chatAccess: false);
        using var agentClient = CreateAgentClient(apiKey);

        var resp = await agentClient.PostAsJsonAsync("/api/chat/stream", new { message = "hello" });

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }
}
