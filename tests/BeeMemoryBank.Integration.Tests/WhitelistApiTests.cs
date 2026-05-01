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
/// Whitelist entries are inserted directly via IWhitelistRepository in DI scope —
/// there is no public "add peer" HTTP endpoint (peers only enter the whitelist via
/// /api/join or whitelist_add sync events).
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
    public async Task GetWhitelist_InitiallyEmpty()
    {
        // Self never lives in tbl_whitelist — it's in tbl_node_identity only.
        var resp = await _client.GetAsync("/api/whitelist");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await resp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        entries!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntry_ReturnsAddedEntry()
    {
        var nodeId = Guid.NewGuid();
        await AddPeerAsync(nodeId);

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
        await AddPeerAsync(nodeId);

        var updateReq = new { apiAddress = "https://peer.example.com" };
        var putResp = await _client.PutAsJsonAsync($"/api/whitelist/{nodeId}", updateReq);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await putResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        entry.GetProperty("apiAddress").GetString().Should().Be("https://peer.example.com");
    }

    [Fact]
    public async Task RevokeEntry_RequiresUnlockedSession()
    {
        var nodeId = Guid.NewGuid();
        await AddPeerAsync(nodeId);

        // Session is still locked — DELETE should be 403
        var resp = await _client.DeleteAsync($"/api/whitelist/{nodeId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokeEntry_Succeeds_AndEntryDisappearsFromList()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        await AddPeerAsync(nodeId);

        var deleteResp = await _client.DeleteAsync($"/api/whitelist/{nodeId}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listResp = await _client.GetAsync("/api/whitelist");
        var entries = await listResp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        entries!.Should().NotContain(e => e.GetProperty("nodeId").GetGuid() == nodeId);
    }

    [Fact]
    public async Task RevokeEntry_LogsWhitelistRevokeEvent()
    {
        await UnlockAsync();

        var nodeId = Guid.NewGuid();
        await AddPeerAsync(nodeId);
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

    private async Task AddPeerAsync(Guid nodeId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var now = DateTime.UtcNow;
        await repo.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = "TestPeer",
            Ed25519PublicKey = new byte[32],
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
