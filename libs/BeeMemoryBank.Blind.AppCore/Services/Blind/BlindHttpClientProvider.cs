using System.Security.Authentication;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Manages HttpClient and primary HttpMessageHandler instances for the Android blind node.
/// Ensures that:
/// 1. A dedicated HttpMessageHandler and connection pool is used per trust target: one SPKI pin, or one public-CA
///    origin (ADR 0007).
/// 2. When the phone re-pairs with a new SPKI pin or changes target, the previous handler and
///    its pooled connections are disposed so no authenticated connection under an old pin
///    can be reused across pairing changes.
/// 3. Implements <see cref="IHttpClientFactory"/> so all HTTP calls throughout the app route
///    through the trust-isolated client.
/// </summary>
public sealed class BlindHttpClientProvider : IHttpClientFactory, IDisposable
{
    private readonly object _gate = new();
    private readonly BlindPhoneState _state;
    private readonly TlsTrustAnchors? _anchors;
    private readonly Func<HttpMessageHandler>? _primaryOverride;
    private string? _currentKey;
    private HttpMessageHandler? _currentHandler;
    private HttpMessageHandler? _anchoredHandler;

    private readonly Action<string>? _onPinRefused;
    private readonly Action<string>? _onCertificateRefused;

    /// <param name="onPinRefused">Told the host:port of a node that presented another key than the pinned one.</param>
    /// <param name="onCertificateRefused">Told the host:port of a public-CA node whose certificate the system's chain refused.</param>
    /// <param name="anchors">Extra roots a test supplies for the public-CA mode; every app passes none.</param>
    /// <param name="primaryHandlerOverride">Tests only: the handler at the bottom of the pipeline, in place of the pinned or public-CA socket handler.</param>
    public BlindHttpClientProvider(BlindPhoneState state, Action<string>? onPinRefused = null,
        Action<string>? onCertificateRefused = null, TlsTrustAnchors? anchors = null,
        Func<HttpMessageHandler>? primaryHandlerOverride = null)
    {
        _state = state;
        _onPinRefused = onPinRefused;
        _onCertificateRefused = onCertificateRefused;
        _anchors = anchors;
        _primaryOverride = primaryHandlerOverride;
    }

    /// <summary>
    /// What counts and guards the account's requests: when set, every client this provider hands out has the metering handler in its pipeline. An init property (the heads never
    /// call this constructor, tests do, and a new parameter type would show in a signature).
    /// </summary>
    public BlindMeteringTap? Metering { get; init; }

    /// <summary>
    /// How this copy reaches its hub over the hub's channels (<see cref="BlindHubChannels"/>): the pairing address, what the hub announced, what mDNS saw. Null (a composition that
    /// has none) is the pairing address through <see cref="GetClient(BlindCallCode, bool)"/> alone. An init property, like <see cref="Metering"/>.
    /// </summary>
    public BlindHubChannels? Hub { get; init; }

    /// <summary>
    /// The checks of a TLS key against a peer's anchor (<see cref="SpkiPinRegistry"/>), for the clients of a session over the hub's channels: whichever address is dialled, the request
    /// carries the hub's anchor, and the certificate must be one of its pinned keys or a leaf of its local CA. Without it (a composition with no hub channels) there is no such client.
    /// </summary>
    public SpkiPinRegistry? Pins { get; init; }

    /// <summary>
    /// Runs <paramref name="work"/> with the client and the call code of one call to the hub. With <see cref="Hub"/> they are the hub's first channel that answers (the call code then
    /// holds that address for this call only; the pairing's own is never changed); without it, the client <see cref="GetClient(BlindCallCode, bool)"/> gives and the call code as it is.
    /// </summary>
    /// <param name="kind">What the call is for (the order of the hub's addresses depends on it).</param>
    /// <param name="exempt">The call counts and is never blocked (see <see cref="GetClient(BlindCallCode, bool)"/>).</param>
    public async Task<T> WithClientAsync<T>(BlindCallCode target, BlindHubCall kind, bool exempt, Func<HttpClient, BlindCallCode, Task<T>> work, CancellationToken ct)
    {
        if (Hub is { } hub) return await hub.RunAsync(this, target, kind, exempt, work, ct).ConfigureAwait(false);
        using var client = GetClient(target, exempt);
        return await work(client, target).ConfigureAwait(false);
    }

    /// <summary>The same for a call that returns nothing.</summary>
    public Task WithClientAsync(BlindCallCode target, BlindHubCall kind, bool exempt, Func<HttpClient, BlindCallCode, Task> work, CancellationToken ct) =>
        WithClientAsync<int>(target, kind, exempt, async (client, call) =>
        {
            await work(client, call).ConfigureAwait(false);
            return 0;
        }, ct);

