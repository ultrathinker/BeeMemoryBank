using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// A hub on a home network, as a copy sees it (docs/design/lan-peer-sync.md section 16): ONE node with a key and a log of events, answering at any number of addresses, each address
/// in a mode a test sets and changes as the network changes (the hub is moved to another address, an address is taken over by another node). It stands at the bottom of an account's real
/// HTTP pipeline (<see cref="BlindAppOptions.PrimaryHandlerFactory"/>), so the account's own pull client, signer, applier, planner and dialler run for real. Every request is recorded with the
/// address it went to.
/// </summary>
internal sealed class HubNetwork : HttpMessageHandler
{
    /// <summary>The pairing address of a hub on a home Wi-Fi.</summary>
    public const string Pairing = "https://192.168.1.220:5311";

    public const string PairingAuthority = "192.168.1.220:5311";

    /// <summary>What an address does when it is called.</summary>
    public enum Mode
    {
        /// <summary>The hub answers everything.</summary>
        Hub,
        /// <summary>The hub's LAN sync surface: the sync routes only, the package route is a 404.</summary>
        SyncOnly,
        /// <summary>Nothing answers: the connection fails.</summary>
        Dead,
        /// <summary>Another node has this address: it answers as itself.</summary>
        OtherNode,
        /// <summary>Something that is not a sync server.</summary>
        NotSync,
    }

    public sealed record Call(string Authority, string Method, string Path, string Query);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly byte[] _privateKey;
    private readonly object _gate = new();
    private readonly List<SyncEvent> _events = [];
    private readonly List<Call> _calls = [];
    private readonly Dictionary<string, Mode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private long _lamport = 100;

    public HubNetwork()
    {
        (PublicKey, _privateKey) = Ed25519Signer.GenerateKeyPair();
        Pin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    public Guid NodeId { get; } = Guid.NewGuid();
    public byte[] PublicKey { get; }

    /// <summary>The pin of the hub's TLS key as the pairing carried it (the fake network does no TLS; a test that needs the key itself uses <see cref="TlsStub"/>).</summary>
    public string Pin { get; set; }

    public IReadOnlyList<Call> Calls { get { lock (_gate) return [.. _calls]; } }

    public IEnumerable<Call> CallsTo(string authority) => Calls.Where(c => string.Equals(c.Authority, authority, StringComparison.OrdinalIgnoreCase));

    public void Answer(string authority, Mode mode)
    {
        lock (_gate) _modes[authority] = mode;
    }

    /// <summary>The call code this hub gives an account whose pairing secret is <paramref name="pairingSecret"/>.</summary>
    public string CallCodeFor(byte[] pairingSecret, string address = Pairing) => BlindCallCode.Create(address, NodeId, Pin, PublicKey, pairingSecret).ToString();

    /// <summary>The whitelist row an account needs to accept this hub's events.</summary>
    public WhitelistEntry WhitelistRow() => new()
    {
        NodeId = NodeId, DisplayName = "Hub", Ed25519PublicKey = PublicKey, Status = "A", IsSuperadmin = true,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = NodeId,
    };

    public SyncEvent AddFolder(string name) => Add(EventTypes.FolderCreate,
        JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), "/" + name, name, "/", DateTime.UtcNow, DateTime.UtcNow)));

    /// <summary>The hub's signed record of the places it listens at (its own, as it signs it about itself).</summary>
    public SyncEvent AddNodeEndpoints(params string[] urls) => AddNodeEndpointsWithAnchors(Pin, null, urls);

    public SyncEvent AddNodeEndpointsWithAnchors(string? leafPin, string? caKey, params string[] urls) => Add(EventTypes.NodeEndpoints,
        JsonSerializer.Serialize(new NodeEndpointsPayload(NodeId.ToString(), ++_lamport, urls.Select(u => new NodeEndpointEntry(u, "lan")).ToList(), leafPin, caKey, 5311)));

    /// <summary>A record that says it is about <paramref name="about"/>, signed by the hub: a node speaking for another.</summary>
    public SyncEvent AddForgedNodeEndpoints(Guid about, string url) => Add(EventTypes.NodeEndpoints,
        JsonSerializer.Serialize(new NodeEndpointsPayload(about.ToString(), 1, [new NodeEndpointEntry(url, "lan")], null, null, 5311)));

    public SyncEvent Add(string type, string payload)
    {
        var evt = new SyncEvent
        {
            EventType = type, ProtocolVersion = 3, Payload = payload, EventId = Guid.NewGuid(), NodeId = NodeId, CreatedAt = DateTime.UtcNow,
        };
        lock (_gate)
        {
            evt.LamportTs = ++_lamport;
            evt.SequenceNum = _events.Count + 1;
            evt.Signature = Ed25519Signer.Sign(_privateKey, EventSignature.BuildPayload(evt));
            _events.Add(evt);
        }
        return evt;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
        Mode mode;
        lock (_gate)
        {
            _calls.Add(new Call(url.Authority, request.Method.Method, url.AbsolutePath, url.Query));
            mode = _modes.GetValueOrDefault(url.Authority, Mode.Dead);
        }

        switch (mode)
        {
            case Mode.Dead:
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            case Mode.NotSync:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            case Mode.OtherNode:
                return url.AbsolutePath == "/api/sync/identity"
                    ? Json(new { nodeId = Guid.NewGuid(), displayName = "Somebody else", ed25519PublicKeyB64 = Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey), protocolVersion = 3 })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        switch (url.AbsolutePath)
        {
            case "/api/sync/identity":
                return Json(new { nodeId = NodeId, displayName = "Hub", ed25519PublicKeyB64 = Convert.ToBase64String(PublicKey), protocolVersion = 3 });
            case "/api/sync/challenge":
                return Json(new { challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), serverNodeId = NodeId });
            case "/api/sync/authenticate":
                return Json(new { token = "token-" + Guid.NewGuid().ToString("N") });
            case "/api/sync/events":
            {
                var after = long.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["afterSequence"] ?? "0");
                List<SyncEvent> page;
                lock (_gate) page = _events.Where(e => e.SequenceNum > after).ToList();
                return Json(page);
            }
            case "/api/sync/report-position":
                return Json(new { ok = true });
            case "/api/sync/leave":
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "/api/blind/replica" when mode == Mode.Hub:
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Web), Encoding.UTF8, "application/json"),
    };
}

