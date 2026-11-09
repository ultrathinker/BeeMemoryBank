using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-337 through a whole session: the sync client with the hub, the standing it asks for afterwards, the key a device lacks for a blind node, and the next
/// dial of that node over a real TLS handshake with its self-signed certificate.
/// </summary>
public class PeerTlsBackfillSessionTests : SyncTestFixture, IDisposable
{
    private static readonly string OtherPin = Base64Url.EncodeToString(Enumerable.Repeat((byte)2, 32).ToArray());

    private readonly Guid _hubId = Guid.NewGuid();
    private readonly Guid _blind = BlindNodeId.NewId();
    private readonly byte[] _hubKey = Enumerable.Repeat((byte)7, 32).ToArray();
    private readonly List<TcpListener> _listeners = [];
    private readonly FakeHub _hub;
    private HttpClient _http = null!;

    public PeerTlsBackfillSessionTests() => _hub = new FakeHub(_hubId, _hubKey);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "Phone", "pass");
        await Session.UnlockAsync("pass");
        _http = new HttpClient(_hub);
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var l in _listeners) l.Stop();
    }

    private async Task AddAsync(Guid id, bool superadmin, byte[]? key = null, string? address = null, string? trust = null, string? spki = null)
    {
        var now = DateTime.UtcNow;
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "peer", Ed25519PublicKey = key ?? new byte[32], ApiAddress = address, IsSuperadmin = superadmin,
            TlsTrust = trust, TlsSpki = spki, Status = "A", CreatedAt = now, UpdatedAt = now,
        });
    }

    /// <summary>The client of a phone or a desktop: reachability (so that the standing is asked) and the backfill, over the real repositories.</summary>
    private SyncClient ClientWith(PeerTlsBackfill? backfill, IWhitelistRepository? whitelist = null) => new(
        NodeRepo, EventLogRepo, new SyncPositionRepository(Factory), new SyncPushPositionRepository(Factory), EventApplier, new SessionNodeAuthSigner(Session),
        NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(), QuarantineRepo, new BlobRepository(Factory),
        whitelistRepo: whitelist ?? WhitelistRepo, reachability: new PeerReachability(), tlsBackfill: backfill);

    private PeerTlsBackfill Backfill(SpkiPinRegistry? pins = null) =>
        new(WhitelistRepo, NullLogger<PeerTlsBackfill>.Instance, pins);

    // The answer of my-standing as a node writes it on the wire (snake_case), independent of the records this build reads it with.
    private static object PeerJson(Guid id, string? trust, string? spki, bool withTls = true) => withTls
        ? new { node_id = id, display_name = "p", last_protocol_version = 4, last_protocol_seen_at = (DateTime?)null, created_at = DateTime.UtcNow, tls_trust = trust, tls_spki = spki }
        : new { node_id = id, display_name = "p", last_protocol_version = 4, last_protocol_seen_at = (DateTime?)null, created_at = DateTime.UtcNow };

    private async Task<WhitelistEntry> RowAsync(Guid id) => (await WhitelistRepo.GetByNodeIdAsync(id, includeDeleted: true))!;

    [Fact]
    public async Task ASessionWithTheHub_GivesABlindPeerWithNoKey_ItsPin()
    {
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", OtherPin) } });

        await ClientWith(Backfill()).SyncWithAsync(_http, "http://hub.example", _hubId);

        _hub.StandingCalls.Should().Be(1);
        var row = await RowAsync(_blind);
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", OtherPin));
    }

    [Fact]
    public async Task AStandingThatNamesAnotherNode_IsNotTakenForTheHub()
    {
        // The node at the hub's address answers as a node that IS a superadmin here. The session was verified against the hub, so the answer is dropped.
        var other = Guid.NewGuid();
        await AddAsync(_hubId, superadmin: false, key: _hubKey);
        await AddAsync(other, superadmin: true);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = other, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", OtherPin) } });

        await ClientWith(Backfill()).SyncWithAsync(_http, "http://hub.example", _hubId);

        _hub.StandingCalls.Should().Be(1);
        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AHubThatIsNotSuperadminHere_GivesNothing()
    {
        await AddAsync(_hubId, superadmin: false, key: _hubKey);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", OtherPin) } });

        await ClientWith(Backfill()).SyncWithAsync(_http, "http://hub.example", _hubId);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task TheAnswerOfAnOlderHub_WithoutTheNewMembers_ChangesNothing_AndTheSessionGoesThrough()
    {
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, null, null, withTls: false) } });

        var act = () => ClientWith(Backfill()).SyncWithAsync(_http, "http://hub.example", _hubId);

        await act.Should().NotThrowAsync();
        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AWriteThatFails_DoesNotFailASessionThatWentThrough()
    {
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", OtherPin) } });
        var failing = new PeerTlsBackfill(FailingFill.Wrap(WhitelistRepo), NullLogger<PeerTlsBackfill>.Instance);

        var act = () => ClientWith(failing).SyncWithAsync(_http, "http://hub.example", _hubId);

        await act.Should().NotThrowAsync("the events were exchanged; the key is asked for again after the next standing");
        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task ASessionWithoutTheBackfill_AsABuiltByHandClientHasIt_LeavesTheRowAlone()
    {
        // The wiring is what makes the fill happen: a client built without it behaves as before.
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", OtherPin) } });

        await ClientWith(null).SyncWithAsync(_http, "http://hub.example", _hubId);

        (await RowAsync(_blind)).TlsSpki.Should().BeNull();
    }

    [Fact]
    public async Task AfterAStandingFillsTheKey_TheNextDialOfTheSelfSignedPeer_Succeeds()
    {
        var cert = SelfSigned();
        var port = Serve(cert);
        var address = $"https://127.0.0.1:{port}";
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false, address: address);

        var registry = new SpkiPinRegistry(new ServiceCollection().AddSingleton(WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        using var pinned = new HttpClient(registry.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        // Before: the row has neither mode nor key, so the certificate goes through the public CAs and is refused (the report of the phone). The registry has
        // now read the whitelist and kept "no pin" for this address.
        var before = () => pinned.GetStringAsync($"{address}/api/sync/identity");
        await before.Should().ThrowAsync<HttpRequestException>();

        // The standing round: the hub says how it reaches the blind node.
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", Spki.Of(cert)) } });
        await ClientWith(Backfill(registry)).SyncWithAsync(_http, "http://hub.example", _hubId);

        // After: the lookup by address (the registry's own cache was dropped) ...
        (await pinned.GetStringAsync($"{address}/api/sync/identity")).Should().Be("ok");
        // ... and the anchor of the peer's session, built from the row a cycle reads again (PeerSessions.PlanAsync).
        var row = await RowAsync(_blind);
        var anchor = PeerTlsAnchor.For(row, null);
        anchor.Should().NotBeNull();
        using var stamped = new HttpClient(new PeerAnchorStamp(anchor) { InnerHandler = registry.CreateHandler() }) { Timeout = TimeSpan.FromSeconds(15) };
        (await stamped.GetStringAsync($"{address}/api/sync/identity")).Should().Be("ok");
    }

    [Fact]
    public async Task AFilledKey_StillRefusesAnotherCertificate()
    {
        // The fill is a pin like any other: a node that presents another key is not the blind node.
        var cert = SelfSigned();
        var port = Serve(SelfSigned());
        var address = $"https://127.0.0.1:{port}";
        await AddAsync(_hubId, superadmin: true, key: _hubKey);
        await AddAsync(_blind, superadmin: false, address: address);
        _hub.Standing = () => FakeHub.Json(new { responder_node_id = _hubId, protocol = 4, caller_is_superadmin = true, reseed_needed = false, peers = new[] { PeerJson(_blind, "pin", Spki.Of(cert)) } });
        var registry = new SpkiPinRegistry(new ServiceCollection().AddSingleton(WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        await ClientWith(Backfill(registry)).SyncWithAsync(_http, "http://hub.example", _hubId);
        using var pinned = new HttpClient(registry.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        var act = () => pinned.GetStringAsync($"{address}/api/sync/identity");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>The hub as a node answers it over HTTP: identity, the challenge handshake, an empty log, and the standing the test sets.</summary>
    private sealed class FakeHub(Guid id, byte[] key) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Standing { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        public int StandingCalls;

        public static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sync/my-standing") Interlocked.Increment(ref StandingCalls);
            return Task.FromResult(path switch
            {
                "/api/sync/identity" => Json(new { nodeId = id, displayName = "Hub", ed25519PublicKeyB64 = Convert.ToBase64String(key), protocolVersion = SyncProtocolVersion.Current }),
                "/api/sync/challenge" => Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id }),
                "/api/sync/authenticate" => Json(new { token = "test-token" }),
                "/api/sync/events" when request.Method == HttpMethod.Get => Json(Array.Empty<object>()),
                "/api/sync/events" => Json(new { applied = 0, skipped = 0, dropped = 0 }),
                "/api/sync/report-position" => new HttpResponseMessage(HttpStatusCode.OK),
                "/api/sync/my-standing" => Standing(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    /// <summary>The whitelist, but the fill of a TLS key fails.</summary>
    public class FailingFill : DispatchProxy
    {
        private IWhitelistRepository _inner = null!;

        public static IWhitelistRepository Wrap(IWhitelistRepository inner)
        {
            var proxy = Create<IWhitelistRepository, FailingFill>();
            ((FailingFill)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IWhitelistRepository.FillMissingTlsTrustAsync)
                ? throw new InvalidOperationException("The database is busy.")
                : method.Invoke(_inner, args);
    }

    /// <summary>A loopback TLS server: answers every request 200 "ok" once the handshake went through.</summary>
    private int Serve(X509Certificate2 cert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(cert);
                            var buffer = new byte[4096];
                            while (await tls.ReadAsync(buffer) > 0)
                                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                        }
                        catch (Exception) { }
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
