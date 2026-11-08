using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Which sync peers must present a pinned TLS key (plan 4.4): every active whitelist row in <c>pin</c> mode
/// (<see cref="BlindTrust.PinOf"/>: a pin that a <c>public-ca</c> or unknown mode left behind does not count), keyed by the host and port of its api_address. Consulted by the certificate
/// check of every outbound sync client, so the phone pins the blind node exactly as the PC that
/// paired it does.
///
/// <para>A peer without a pin — a hub on a public CA (ADR 0007), or any ordinary full node — is checked the
/// ordinary way, through <see cref="PublicCaTls"/>: the platform's chain, name included. <paramref name="anchors"/>
/// is for tests (a test CA's root as an extra anchor); a shipped build passes none. <paramref name="network"/> is for tests too (a name that resolves to
/// what the test says); a shipped build dials by <see cref="PeerNetwork.Production"/>.</para>
/// </summary>
public sealed class SpkiPinRegistry(IServiceScopeFactory scopeFactory, TlsTrustAnchors? anchors = null, PeerNetwork? network = null)
{
    // Re-read at most this often. A new pin arrives with a whitelist event and is needed on the
    // next cycle, not the next second; a stale cache only ever delays trusting a new key.
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A pin carried by the request itself — for the one moment no whitelist row has it yet: the
    /// PC talking to a blind node it is about to add, trusting only the key in the pair code.
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> ExplicitPin = new("bmb.tls-spki");

    /// <summary>
    /// The TLS anchor of the PEER a request is for (docs/design/lan-peer-sync.md sections 7 and 9): its pinned leaf keys, its local CA's key, and whether
    /// an ordinary CA-valid certificate is also acceptable. Set by the sync loop on every request of a session (<see cref="PeerAnchorStamp"/>), so an
    /// address learned from an announcement, an mDNS record or a person is held to the anchor of the peer it is dialled FOR - never to whatever row
    /// happens to have that host:port - and peer A's address is never accepted with peer B's key. It wins over <see cref="ExplicitPin"/> and the
    /// host:port lookup, which stays for the callers that dial a row's primary address without a peer context.
    /// </summary>
    public static readonly HttpRequestOptionsKey<PeerTlsAnchor> ExplicitAnchor = new("bmb.tls-anchor");

    private readonly object _gate = new();
    private Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedAt = DateTime.MinValue;

