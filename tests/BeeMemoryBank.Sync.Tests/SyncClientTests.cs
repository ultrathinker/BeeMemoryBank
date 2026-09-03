using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

public class SyncClientTests : IAsyncLifetime
{
    private SyncTestFixture _node = null!;
    private PeerNewerProtocolState _peerNewerProtocolState = null!;
    private SyncClient _client = null!;
    private MockHandler _mockHandler = null!;
    private HttpClient _http = null!;
    private Guid _remoteNodeId;

    public async Task InitializeAsync()
    {
        _node = new ConcreteFixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");

        // Create an article to ensure there is at least one local event to push.
        await _node.ArticleService.CreateAsync("Test Article", "/Root", new List<string>(), "content");

        _peerNewerProtocolState = new PeerNewerProtocolState();

        var syncPositionRepo = new SyncPositionRepository(_node.Factory);
        var pushPositionRepo = new SyncPushPositionRepository(_node.Factory);
        var authSigner = new SessionNodeAuthSigner(_node.Session);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance;

        _client = new SyncClient(
            _node.NodeRepo,
            _node.EventLogRepo,
            syncPositionRepo,
            pushPositionRepo,
            _node.EventApplier,
            _node.Session,
            authSigner,
            logger,
            _peerNewerProtocolState,
            _node.QuarantineRepo);

        _remoteNodeId = Guid.NewGuid();
        _mockHandler = new MockHandler();
        _http = new HttpClient(_mockHandler) { BaseAddress = new Uri("http://remote.local") };

        // Default mock routes
        _mockHandler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = _remoteNodeId
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/authenticate", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                token = "test-token"
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/events", req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }
            else // POST (push)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        applied = 1,
                        skipped = 0,
                        lastAppliedSequence = 1,
                        dropped = 0
                    }), Encoding.UTF8, "application/json")
                };
            }
        });
        _mockHandler.MapRoute("/api/sync/report-position", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _node.DisposeAsync();
    }

    [Fact]
    public async Task SyncWith_PeerEqualVersion_SyncsNormally()
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteEqual",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeFalse();
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("GET") && s.Contains("/api/sync/events"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/report-position"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/events")); // push
    }

    [Fact]
    public async Task SyncWith_PeerLowerVersion_SyncsNormally()
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteLower",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = 0
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeFalse();
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("GET") && s.Contains("/api/sync/events"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/report-position"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/events")); // push
    }

    [Fact]
    public async Task SyncWith_PeerHigherVersion_SkipsPull_PushesNormally()
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteHigher",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current + 1
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeTrue();
        _mockHandler.CallLog.Should().NotContain(s => s.StartsWith("GET") && s.Contains("/api/sync/events"));
        _mockHandler.CallLog.Should().NotContain(s => s.StartsWith("POST") && s.Contains("/api/sync/report-position"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/events")); // push still happens
    }

    [Fact]
    public async Task SyncWith_PeerHigherVersion_ThenPeerEqualVersion_ClearsFlag()
    {
        _peerNewerProtocolState.HasNewerProtocol = false;
        
        bool isHigher = true;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteNode",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = isHigher ? SyncProtocolVersion.Current + 1 : SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var result1 = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);
        result1.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeTrue();

        isHigher = false;

        var result2 = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);
        result2.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeFalse();
    }

    // ─── M6: caller-pinned audience anchor ─────────────────────────────────────

    [Fact]
    public async Task SyncWith_PeerDeclaresADifferentNodeIdThanWeDialed_RefusesToSync()
    {
        // We dial _remoteNodeId — in production, the whitelist entry SyncScheduler is iterating.
        // The peer's own /api/sync/identity response claims to be someone else: a stale/incorrect
        // ApiAddress entry, or a peer impersonating the node we meant to reach. SyncClient's
        // fast-fail check must refuse before ever touching the network for a challenge.
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = Guid.NewGuid(), // self-declared — deliberately != the pinned _remoteNodeId
                displayName = "Impersonator",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*but we dialed it as*");

        // Refused before ever fetching a challenge.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/challenge"));
    }

    [Fact]
    public async Task SyncWith_ChallengeClaimsForeignAudience_RefusesToSign_EvenWhenSelfDeclaredIdentityMatches()
    {
        // The actually security-relevant case (the M6 hole this fix closes): the peer's
        // self-declared /api/sync/identity AGREES with the id we dialed — so the fast-fail check
        // above does NOT catch it — but /api/sync/challenge claims a DIFFERENT ServerNodeId,
        // modeling a peer that relays a genuine challenge fetched live from some unrelated third
        // node. PeerAuthenticator must still refuse to sign, because the audience anchor is the
        // NodeId the CALLER dialed, never whatever this same connection claims about itself.
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId, // matches the pin — the shallow check alone would pass
                displayName = "SelfConsistentRelay",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var foreignNodeId = Guid.NewGuid();
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = foreignNodeId // relayed from an unrelated third node
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Challenge audience mismatch*");

        // Refused right after the challenge — never got as far as posting a signature.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/authenticate"));
    }

    [Fact]
    public async Task SyncWith_PeerChallengeOmitsServerNodeId_RefusesToSign_NoUnboundFallback()
    {
        // A challenge response with no ServerNodeId used to be read as "an old peer that predates
        // audience binding" and answered with an unbound V1 signature. That was a downgrade
        // anyone could trigger: the responding peer decides whether to send the field, so an
        // attacker only had to omit it, hand us a challenge fetched live from node C, and redeem
        // the resulting unbound signature at C — the exact relay attack the binding exists to
        // stop, reachable by deleting one JSON property. There is no fallback any more: a peer
        // that declares no audience gets no signature.
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "OldPeer",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            // No serverNodeId field at all (not even null — absent).
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32])
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no ServerNodeId*");

        // Never posted a signature of any kind.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/authenticate"));
    }

    private class ConcreteFixture : SyncTestFixture { }

    private class MockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<string> CallLog { get; } = new();

        public void MapRoute(string path, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _routes[path] = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No URI");
            CallLog.Add($"{request.Method} {uri.AbsolutePath}");

            foreach (var (path, handler) in _routes)
            {
                if (uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(handler(request));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