    /// <summary>
    /// The pipeline of a client that is stamped with a peer's anchor on every request (<see cref="PeerAnchorStamp"/>): the maintenance and https checks, no redirect, and the same counting
    /// and limits as <see cref="GetClient(BlindCallCode, bool)"/>; at the bottom the handler of <see cref="Pins"/>, which accepts only what the request's anchor accepts. The handler at the bottom
    /// is shared (its connections are kept per address and anchor) and is not disposed with the client.
    /// </summary>
    internal HttpMessageHandler CreateAnchoredHandler(bool exempt)
    {
        HttpMessageHandler primary;
        lock (_gate)
        {
            _anchoredHandler ??= _primaryOverride?.Invoke()
                ?? (Pins ?? throw new InvalidOperationException("This composition has no checks for a peer's anchor.")).CreateHandler();
            primary = _anchoredHandler;
        }

        HttpMessageHandler inner = new PinRefusalReporter(_onPinRefused) { InnerHandler = new NonDisposingDelegatingHandler(primary) };
        if (Metering is { } tap) inner = new BlindMeteringHandler(tap, exempt) { InnerHandler = inner };
        return new MaintenanceDetectingHandler { InnerHandler = new BlindHttpHandler(inner, onlyAuthority: null) };
    }

    /// <summary>
    /// Gets or creates a fresh disposable HttpClient bound to the specified or active CallCode SPKI pin.
    /// Reuses connections and primary socket pool within the same pin, but disposes the connection pool
    /// whenever the pin changes or Invalidate is called. Each call returns an independent client wrapper
    /// so caller disposal is safe and does not tear down the provider's connection pool. Without an explicit pin, an
    /// active call code for a public-CA node gets the client <see cref="GetClient(BlindCallCode)"/> would.
    /// </summary>
    public HttpClient GetClient(string? explicitPin = null)
    {
        if (explicitPin is null && _state.CallCode is { Trust: BlindTrust.PublicCa } publicCa)
            return GetClient(publicCa);

        var targetPin = explicitPin ?? _state.CallCode?.SpkiPin;
        lock (_gate)
        {
            return Build("pin:" + targetPin, () => BlindHttpHandler.CreatePrimaryHandler(targetPin, _onPinRefused),
                onlyAuthority: null, alwaysFresh: string.IsNullOrWhiteSpace(targetPin));
        }
    }

    /// <summary>
    /// The client for the node a call code names, by the trust it names: the pinned handler for a pinned node, and for a
    /// node on a public CA the system's certificate validation (host name included), https only, no redirect, and no
    /// request to any other origin.
    /// </summary>
    public HttpClient GetClient(BlindCallCode target) => GetClient(target, exempt: false);

    /// <summary>
    /// The client for the node a call code names, as <see cref="GetClient(BlindCallCode)"/>; <paramref name="exempt"/> makes its pipeline COUNT without ever BLOCKING (the paused report to the
    /// source must go out whatever the limits say). The connection pool is the same.
    /// </summary>
    public HttpClient GetClient(BlindCallCode target, bool exempt)
    {
        if (target.Trust != BlindTrust.PublicCa)
        {
            lock (_gate)
            {
                return Build("pin:" + target.SpkiPin, () => BlindHttpHandler.CreatePrimaryHandler(target.SpkiPin, _onPinRefused),
                    onlyAuthority: null, alwaysFresh: string.IsNullOrWhiteSpace(target.SpkiPin), exempt);
            }
        }

        var authority = new Uri(target.Address).Authority;
        lock (_gate)
        {
            return Build("public-ca:" + authority, () => BlindHttpHandler.CreatePublicCaHandler(_onCertificateRefused, _anchors),
                onlyAuthority: authority, alwaysFresh: false, exempt);
        }
    }

    public HttpClient CreateClient(string name) => GetClient();

    private HttpClient Build(string key, Func<HttpClientHandler> createHandler, string? onlyAuthority, bool alwaysFresh, bool exempt = false)
    {
        if (_currentHandler == null || _currentKey != key || alwaysFresh)
        {
            DisposeCurrent();
            _currentKey = key;
            _currentHandler = _primaryOverride?.Invoke() ?? createHandler();
        }

        var nonDisposing = new NonDisposingDelegatingHandler(_currentHandler);
        // The metering handler is the one every request of the copy passes: it counts what goes out and comes in, and asks the guard (not for an exempt client).
        HttpMessageHandler inner = nonDisposing;
        if (Metering is { } tap) inner = new BlindMeteringHandler(tap, exempt) { InnerHandler = nonDisposing };
        var blindHandler = new BlindHttpHandler(inner, onlyAuthority);
        var maintenanceHandler = new MaintenanceDetectingHandler { InnerHandler = blindHandler };
        return new HttpClient(maintenanceHandler, disposeHandler: true);
    }

    /// <summary>
    /// Invalidates and disposes the current handler and pooled connections.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            DisposeCurrent();
            _currentKey = null;
            _anchoredHandler?.Dispose();
            _anchoredHandler = null;
        }
    }

    private void DisposeCurrent()
    {
        if (_currentHandler != null)
        {
            _currentHandler.Dispose();
            _currentHandler = null;
        }
    }

    public void Dispose() => Invalidate();

    /// <summary>Tells when a peer answered with another key than its anchor accepts, as the pinned handler does for the pairing address: the stack alone says "connection failure", as for a node that is off.</summary>
    private sealed class PinRefusalReporter(Action<string>? onRefused) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && request.RequestUri is { } uri)
            {
                onRefused?.Invoke(uri.Authority);
                throw;
            }
        }
    }

    private sealed class NonDisposingDelegatingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override void Dispose(bool disposing)
        {
            // Intentionally do not dispose the inner handler so the connection pool is retained by BlindHttpClientProvider.
        }
    }
}
