using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Integration tests for whitelist management API.
/// </summary>
public class WhitelistApiTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private const string Password = "whitelistPassword";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _factory = new BmbWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("WhitelistTestNode", Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetWhitelist_InitiallyContainsOwnNode()
    {
        // InitializeNodeAsync adds the node itself to the whitelist
        var resp = await _client.GetAsync("/api/whitelist");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await resp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        entries!.Should().HaveCount(1);
        entries![0].GetProperty("displayName").GetString().Should().Be("WhitelistTestNode");
    }

    [Fact]
    public async Task AddEntry_RequiresUnlockedSession()
    {
        var req = MakeAddRequest(Guid.NewGuid());
        var resp = await _client.PostAsJsonAsync("/api/whitelist", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddEntry_Succeeds_WhenUnlocked()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        var req = MakeAddRequest(nodeId);
        var resp = await _client.PostAsJsonAsync("/api/whitelist", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var entry = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        entry.GetProperty("nodeId").GetGuid().Should().Be(nodeId);
        entry.GetProperty("displayName").GetString().Should().Be("TestPeer");
        entry.GetProperty("status").GetString().Should().Be("A");
    }

    [Fact]
    public async Task AddEntry_Duplicate_Returns409()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        var req = MakeAddRequest(nodeId);
        (await _client.PostAsJsonAsync("/api/whitelist", req)).EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync("/api/whitelist", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetEntry_ReturnsAddedEntry()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        (await _client.PostAsJsonAsync("/api/whitelist", MakeAddRequest(nodeId))).EnsureSuccessStatusCode();

        var resp = await _client.GetAsync($"/api/whitelist/{nodeId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        entry.GetProperty("nodeId").GetGuid().Should().Be(nodeId);
    }

    [Fact]
    public async Task GetEntry_NotFound_Returns404()
    {
        var resp = await _client.GetAsync($"/api/whitelist/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateEntry_ChangesApiAddress()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        (await _client.PostAsJsonAsync("/api/whitelist", MakeAddRequest(nodeId))).EnsureSuccessStatusCode();

        var updateReq = new { apiAddress = "https://peer.example.com" };
        var putResp = await _client.PutAsJsonAsync($"/api/whitelist/{nodeId}", updateReq);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await putResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        entry.GetProperty("apiAddress").GetString().Should().Be("https://peer.example.com");
    }

    [Fact]
    public async Task RevokeEntry_RequiresUnlockedSession()
    {
        // Add entry via DI (bypass unlock check) so session stays locked
        var nodeId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var now = DateTime.UtcNow;
            await repo.CreateAsync(new WhitelistEntry
            {
                NodeId = nodeId, DisplayName = "Peer",
                Ed25519PublicKey = new byte[32], Status = "A",
                CreatedAt = now, UpdatedAt = now
            });
        }

        // Session is still locked — DELETE should be 403
        var resp = await _client.DeleteAsync($"/api/whitelist/{nodeId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokeEntry_Succeeds_AndEntryDisappearsFromList()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        (await _client.PostAsJsonAsync("/api/whitelist", MakeAddRequest(nodeId))).EnsureSuccessStatusCode();

        var deleteResp = await _client.DeleteAsync($"/api/whitelist/{nodeId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Peer entry should be gone; only own node remains
        var listResp = await _client.GetAsync("/api/whitelist");
        var entries = await listResp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        entries!.Should().NotContain(e => e.GetProperty("nodeId").GetGuid() == nodeId);
    }

    [Fact]
    public async Task AddEntry_LogsWhitelistAddEvent()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        (await _client.PostAsJsonAsync("/api/whitelist", MakeAddRequest(nodeId))).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var events = await eventLog.GetAfterSequenceAsync(0, 100);
        events.Should().ContainSingle(e => e.EventType == EventTypes.WhitelistAdd);
    }

    [Fact]
    public async Task RevokeEntry_LogsWhitelistRevokeEvent()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        (await _client.PostAsJsonAsync("/api/whitelist", MakeAddRequest(nodeId))).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/whitelist/{nodeId}")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var events = await eventLog.GetAfterSequenceAsync(0, 100);
        events.Should().ContainSingle(e => e.EventType == EventTypes.WhitelistRevoke);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task UnlockAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/unlock", new { Password });
        resp.EnsureSuccessStatusCode();
    }

    private static object MakeAddRequest(Guid nodeId) => new
    {
        nodeId,
        displayName = "TestPeer",
        // Valid 32-byte Ed25519 public key (all zeros for testing)
        ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
        apiAddress = (string?)null,
        canGenerateEmbeddings = false
    };
}