/// <summary>The handler an account's pipeline sits on; disposing it (a pin change, a wipe) leaves the network alone.</summary>
internal sealed class ForwardTo(HttpMessageHandler target) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _invoker.SendAsync(request, ct);
}

/// <summary>The networks this computer is on, as a test says.</summary>
internal sealed class FixedNetworks(params LocalSubnet[] subnets) : ILocalNetworks
{
    private IReadOnlyList<LocalSubnet> _subnets = subnets;

    public IReadOnlyList<LocalSubnet> Current() => _subnets;

    /// <summary>The computer is carried to other networks.</summary>
    public void MoveTo(IReadOnlyList<LocalSubnet> subnets) => _subnets = subnets;

    /// <summary>The home Wi-Fi of the tests: 192.168.1.0/24.</summary>
    public static FixedNetworks Home() => new(new LocalSubnet(IPAddress.Parse("192.168.1.42"), 24, false));

    /// <summary>Somewhere else: 10.20.30.0/24.</summary>
    public static FixedNetworks Elsewhere() => new(new LocalSubnet(IPAddress.Parse("10.20.30.5"), 24, false));
}

/// <summary>The host's mDNS browse, as a test says: what it hears now, and how many scans were made.</summary>
internal sealed class FakeLanBrowser : IBlindLanBrowser
{
    private readonly object _gate = new();
    private List<BlindLanSighting> _heard = [];
    private int _scans;

    public int Scans { get { lock (_gate) return _scans; } }

    public void Hear(params BlindLanSighting[] sightings)
    {
        lock (_gate) _heard = [.. sightings];
    }

    public Task<IReadOnlyList<BlindLanSighting>> BrowseAsync(TimeSpan scanTime, CancellationToken ct)
    {
        lock (_gate)
        {
            _scans++;
            return Task.FromResult<IReadOnlyList<BlindLanSighting>>([.. _heard]);
        }
    }
}

/// <summary>
/// A server that speaks TLS and a little HTTP/1.1 on a loopback port, with the certificate a test gives it: the real handshake, so that a key the client does not accept is refused where it is
/// refused in the app. Answers <c>GET /api/sync/identity</c> as the node it is told to be and every other path with 404.
/// </summary>
internal sealed class TlsStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly string _identity;
    private int _handshakes;
    private int _requests;

    public TlsStub(X509Certificate2 certificate, Guid nodeId, byte[] publicKey)
    {
        // A key that was never persisted cannot be used by SslStream on every platform: the certificate goes through a PFX.
        _certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        _identity = JsonSerializer.Serialize(new { nodeId, displayName = "Hub", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), protocolVersion = 3 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Authority => $"127.0.0.1:{Port}";

    public string Url => $"https://{Authority}";

    /// <summary>TLS handshakes that went through.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    /// <summary>HTTP requests that were read: a client that refused the certificate sent none.</summary>
    public int Requests => Volatile.Read(ref _requests);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _stop.Token);
                Interlocked.Increment(ref _handshakes);

                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await tls.ReadAsync(one, _stop.Token) == 0) return;
                    head.Append((char)one[0]);
                }
                Interlocked.Increment(ref _requests);
                var line = head.ToString().Split("\r\n")[0].Split(' ');
                var found = line.Length > 1 && line[1] == "/api/sync/identity";
                var body = Encoding.UTF8.GetBytes(found ? _identity : "{}");
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(response, _stop.Token);
                await tls.WriteAsync(body, _stop.Token);
                await tls.FlushAsync(_stop.Token);
            }
            catch (Exception)
            {
                // A client that refuses the certificate ends the handshake with an alert: the server sees a failed handshake, which is the point of the test.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _certificate.Dispose();
    }

    /// <summary>A self-signed certificate with a fresh P-256 key.</summary>
    public static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90));
    }

    /// <summary>A certificate authority: its certificate and its key.</summary>
    public static (X509Certificate2 Certificate, ECDsa Key) NewCa(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(10)), key);
    }

    /// <summary>A leaf with a NEW key, signed by <paramref name="ca"/>.</summary>
    public static X509Certificate2 LeafOf(X509Certificate2 ca)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var serial = RandomNumberGenerator.GetBytes(10);
        serial[0] &= 0x7F;
        return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90), serial).CopyWithPrivateKey(key);
    }
}
