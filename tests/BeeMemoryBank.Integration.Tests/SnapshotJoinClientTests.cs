using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Drives <see cref="SnapshotJoinClient.DownloadAndImportAsync"/> — the mobile app's join path —
/// against a REAL node-A <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>, exercising the
/// actual production challenge/authenticate handshake and snapshot download/import code, not a
/// hand-rolled equivalent.
///
/// This is the "mobile mesh join" flow named in the postmortem for the removed
/// BMB-CHALLENGE-V1 domain tag (commit 3c43dfc5): <see cref="SnapshotJoinClient"/> kept signing
/// the unbound V1 tag and 401'd against any upgraded node, and nothing in the suite caught it
/// because no test drove this class over HTTP — <see cref="JoinWithSnapshotTests"/> only exercises
/// <c>SnapshotService.RestoreForJoinAsync</c> directly, in-process, never going through the
/// network handshake at all.
///
/// The "joiner" side here is a bare, migrated SQLite database with no ASP.NET host around it —
/// exactly what the phone's local store looks like — set up the same way the mobile app's
/// NodeSetupService.JoinAsync does: call /api/join first (to register on the remote's whitelist
/// and fetch the key slot), THEN drive SnapshotJoinClient for the actual data transfer.
/// </summary>
public class SnapshotJoinClientTests : IAsyncLifetime
{
    private const string MasterPassword = "mobileJoinPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _nodeA = null!;
    private HttpClient _clientA = null!;
    // Simulates the phone's own HttpClient: no X-Internal-Key / X-User-Role headers, because a
    // real external mobile client would never send them — only the internal-key-gated
    // /api/init/* group needs those, and none of the endpoints this client calls are in it.
    private HttpClient _mobileHttp = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        await UnlockAsync(_clientA, MasterPassword);

        _mobileHttp = new HttpClient(_nodeA.Server.CreateHandler());
    }

    public Task DisposeAsync()
    {
        _mobileHttp.Dispose();
        _clientA.Dispose();
        _nodeA.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DownloadAndImportAsync_RealHandshake_ImportsSnapshotAndPeerCanThenSync()
    {
        // NodeA has real content before the phone joins. The body needs to be large enough that
        // the snapshot's compressed:decompressed ratio stays under SnapshotJoinClient's zip-bomb
        // guard (decompressed <= 20x compressed) — a near-empty freshly-migrated schema alone
        // compresses so well (mostly-empty B-tree pages) that it trips that guard on its own.
        var article = await CreateArticleAsync(_clientA, "Mobile-join article", "/Mobile", new string('a', 80_000));
        var articleId = Guid.Parse(article.GetProperty("id").GetString()!);

        // Mirrors NodeSetupService.JoinAsync: generate a keypair, call /api/join to register on
        // NodeA's whitelist and fetch the shared Master DEK's key slot. We don't need the DEK
        // itself for this test (SnapshotJoinClient never touches it), only the remote node's info.
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var joinerNodeId = Guid.NewGuid();

        var joinResp = await _mobileHttp.PostAsJsonAsync("http://localhost/api/join", new
        {
            masterPassword = MasterPassword,
            nodeId = joinerNodeId,
            displayName = "MobilePhone",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        }, JsonOpts);
        var joinBody = await joinResp.Content.ReadAsStringAsync();
        joinResp.IsSuccessStatusCode.Should().BeTrue($"/api/join should succeed, got: {joinBody}");

        var joinData = await joinResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var remote = joinData.GetProperty("remoteNode");
        var remotePublicKey = Convert.FromBase64String(remote.GetProperty("ed25519PublicKeyB64").GetString()!);

        var joinerDir = Path.Combine(Path.GetTempPath(), $"bmb_mobile_join_{Guid.NewGuid():N}");
        Directory.CreateDirectory(joinerDir);
        try
        {
            using var joinerFactory = DbConnectionFactory.CreateInMemory($"bmb_mobile_join_{Guid.NewGuid():N}");
            await new MigrationRunner(joinerFactory).RunMigrationsAsync();

            var client = new SnapshotJoinClient(
                _mobileHttp, joinerFactory, joinerDir, NullLogger<SnapshotJoinClient>.Instance);

            // The real production code under test: real HTTP challenge/authenticate handshake
            // (V2 domain tag, real Ed25519 signing) plus the real snapshot download + import.
            var (cpSeq, lamportTs) = await client.DownloadAndImportAsync(
                "http://localhost", joinerNodeId, privateKey, remotePublicKey);

            cpSeq.Should().BeGreaterThan(0);
            lamportTs.Should().BeGreaterOrEqualTo(0);

            using var conn = joinerFactory.CreateConnection();
            var importedTitle = await conn.QuerySingleAsync<string>(
                "SELECT title FROM tbl_article WHERE id = @Id", new { Id = articleId });
            importedTitle.Should().Be("Mobile-join article");

            // "the resulting node can then sync": prove the identity /api/join registered is
            // recognized for a SECOND, independent challenge/authenticate round, not just the
            // one-shot snapshot fetch — i.e. NodeA will keep authenticating this phone going
            // forward. (This second handshake targets the ALREADY-covered steady-state peer-auth
            // path, not SnapshotJoinClient itself — it's here only to prove the joined identity is
            // fully functional afterwards.)
            var token = await AuthenticateAsync(_mobileHttp, "http://localhost", joinerNodeId, privateKey);
            token.Should().NotBeNullOrEmpty();

            using var pullReq = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/sync/events?afterSequence=0");
            pullReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var pullResp = await _mobileHttp.SendAsync(pullReq);
            pullResp.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(joinerDir, true); } catch { }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> CreateArticleAsync(
        HttpClient client, string title, string treePath, string content = "test content")
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new
        {
            title,
            treePath,
            content
        });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private static async Task<string> AuthenticateAsync(
        HttpClient http, string remoteUrl, Guid nodeId, byte[] privateKey)
    {
        var challengeResp = await http.PostAsync($"{remoteUrl}/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challenge = await challengeResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var serverNodeId = challenge.GetProperty("serverNodeId").GetGuid();

        var challengeBytes = Convert.FromBase64String(challengeB64);
        var domainTag = "BMB-CHALLENGE-V2\0"u8.ToArray();
        var payload = domainTag.Concat(serverNodeId.ToByteArray()).Concat(challengeBytes).ToArray();
        var sig = Ed25519Signer.Sign(privateKey, payload);

        var authResp = await http.PostAsJsonAsync($"{remoteUrl}/api/sync/authenticate", new
        {
            NodeId = nodeId,
            ChallengeB64 = challengeB64,
            SignatureB64 = Convert.ToBase64String(sig)
        }, JsonOpts);
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return authData.GetProperty("token").GetString()!;
    }
}
