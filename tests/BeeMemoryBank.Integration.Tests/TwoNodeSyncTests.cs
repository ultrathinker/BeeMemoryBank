using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Integration tests for bidirectional sync between two nodes.
/// Both nodes run as WebApplicationFactory (in-process HTTP).
/// SyncClient directly uses test HttpClients from both factories.
/// </summary>
public class TwoNodeSyncTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _nodeA = null!;
    private BmbWebApplicationFactory _nodeB = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    private const string MasterPassword = "sharedPassword123";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        _nodeB = new BmbWebApplicationFactory();

        // NodeA is the primary node
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();

        // Unlock NodeA so /api/join can validate the password
        await UnlockAsync(_clientA, MasterPassword);

        // NodeB joins NodeA's network — shares the same Master DEK
        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _nodeB.CreateClient();

        await UnlockAsync(_clientB, MasterPassword);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _nodeA.Dispose();
        _nodeB.Dispose();
        return Task.CompletedTask;
    }

    // ─── Sync API ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Identity_ReturnsNodeInfo()
    {
        var resp = await _clientA.GetAsync("/api/sync/identity");
        resp.IsSuccessStatusCode.Should().BeTrue();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("nodeId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("displayName").GetString().Should().Be("NodeA");
        body.GetProperty("ed25519PublicKeyB64").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Challenge_Authenticate_Succeeds()
    {
        // NodeB authenticates on NodeA
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PullEvents_RequiresAuth()
    {
        var resp = await _clientB.GetAsync("/api/sync/events?afterSequence=0");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PullEvents_WithAuth_ReturnsEvents()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await _clientA.SendAsync(req);

        resp.IsSuccessStatusCode.Should().BeTrue();
        var events = await resp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        events.Should().NotBeNull();
        // After join, NodeA has a whitelist_add event for NodeB
        events!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PushEvents_PermanentlyFailingEvent_GetsQuarantinedOnReceiver()
    {
        // NodeB authenticates on NodeA and pushes an event that can never apply: it carries NodeB's
        // (whitelisted, non-revoked) node id so it passes the originator checks, but has a garbage
        // signature, so EventApplier throws InvalidDataException on every attempt — a permanent
        // failure. Before receive-side quarantine was wired, such a push was only logged and counted
        // as skipped, leaving no persistent trace; it must now surface in the same quarantine store
        // the pull path already uses.
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        Guid nodeBId;
        using (var scope = _nodeB.Services.CreateScope())
        {
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            nodeBId = (await nodeRepo.GetAsync())!.NodeId;
        }

        var badEventId = Guid.NewGuid();
        var badEvent = new
        {
            sequenceNum = 999_999L,
            eventId = badEventId,
            nodeId = nodeBId,
            lamportTs = 1L,
            eventType = "article_create",
            payload = "{}",
            signature = new byte[64], // invalid — never matches NodeB's key
            protocolVersion = 1,
            createdAt = DateTime.UtcNow
        };

        // Push the identical failing event enough times to exhaust its permanent-failure budget.
        for (int i = 0; i < SyncEventQuarantine.QuarantineThreshold; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/events")
            {
                Content = JsonContent.Create(new[] { badEvent })
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await _clientA.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            // Behavior is unchanged: the event is still skipped-and-continue, never applied.
            result.GetProperty("skipped").GetInt32().Should().Be(1);
            result.GetProperty("applied").GetInt32().Should().Be(0);
        }

        // The receiver now holds a persisted quarantine record for the event.
        using (var scope = _nodeA.Services.CreateScope())
        {
            var quarantineRepo = scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>();
            var entries = await SyncEventQuarantine.ListAllAsync(quarantineRepo);
            var entry = entries.SingleOrDefault(e => e.EventId == badEventId);
            entry.Should().NotBeNull("a permanently-failing pushed event must be recorded in the receiver's quarantine");
            entry!.PermanentFailureCount.Should().BeGreaterThanOrEqualTo(SyncEventQuarantine.QuarantineThreshold);
            entry.Quarantined.Should().BeTrue("it exhausted its permanent-failure retry budget");
        }
    }

    // ─── SyncClient ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncClient_NodeB_PullsArticlesCreatedOnNodeA()
    {
        // NodeA creates an article
        var articleResp = await CreateArticleAsync(_clientA, "Sync-test article", "/Root");
        var articleId = articleResp.GetProperty("id").GetString()!;

        // NodeB syncs with NodeA
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        // Article should appear on NodeB
        var resp = await _clientB.GetAsync($"/api/articles/{articleId}");
        resp.IsSuccessStatusCode.Should().BeTrue();
        var article = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        article.GetProperty("title").GetString().Should().Be("Sync-test article");
    }

    [Fact]
    public async Task SyncClient_BidirectionalSync_BothArticlesOnBothNodes()
    {
        // NodeA creates article A
        var artA = await CreateArticleAsync(_clientA, "Article from NodeA", "/A");
        var idA = artA.GetProperty("id").GetString()!;

        // NodeB creates article B
        var artB = await CreateArticleAsync(_clientB, "Article from NodeB", "/B");
        var idB = artB.GetProperty("id").GetString()!;

        // NodeB syncs with NodeA: receives article A and sends article B
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);
        // NodeA syncs with NodeB: receives article B
        await SyncNodeWithAsync(_nodeA, _clientB, _nodeB);

        // Article A on NodeB
        (await _clientB.GetAsync($"/api/articles/{idA}")).IsSuccessStatusCode.Should().BeTrue();
        // Article B on NodeA
        (await _clientA.GetAsync($"/api/articles/{idB}")).IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task SyncClient_Idempotent_DoubleSyncNoduplicates()
    {
        await CreateArticleAsync(_clientA, "Idempotent", "/Test");

        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA); // second time — no-op

        var resp = await _clientB.GetAsync("/api/articles");
        var articles = await resp.Content.ReadFromJsonAsync<JsonElement[]>(JsonOpts);
        articles!.Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncClient_DeletePropagates()
    {
        // NodeA creates and syncs
        var art = await CreateArticleAsync(_clientA, "For deletion", "/Del");
        var id = art.GetProperty("id").GetString()!;
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        // Verify article exists on NodeB
        (await _clientB.GetAsync($"/api/articles/{id}")).IsSuccessStatusCode.Should().BeTrue();

        // NodeA deletes
        await _clientA.DeleteAsync($"/api/articles/{id}");

        // NodeB syncs — article should disappear
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);
        var resp = await _clientB.GetAsync($"/api/articles/{id}");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    // Whitelisting is handled by the join process — no manual cross-add needed.

    private static async Task<SyncIdentityDto> GetIdentityAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/sync/identity");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SyncIdentityDto>(JsonOpts)
            ?? throw new InvalidDataException();
    }

    /// <summary>
    /// Client node authenticates on server.
    /// Returns a Bearer token.
    /// </summary>
    private static async Task<string> AuthNodeOnServerAsync(
        BmbWebApplicationFactory clientNode, HttpClient server)
    {
        // Get challenge from server
        var challengeResp = await server.PostAsync("/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts)
            ?? throw new InvalidDataException();

        // Sign challenge with client node key
        using var scope = clientNode.Services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException();

        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        // V2: the signed payload is bound to the audience node's id. The server no longer accepts
        // the old unbound V1 tag at all — see PeerAuthenticator's domain-tag comment.
        var domainTag = "BMB-CHALLENGE-V2\0"u8.ToArray();
        var challengePayload = domainTag
            .Concat(challengeData.ServerNodeId.ToByteArray())
            .Concat(challengeBytes)
            .ToArray();
        var session = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
        var masterDek = session.GetMasterDek();
        byte[] signature;
        try
        {
            signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, masterDek, challengePayload);
        }
        finally { Array.Clear(masterDek); }

        // Send authentication request
        var authResp = await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        });
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts)
            ?? throw new InvalidDataException();

        return authData.Token;
    }

    /// <summary>
    /// Node syncs with remote server.
    /// Uses the real SyncClient from node's DI.
    /// </summary>
    private static async Task SyncNodeWithAsync(
        BmbWebApplicationFactory node, HttpClient serverClient, BmbWebApplicationFactory server)
    {
        using var serverScope = server.Services.CreateScope();
        var serverIdentity = await serverScope.ServiceProvider
            .GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("Server node is not initialized.");

        using var scope = node.Services.CreateScope();
        var syncClient = scope.ServiceProvider.GetRequiredService<SyncClient>();
        // remoteApiBase = "" works because serverClient.BaseAddress = http://localhost/. The peer
        // id is passed explicitly, exactly as SyncScheduler passes the whitelist entry's NodeId —
        // it is the audience anchor the peer itself must not be able to choose.
        await syncClient.SyncWithAsync(serverClient, "", serverIdentity.NodeId);
    }

    private static async Task<JsonElement> CreateArticleAsync(
        HttpClient client, string title, string treePath)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new
        {
            title,
            treePath,
            content = "test content"
        });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private sealed record SyncIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);
    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
