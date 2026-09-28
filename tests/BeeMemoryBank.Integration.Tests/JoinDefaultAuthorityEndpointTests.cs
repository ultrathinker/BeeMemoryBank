using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Exercises the real HTTP endpoints (not a hand-built <see cref="WhitelistEntry"/>) for the trust
/// model: <c>POST /api/join</c> records whoever proved the master password as a superadmin (BMB-42),
/// and <c>PUT /api/whitelist/{nodeId}/superadmin</c> is how that authority is changed afterwards,
/// deliberately, by an existing superadmin.
/// </summary>
public class JoinDefaultAuthorityEndpointTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private const string Password = "joinDefaultAuthorityPassword";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _factory = new BmbWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("JoinDefaultAuthorityNode", Password);

        var unlockResp = await _client.PostAsJsonAsync("/api/session/unlock", new { Password });
        unlockResp.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// BMB-42: a peer recorded content-only after proving the master password applied its own hard
    /// delete and password-change notice locally while this node refused them — a permanent split.
    /// The row and the whitelist_add the mesh hears about must both say superadmin.
    /// </summary>
    [Fact]
    public async Task Join_NewPeer_IsSuperadmin_InRowAndInWhitelistAddEvent()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var joinResp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId,
            displayName = "JoiningPhone",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });
        joinResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResp = await _client.GetAsync($"/api/whitelist/{nodeId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await getResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        entry.GetProperty("isSuperadmin").GetBoolean().Should().BeTrue(
            "whoever knows the master password is a superadmin (owner's decision, BMB-42)");

        using var scope = _factory.Services.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var add = (await eventLog.GetAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(nodeId.ToString()));
        JsonSerializer.Deserialize<WhitelistAddPayload>(add.Payload)!.IsSuperadmin.Should().BeTrue(
            "the rest of the mesh learns the joiner's authority from this event, not from the row");
    }

    /// <summary>
    /// A blind node holds no DEK and must never gain authority; /api/join is the path that hands out
    /// the DEK, so a blind id is refused outright and leaves no row behind.
    /// </summary>
    [Fact]
    public async Task Join_BlindNodeId_IsRefused_AndLeavesNoRow()
    {
        var nodeId = BlindNodeId.NewId();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var joinResp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId,
            displayName = "BlindBox",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });

        joinResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await joinResp.Content.ReadAsStringAsync()).Should().Contain("blind");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>()
            .GetByNodeIdAsync(nodeId, includeDeleted: true)).Should().BeNull();
    }

    [Fact]
    public async Task Join_NewPeer_ArticleSyncStillReachesIt()
    {
        // The joined peer is announced to the rest of the mesh and syncs content (that path is never
        // gated on is_superadmin — see EventApplier.ApplyAsync's requiresSuperadmin list).
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var joinResp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId,
            displayName = "JoiningLaptop",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });
        joinResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var events = await eventLog.GetAfterSequenceAsync(0, 100);
        events.Should().ContainSingle(
            e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(nodeId.ToString()),
            "the receiving node must still announce the new peer to the rest of the mesh");
    }

    /// <summary>
    /// Promotion is the explicit act that replaces the old default. It must actually flip the row
    /// and tell the mesh, the same way any other whitelist_update does.
    /// </summary>
    [Fact]
    public async Task PromoteEndpoint_GrantsSuperadmin_AndEmitsWhitelistUpdateEvent()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        (await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId,
            displayName = "PeerToPromote",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        })).EnsureSuccessStatusCode();

        // Pin the starting state explicitly — a demoted peer, or one recorded before BMB-42 — since
        // /api/join itself now records a superadmin.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var repo = seedScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var seedEntry = (await repo.GetByNodeIdAsync(nodeId))!;
            seedEntry.IsSuperadmin = false;
            await repo.UpdateAsync(seedEntry);
        }

        var putResp = await _client.PutAsJsonAsync($"/api/whitelist/{nodeId}/superadmin", new { isSuperadmin = true });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await putResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        entry.GetProperty("isSuperadmin").GetBoolean().Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var events = await eventLog.GetAfterSequenceAsync(0, 100);
        events.Should().ContainSingle(e => e.EventType == EventTypes.WhitelistUpdate,
            "the promotion must travel to the rest of the mesh as a whitelist_update event");
    }
}
