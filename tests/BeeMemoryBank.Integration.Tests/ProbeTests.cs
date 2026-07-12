using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Integration tests for the reachability self-test flow:
///   POST /api/sync/probe        (local, internal-key-gated) → picks a peer, asks it to relay
///   POST /api/sync/probe-relay  (peer-to-peer, Bearer auth) → fetches {url}/api/sync/ping
///
/// Two real in-process nodes (WebApplicationFactory / TestServer) are set up and mutually
/// whitelisted. A test routing handler steers each node's outbound HttpClient to the OTHER
/// node's TestServer, so the probe→relay round-trip runs end-to-end without real TCP ports.
/// </summary>
public class ProbeTests : IAsyncLifetime
{
    private const string MasterPassword = "sharedPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private ProbeWebAppFactory _nodeA = null!;
    private ProbeWebAppFactory _nodeB = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;
    private Guid _nodeAId;
    private Guid _nodeBId;

    private readonly TestRoutingHandler _routeA = new();
    private readonly TestRoutingHandler _routeB = new();

    public async Task InitializeAsync()
    {
        _nodeA = new ProbeWebAppFactory(_routeA);
        _nodeB = new ProbeWebAppFactory(_routeB);

        // NodeA is the primary node.
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        await UnlockAsync(_clientA, MasterPassword);

        // NodeB joins NodeA's network — both now share the same Master DEK + mutual whitelist.
        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _nodeB.CreateClient();
        await UnlockAsync(_clientB, MasterPassword);

        _nodeAId = await GetNodeIdAsync(_clientA);
        _nodeBId = await GetNodeIdAsync(_clientB);

        // The join process leaves ApiAddress null — set mutual addresses so probe can find peers.
        // The host names are arbitrary; the test routing handler maps them to the right TestServer.
        await SetApiAddressAsync(_nodeA.Services, _nodeBId, "http://node-b");
        await SetApiAddressAsync(_nodeB.Services, _nodeAId, "http://node-a");

        // Wire routing AFTER both TestServers exist:
        //   Node A's outbound → Node B ; Node B's outbound → Node A.
        _routeA.Route("node-b", _nodeB.Server!.CreateHandler());
        _routeB.Route("node-a", _nodeA.Server!.CreateHandler());
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _nodeA.Dispose();
        _nodeB.Dispose();
        return Task.CompletedTask;
    }

    // ─── Tests ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Full flow: NodeA probes its own candidate URL. NodeA authenticates to NodeB,
    /// NodeB relays a fetch to NodeA's /api/sync/ping (gets 403 — no internal key on
    /// the bare relay fetch, but ANY http response proves reachability).
    /// </summary>
    [Fact]
    public async Task Probe_ReachableUrl_ReportsReachable()
    {
        var resp = await _clientA.PostAsJsonAsync("/api/sync/probe", new { url = "http://node-a" });
        resp.IsSuccessStatusCode.Should().BeTrue();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        body.GetProperty("outcome").GetString().Should().Be("Reachable");
        body.GetProperty("peerNodeId").GetString().Should().Be(_nodeBId.ToString());
        body.GetProperty("peerDisplayName").GetString().Should().Be("NodeB");
        body.GetProperty("targetHttpStatusCode").GetInt32().Should().Be(403);
        body.GetProperty("errorCategory").GetString().Should().Be("None");
    }

    /// <summary>
    /// NodeA probes a genuinely unreachable URL (loopback port 1 — nothing listens).
    /// NodeB's relay fetch fails with connection-refused → outcome Unreachable, which
    /// is the signal a later wizard uses to suggest a CGNAT diagnosis.
    /// </summary>
    [Fact]
    public async Task Probe_UnreachableUrl_ReportsUnreachable()
    {
        var resp = await _clientA.PostAsJsonAsync("/api/sync/probe", new { url = "http://127.0.0.1:1" });
        resp.IsSuccessStatusCode.Should().BeTrue();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        body.GetProperty("outcome").GetString().Should().Be("Unreachable");
        body.GetProperty("errorCategory").GetString().Should().Be("ConnectionRefused");
        body.GetProperty("peerNodeId").GetString().Should().Be(_nodeBId.ToString());
        body.GetProperty("message").GetString().Should().Contain("CGNAT");
    }

