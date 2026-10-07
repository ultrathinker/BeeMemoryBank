using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

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
    private string? _currentKey;
    private HttpClientHandler? _currentHandler;

    private readonly Action<string>? _onPinRefused;
    private readonly Action<string>? _onCertificateRefused;

    /// <param name="onPinRefused">Told the host:port of a node that presented another key than the pinned one.</param>
    /// <param name="onCertificateRefused">Told the host:port of a public-CA node whose certificate the system's chain refused.</param>
    /// <param name="anchors">Extra roots a test supplies for the public-CA mode; every app passes none.</param>
    public BlindHttpClientProvider(BlindPhoneState state, Action<string>? onPinRefused = null,
        Action<string>? onCertificateRefused = null, TlsTrustAnchors? anchors = null)
    {
        _state = state;
        _onPinRefused = onPinRefused;
        _onCertificateRefused = onCertificateRefused;
        _anchors = anchors;
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
    public HttpClient GetClient(BlindCallCode target)
    {
        if (target.Trust != BlindTrust.PublicCa) return GetClient(target.SpkiPin);

        var authority = new Uri(target.Address).Authority;
        lock (_gate)
        {
            return Build("public-ca:" + authority, () => BlindHttpHandler.CreatePublicCaHandler(_onCertificateRefused, _anchors),
                onlyAuthority: authority, alwaysFresh: false);
        }
    }

    public HttpClient CreateClient(string name) => GetClient();

    private HttpClient Build(string key, Func<HttpClientHandler> createHandler, string? onlyAuthority, bool alwaysFresh)
    {
        if (_currentHandler == null || _currentKey != key || alwaysFresh)
        {
            DisposeCurrent();
            _currentKey = key;
            _currentHandler = createHandler();
        }

        var nonDisposing = new NonDisposingDelegatingHandler(_currentHandler);
        var blindHandler = new BlindHttpHandler(nonDisposing, onlyAuthority);
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

    private sealed class NonDisposingDelegatingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override void Dispose(bool disposing)
        {
            // Intentionally do not dispose the inner handler so the connection pool is retained by BlindHttpClientProvider.
        }
    }
}
