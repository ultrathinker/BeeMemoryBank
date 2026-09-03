using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Drives POST /api/init/join — a brand-new node joining an existing mesh — through the REAL
/// endpoint rather than the hand-rolled <see cref="BmbWebApplicationFactory.JoinNodeAsync"/> helper
/// that most other tests in this suite use. That helper reimplements joining by hand (POSTs
/// /api/join directly and does the key-slot crypto itself) and never exercises
/// InitEndpoints.cs's own inline challenge/authenticate handshake — which is the exact code path
/// that kept signing the removed BMB-CHALLENGE-V1 domain tag and 401'd against any upgraded node
/// (see commit 3c43dfc5). This test exists so breaking that handshake fails the suite.
/// </summary>
public class InitJoinEndpointTests : IAsyncLifetime
{
    private const string MasterPassword = "initJoinPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _nodeA = null!;
    private HttpClient _clientA = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        await UnlockAsync(_clientA, MasterPassword);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _nodeA.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Join_RealEndpoint_InitializesNodeImportsDataAndAuthenticatesAfterwards()
    {
        // NodeA has real content BEFORE NodeB joins, so we can prove the snapshot pulled inside
        // /api/init/join actually carried it across.
        var article = await CreateArticleAsync(_clientA, "Init-join article", "/InitJoin");
        var articleId = article.GetProperty("id").GetString()!;

        using var nodeB = new BmbWebApplicationFactory();
        // NodeB is not initialized yet. Its own /api/init/join handler will call
        // httpClientFactory.CreateClient() and hit "req.RemoteUrl" — route that straight into
        // NodeA's real in-process TestServer so the real production handshake code (domain tag,
        // signing, verification) actually runs against a real peer, not a mock.
        nodeB.RouteOutboundHttpThrough(_nodeA.Server.CreateHandler());
        var clientB = nodeB.CreateClient();

        var joinResp = await clientB.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "NodeB",
            remoteUrl = "http://node-a", // arbitrary — RouteOutboundHttpThrough ignores the host
            password = MasterPassword
        }, JsonOpts);

        var joinBody = await joinResp.Content.ReadAsStringAsync();
        joinResp.IsSuccessStatusCode.Should().BeTrue($"join should succeed, got: {joinBody}");

        Guid nodeAId;
        using (var scopeA = _nodeA.Services.CreateScope())
        {
            var nodeAIdentity = await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            nodeAIdentity.Should().NotBeNull();
            nodeAId = nodeAIdentity!.NodeId;
        }

        using (var scopeB = nodeB.Services.CreateScope())
        {
            // NodeB ended up correctly initialized: identity present...
            var identity = await scopeB.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
            identity.Should().NotBeNull();

            // ...and a whitelist entry for NodeA (trust-on-join).
            var whitelistEntry = await scopeB.ServiceProvider
                .GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeAId);
            whitelistEntry.Should().NotBeNull();
            whitelistEntry!.DisplayName.Should().Be("NodeA");
            whitelistEntry.IsSuperadmin.Should().BeTrue();
        }

        // The snapshot pulled as part of the join actually carried NodeA's content across.
        await UnlockAsync(clientB, MasterPassword);
        var getArticleResp = await clientB.GetAsync($"/api/articles/{articleId}");
        getArticleResp.IsSuccessStatusCode.Should().BeTrue();
        var importedArticle = await getArticleResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        importedArticle.GetProperty("title").GetString().Should().Be("Init-join article");

        // NodeB can authenticate on NodeA again afterwards — the identity /api/init/join created
        // is recognized going forward, not just for the one-shot join handshake. (This second
        // handshake is the ALREADY-covered steady-state peer-auth path, not one of the three flows
        // this test targets — it is here only to prove the joined node is fully functional.)
        var token = await AuthNodeOnServerAsync(nodeB, _clientA);
        token.Should().NotBeNullOrEmpty();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> CreateArticleAsync(HttpClient client, string title, string treePath)
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

    private static async Task<string> AuthNodeOnServerAsync(
        BmbWebApplicationFactory clientNode, HttpClient server)
    {
        var challengeResp = await server.PostAsync("/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts)
            ?? throw new InvalidDataException();

        using var scope = clientNode.Services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException();

        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
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

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
