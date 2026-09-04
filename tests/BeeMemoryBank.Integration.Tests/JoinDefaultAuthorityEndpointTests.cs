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
/// Exercises the real HTTP endpoints (not a hand-built <see cref="WhitelistEntry"/>) for the two
/// halves of the trust-model change: <c>POST /api/join</c> must no longer hand a new peer authority
/// over cluster state, and <c>PUT /api/whitelist/{nodeId}/superadmin</c> is how that authority is
/// granted afterward, deliberately, by an existing superadmin.
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
    /// The regression this whole change is about: JoinEndpoints.cs used to write every new peer
    /// in with <c>IsSuperadmin = true</c> ("trust-on-join"), which handed a device that only wanted
    /// to sync content the same authority as the admin's own server. Flip the default back to
    /// prove this test actually catches that.
    /// </summary>
    [Fact]
    public async Task Join_NewPeer_IsContentOnlyByDefault()
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

        entry.GetProperty("isSuperadmin").GetBoolean().Should().BeFalse(
            "a node that only proved it knows the master password must not gain authority over " +
            "cluster state (whitelist changes, hard-delete, network restore) merely by joining");
    }

    [Fact]
    public async Task Join_NewPeer_ArticleSyncStillReachesIt()
    {
        // The flip side of content-only: it is still a full sync member for content. Nothing about
        // removing default cluster-state authority should touch the whitelist_add event itself or
        // the peer's ability to receive/send article events (that path is never gated on
        // is_superadmin — see EventApplier.ApplyAsync's requiresSuperadmin list).
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

        // Pin the starting state explicitly rather than assume what /api/join just produced — this
        // test is about the promotion endpoint itself, not a second assertion of the join default
        // already covered by Join_NewPeer_IsContentOnlyByDefault above.
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