    /// <summary>
    /// A standalone node with no whitelisted peers (that have an ApiAddress) reports
    /// NoPeersAvailable — the wizard should then suggest a manual check from a phone.
    /// </summary>
    [Fact]
    public async Task Probe_NoPeersWithAddress_ReportsNoPeersAvailable()
    {
        using var standalone = new ProbeWebAppFactory(new TestRoutingHandler());
        await standalone.InitializeNodeAsync("Lonely", MasterPassword);
        var client = standalone.CreateClient();
        await UnlockAsync(client, MasterPassword);

        var resp = await client.PostAsJsonAsync("/api/sync/probe", new { url = "http://example.com" });
        resp.IsSuccessStatusCode.Should().BeTrue();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        body.GetProperty("outcome").GetString().Should().Be("NoPeersAvailable");
        body.GetProperty("peerNodeId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("message").GetString().Should().Contain("phone");
    }

    /// <summary>
    /// The relay endpoint is peer-to-peer (Bearer auth) — calling it without a token
    /// must be rejected with 401.
    /// </summary>
    [Fact]
    public async Task ProbeRelay_WithoutBearerToken_Returns401()
    {
        var resp = await _clientB.PostAsJsonAsync("/api/sync/probe-relay", new { url = "http://example.com" });
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Invalid URL (not http/https) is rejected at the probe endpoint with a 400 + InvalidUrl.
    /// </summary>
    [Fact]
    public async Task Probe_InvalidUrl_Returns400()
    {
        var resp = await _clientA.PostAsJsonAsync("/api/sync/probe", new { url = "not-a-url" });
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("outcome").GetString().Should().Be("InvalidUrl");
    }

    /// <summary>
    /// Invalid URL on the relay endpoint returns 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task ProbeRelay_WithBearerToken_InvalidUrl_Returns400()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/probe-relay");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new { url = "ftp://bad" });
        var resp = await _clientA.SendAsync(req);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> GetNodeIdAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/sync/identity");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return Guid.Parse(body.GetProperty("nodeId").GetString()!);
    }

    private static async Task SetApiAddressAsync(IServiceProvider services, Guid peerNodeId, string apiAddress)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var entry = await repo.GetByNodeIdAsync(peerNodeId);
        if (entry != null)
        {
            entry.ApiAddress = apiAddress;
            await repo.UpdateAsync(entry);
        }
    }

    /// <summary>
    /// Authenticates <paramref name="clientNode"/> on <paramref name="server"/> using the
    /// standard challenge/sign/authenticate flow, returning a Bearer token.
    /// </summary>
    private static async Task<string> AuthNodeOnServerAsync(ProbeWebAppFactory clientNode, HttpClient server)
    {
        var challengeResp = await server.PostAsync("/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var challengeB64 = challengeData.GetProperty("challenge").GetString()!;

        using var scope = clientNode.Services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException();
        var session = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
        var masterDek = session.GetMasterDek();
        byte[] signature;
        try
        {
            signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, masterDek,
                "BMB-CHALLENGE-V1\0"u8.ToArray().Concat(Convert.FromBase64String(challengeB64)).ToArray());
        }
        finally { Array.Clear(masterDek); }

        var authResp = await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeB64,
            SignatureB64 = Convert.ToBase64String(signature)
        });
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return authData.GetProperty("token").GetString()!;
    }

    // ─── Test infrastructure ───────────────────────────────────────────────

    /// <summary>
    /// Routes requests for specific hosts to a target handler (wrapped in an HttpClient),
    /// and falls through to a real <see cref="SocketsHttpHandler"/> for unrouted hosts. This
    /// lets the probe/relay round-trip run between two in-process TestServers, while still
    /// allowing genuine connection failures for "unreachable" test URLs.
    ///
    /// Requests must be cloned before forwarding because the outer <c>HttpClient</c> pipeline
    /// marks the original request as "already sent" — re-sending it through an inner client
    /// would throw <c>InvalidOperationException</c>.
    /// </summary>
    private sealed class TestRoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpClient> _routes = new();
        private readonly HttpClient _fallback = new(new SocketsHttpHandler(), disposeHandler: true);

        public void Route(string host, HttpMessageHandler target)
        {
            _routes[host] = new HttpClient(target, disposeHandler: false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host;
            var client = (host != null && _routes.TryGetValue(host, out var c)) ? c : _fallback;
            var clone = await CloneRequestAsync(request, cancellationToken);
            return await client.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fallback.Dispose();
                foreach (var c in _routes.Values)
                    try { c.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(
            HttpRequestMessage original, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(original.Method, original.RequestUri)
            {
                Version = original.Version,
            };

            foreach (var (key, values) in original.Headers)
                clone.Headers.TryAddWithoutValidation(key, values.ToArray());

            if (original.Content != null)
            {
                var bytes = await original.Content.ReadAsByteArrayAsync(cancellationToken);
                clone.Content = new ByteArrayContent(bytes);
                foreach (var (key, values) in original.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(key, values.ToArray());
            }

            return clone;
        }
    }

    /// <summary>
    /// <see cref="BmbWebApplicationFactory"/> subclass that wires all unnamed
    /// <c>IHttpClientFactory</c> clients through a test routing handler, so probe/relay
    /// outbound calls can be steered to a specific peer's TestServer.
    /// </summary>
    private sealed class ProbeWebAppFactory : BmbWebApplicationFactory
    {
        private readonly HttpMessageHandler _outboundHandler;

        public ProbeWebAppFactory(HttpMessageHandler outboundHandler)
        {
            _outboundHandler = outboundHandler;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(string.Empty)
                    .ConfigurePrimaryHttpMessageHandler(() => _outboundHandler);
            });
        }
    }
}