    /// <summary>The pin for this request's host:port, or null when the peer there is not pinned.</summary>
    public string? PinFor(Uri requestUri)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _loadedAt > MaxAge) Reload();
            return _pins.GetValueOrDefault(Key(requestUri));
        }
    }

    /// <summary>Forget the cache — after this node itself writes a pin (pairing, address change).</summary>
    public void Invalidate()
    {
        lock (_gate) _loadedAt = DateTime.MinValue;
    }

    /// <summary>
    /// The check for <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/>. A
    /// pinned peer is accepted on its key alone — its certificate is self-signed, so the chain and
    /// the name mean nothing — and refused on any other key, whatever a CA says about it. Every
    /// other host gets the ordinary validation.
    /// </summary>
    public bool Validate(HttpRequestMessage request, X509Certificate2? certificate, SslPolicyErrors errors, X509Chain? chain = null) =>
        Check(AnchorOf(request), certificate, errors, chain);

    /// <summary>The pin a request must meet: the one it carries itself, else its host:port's row; null = the ordinary check.</summary>
    internal string? PinOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(ExplicitPin, out var explicitPin) ? explicitPin
            : request.RequestUri is { } uri ? PinFor(uri) : null;

    /// <summary>
    /// The anchor a request must meet: the peer's own (<see cref="ExplicitAnchor"/>, with the public CAs only at the row's own host:
    /// <see cref="PeerTlsAnchor.AtHost"/>), else a pin as before; null = the ordinary check.
    /// </summary>
    internal PeerTlsAnchor? AnchorOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(ExplicitAnchor, out var anchor) && anchor is not null ? anchor.AtHost(request.RequestUri)
            : PinOf(request) is { } pin ? PeerTlsAnchor.PinOnly(pin) : null;

    private bool Check(PeerTlsAnchor? anchor, X509Certificate2? certificate, SslPolicyErrors errors, X509Chain? chain)
    {
        if (anchor is null) return PublicCaTls.IsValid(certificate, chain, errors, anchors);
        return anchor.Accepts(certificate, DateTime.UtcNow) || (anchor.AllowPublicCa && PublicCaTls.IsValid(certificate, chain, errors, anchors));
    }

    /// <summary>
    /// The primary handler for sync clients: ordinary TLS plus the pins above, never following a
    /// redirect (see <see cref="SpkiPinGuardHandler"/>), and a checked connect (<see cref="PeerSocketConnector"/>): the name is
    /// resolved here, every answer must be an address a node may call, and only those are connected to - so a name that
    /// resolves to a link-local or cloud-metadata address, or resolves differently the second time, never gets a connection.
    /// </summary>
    public HttpMessageHandler CreateHandler() => new SpkiPinGuardHandler(this, new PinLanes(this, network ?? PeerNetwork.Production));

    /// <summary>
    /// A sockets handler for one pin (null = the ordinary check). The certificate callback of a <see cref="SocketsHttpHandler"/>
    /// gets the <see cref="System.Net.Security.SslStream"/> as its sender, NOT the request (only HttpClientHandler's own callback is
    /// given the request), so a handler cannot learn the pin from the request: each handler is made for one pin instead.
    /// </summary>
    internal SocketsHttpHandler NewSocketsHandler(PeerNetwork net, string? pin) => NewSocketsHandlerFor(net, pin is null ? null : PeerTlsAnchor.PinOnly(pin));

    /// <summary>A sockets handler for one anchor (null = the ordinary check); see <see cref="NewSocketsHandler"/>.</summary>
    internal SocketsHttpHandler NewSocketsHandlerFor(PeerNetwork net, PeerTlsAnchor? anchor) => new()
    {
        // A redirect would take the request to a host the pin never covered — an unpinned host
        // with any CA-valid certificate passes the ordinary check (review L-stage1 #5).
        AllowAutoRedirect = false,
        // A proxy would resolve and connect on this node's behalf: the check below would judge the proxy, not the peer.
        UseProxy = false,
        ConnectCallback = (context, ct) => PeerSocketConnector.ConnectAsync(context, net, ct),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                Check(anchor, certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate)), errors, chain)
        }
    };

    /// <summary>
    /// Sends each request through the sockets handler made for ITS pin: one lane per host:port and pin (the request's own pin, else
    /// the row's, else none), so a pinned peer is only ever dialled by a handler whose certificate check is its pin, and a peer
    /// without one by a handler with the ordinary check. A changed pin is a new lane; the pins are few, so the lanes are too.
    /// </summary>
    private sealed class PinLanes(SpkiPinRegistry owner, PeerNetwork net) : HttpMessageHandler
    {
        // Ordinal: a pin is base64url, whose letter case is part of the key it names.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _lanes = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no address.");
            var anchor = owner.AnchorOf(request);
            // Lazy: two requests racing for one new lane build ONE handler (a second one would never be disposed).
            var lane = _lanes.GetOrAdd(LaneKeyFor(uri, anchor),
                _ => new Lazy<HttpMessageInvoker>(() => new HttpMessageInvoker(owner.NewSocketsHandlerFor(net, anchor), disposeHandler: true)));
            return lane.Value.SendAsync(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var lane in _lanes.Values.Where(l => l.IsValueCreated)) lane.Value.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>One lane per host:port (host in lower case, as <see cref="Uri.Host"/> gives it) and EXACT pin: a pin that differs in letter case is another key; no pin is not an empty pin.</summary>
    internal static string LaneKey(Uri uri, string? pin) => LaneKeyFor(uri, pin is null ? null : PeerTlsAnchor.PinOnly(pin));

    /// <summary>One lane per host:port and EXACT anchor (<see cref="PeerTlsAnchor.LaneKey"/>).</summary>
    internal static string LaneKeyFor(Uri uri, PeerTlsAnchor? anchor) => $"{uri.Host.ToLowerInvariant()}:{uri.Port}|{(anchor is null ? "-" : anchor.LaneKey)}";

    /// <summary>True if this request goes to a pinned peer (by the request's own pin or anchor, or the whitelist): it must be HTTPS. An anchor that also
    /// accepts an ordinary certificate (a peer on a public CA) is not "pinned": its plain-HTTP address of a test setup keeps working as before.</summary>
    public bool IsPinned(HttpRequestMessage request) =>
        AnchorOf(request) is { AllowPublicCa: false };

    private void Reload()
    {
        using var scope = scopeFactory.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>()
            .GetAllActiveAsync().GetAwaiter().GetResult();
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var pin = BlindTrust.PinOf(row.TlsTrust, row.TlsSpki);
            if (pin is null || string.IsNullOrEmpty(row.ApiAddress)) continue;
            if (Uri.TryCreate(row.ApiAddress, UriKind.Absolute, out var address))
                pins[Key(address)] = pin;
        }
        _pins = pins;
        _loadedAt = DateTime.UtcNow;
    }

    private static string Key(Uri uri) => $"{uri.Host}:{uri.Port}";
}
