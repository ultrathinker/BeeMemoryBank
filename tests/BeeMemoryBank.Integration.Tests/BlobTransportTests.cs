using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Protocol 2 blob transport between two in-process nodes: the /api/sync/blobs/* endpoints and
/// the guarantees SyncClient/BlobTransport give around them — bytes arrive before the events that
/// name them, nothing but well-formed hashes is accepted, and wrong bytes can never sit at a hash
/// an event will look up.
/// </summary>
public class BlobTransportTests : IAsyncLifetime
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
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        (await _clientA.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword })).EnsureSuccessStatusCode();
        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _nodeB.CreateClient();
        (await _clientB.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose(); _clientB.Dispose();
        _nodeA.Dispose(); _nodeB.Dispose();
        return Task.CompletedTask;
    }

    // ─── End to end through SyncClient ───────────────────────────────────────

    [Fact]
    public async Task Pull_FetchesBlobBeforeApplying_EventCarriesOnlyTheHash()
    {
        var art = await CreateArticleAsync(_clientA, "Pulled", "/P", "pulled body");
        var id = Guid.Parse(art.GetProperty("id").GetString()!);

        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        // The blob landed on B under the hash A's event names, and the event B recorded carries
        // no inline body.
        using var scopeB = _nodeB.Services.CreateScope();
        var eventsB = await scopeB.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0);
        var create = eventsB.Single(e => e.EventType == EventTypes.ArticleCreate && e.ArticleId == id);
        using var doc = JsonDocument.Parse(create.Payload);
        var hash = doc.RootElement.GetProperty("ciphertext_sha256").GetString()!;
        doc.RootElement.GetProperty("ciphertext").ValueKind.Should().Be(JsonValueKind.Null);

        var blobsB = scopeB.ServiceProvider.GetRequiredService<IBlobRepository>();
        var bytes = await blobsB.GetAsync(hash);
        bytes.Should().NotBeNull();
        BlobHash.Compute(bytes!).Should().Be(hash);

        // And the article is readable on B — the body resolved through the blob.
        var resp = await _clientB.GetAsync($"/api/articles/{id}/content");
        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("content").GetString().Should().Be("pulled body");
    }

    [Fact]
    public async Task Push_ShipsBlobsFirst_ReceiverCanReadTheArticle()
    {
        var art = await CreateArticleAsync(_clientB, "Pushed", "/Q", "pushed body");
        var id = art.GetProperty("id").GetString()!;

        // B dials A: pull (nothing new for this article) then push B's events, blobs ahead.
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        var resp = await _clientA.GetAsync($"/api/articles/{id}/content");
        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("content").GetString().Should().Be("pushed body");
    }

    [Fact]
    public async Task Update_ReferencesNewBlob_OldOneStaysForHistory()
    {
        var art = await CreateArticleAsync(_clientA, "Versioned", "/V", "v1");
        var id = art.GetProperty("id").GetString()!;
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await _clientA.PutAsJsonAsync($"/api/articles/{id}", new { title = "Versioned", content = "v2" })).EnsureSuccessStatusCode();
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        var resp = await _clientB.GetAsync($"/api/articles/{id}/content");
        resp.EnsureSuccessStatusCode();
        (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("content").GetString().Should().Be("v2");

        // Both ciphertexts are in B's store: v2 as the live body, v1 referenced by the version
        // row and by the create event still in the log.
        using var scopeB = _nodeB.Services.CreateScope();
        var eventsB = await scopeB.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0);
        var hashes = BlobReferences.Collect(eventsB.Where(e => e.ArticleId?.ToString() == id));
        hashes.Should().HaveCount(2);
        var have = await scopeB.ServiceProvider.GetRequiredService<IBlobRepository>().GetExistingAsync(hashes.ToList());
        have.Should().BeEquivalentTo(hashes);
    }

    // ─── Endpoint contract ───────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_RequirePeerAuth()
    {
        var body = JsonContent.Create(new { hashes = new[] { new string('a', 64) } });
        (await _clientA.PostAsync("/api/sync/blobs/check", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _clientA.PostAsync("/api/sync/blobs/get", JsonContent.Create(new { hashes = new[] { new string('a', 64) } })))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _clientA.PostAsync("/api/sync/blobs", JsonContent.Create(new { blobs = Array.Empty<object>() })))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Check_RejectsMalformedHashes_ReportsUnknownOnesMissing()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        var bad = await PostAsync(_clientA, token, "/api/sync/blobs/check", new { hashes = new[] { "not-a-hash" } });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var upper = await PostAsync(_clientA, token, "/api/sync/blobs/check", new { hashes = new[] { new string('A', 64) } });
        upper.StatusCode.Should().Be(HttpStatusCode.BadRequest, "hashes are canonical lowercase hex; anything else is not a key");

        var unknown = new string('0', 64);
        var ok = await PostAsync(_clientA, token, "/api/sync/blobs/check", new { hashes = new[] { unknown } });
        ok.EnsureSuccessStatusCode();
        var missing = (await ok.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList();
        missing.Should().Equal(unknown);
    }

    [Fact]
    public async Task Upload_StoresUnderRealHash_WrongClaimCannotShadowContent()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var realHash = BlobHash.Compute(data);
        var claimed = new string('f', 64);

        var resp = await PostAsync(_clientA, token, "/api/sync/blobs", new
        {
            blobs = new[] { new { hash = claimed, data = Convert.ToBase64String(data) } }
        });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        result.GetProperty("stored").GetInt32().Should().Be(1);

        using var scope = _nodeA.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobRepository>();
        (await blobs.GetAsync(claimed)).Should().BeNull("the claimed address must stay empty");
        (await blobs.GetAsync(realHash)).Should().BeEquivalentTo(data);

        // And check/get agree with the store.
        var check = await PostAsync(_clientA, token, "/api/sync/blobs/check", new { hashes = new[] { claimed, realHash } });
        var missing = (await check.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList();
        missing.Should().Equal(claimed);

        var get = await PostAsync(_clientA, token, "/api/sync/blobs/get", new { hashes = new[] { claimed, realHash } });
        var got = (await get.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("blobs").EnumerateArray().ToList();
        got.Should().HaveCount(1);
        got[0].GetProperty("hash").GetString().Should().Be(realHash);
        Convert.FromBase64String(got[0].GetProperty("data").GetString()!).Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Upload_CountsMalformedBase64AsRejected()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);
        var resp = await PostAsync(_clientA, token, "/api/sync/blobs", new
        {
            blobs = new[] { new { hash = new string('a', 64), data = "%%%not base64%%%" } }
        });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        result.GetProperty("stored").GetInt32().Should().Be(0);
        result.GetProperty("rejected").GetInt32().Should().Be(1);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private static async Task<string> AuthNodeOnServerAsync(BmbWebApplicationFactory clientNode, HttpClient server)
    {
        var challengeResp = await server.PostAsync("/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challenge = await challengeResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var serverNodeId = challenge.GetProperty("serverNodeId").GetGuid();

        using var scope = clientNode.Services.CreateScope();
        var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray()
            .Concat(serverNodeId.ToByteArray())
            .Concat(Convert.FromBase64String(challengeB64))
            .ToArray();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        var dek = session.GetMasterDek();
        byte[] signature;
        try
        {
            signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, dek, payload);
        }
        finally { Array.Clear(dek); }

        var authResp = await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId, ChallengeB64 = challengeB64, SignatureB64 = Convert.ToBase64String(signature)
        });
        authResp.EnsureSuccessStatusCode();
        return (await authResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("token").GetString()!;
    }

    private static async Task SyncNodeWithAsync(BmbWebApplicationFactory node, HttpClient serverClient, BmbWebApplicationFactory server)
    {
        using var serverScope = server.Services.CreateScope();
        var serverIdentity = (await serverScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(serverClient, "", serverIdentity.NodeId);
    }

    private static async Task<JsonElement> CreateArticleAsync(HttpClient client, string title, string treePath, string content)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new { title, treePath, content });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }
}
