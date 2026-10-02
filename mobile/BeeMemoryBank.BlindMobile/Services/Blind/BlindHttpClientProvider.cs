using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Manages HttpClient and primary HttpMessageHandler instances for the Android blind node.
/// Ensures that:
/// 1. A dedicated HttpMessageHandler and connection pool is used per target SPKI pin.
/// 2. When the phone re-pairs with a new SPKI pin or changes target, the previous handler and
///    its pooled connections are disposed so no authenticated connection under an old pin
///    can be reused across pairing changes.
/// 3. Implements <see cref="IHttpClientFactory"/> so all HTTP calls throughout the app route
///    through the pin-isolated client.
/// </summary>
public sealed class BlindHttpClientProvider : IHttpClientFactory, IDisposable
{
    private readonly object _gate = new();
    private readonly BlindPhoneState _state;
    private string? _currentPin;
    private HttpClientHandler? _currentHandler;

    private readonly Action<string>? _onPinRefused;

    /// <param name="onPinRefused">Told the host:port of a node that presented another key than the pinned one.</param>
    public BlindHttpClientProvider(BlindPhoneState state, Action<string>? onPinRefused = null)
    {
        _state = state;
        _onPinRefused = onPinRefused;
    }

    /// <summary>
    /// Gets or creates a fresh disposable HttpClient bound to the specified or active CallCode SPKI pin.
    /// Reuses connections and primary socket pool within the same pin, but disposes the connection pool
    /// whenever the pin changes or Invalidate is called. Each call returns an independent client wrapper
    /// so caller disposal is safe and does not tear down the provider's connection pool.
    /// </summary>
    public HttpClient GetClient(string? explicitPin = null)
    {
        lock (_gate)
        {
            var targetPin = explicitPin ?? _state.CallCode?.SpkiPin;
            if (_currentHandler == null || _currentPin != targetPin || string.IsNullOrWhiteSpace(targetPin))
            {
                DisposeCurrent();
                _currentPin = targetPin;
                _currentHandler = BlindHttpHandler.CreatePrimaryHandler(targetPin, _onPinRefused);
            }

            var nonDisposing = new NonDisposingDelegatingHandler(_currentHandler);
            var blindHandler = new BlindHttpHandler(nonDisposing);
            var maintenanceHandler = new MaintenanceDetectingHandler { InnerHandler = blindHandler };
            return new HttpClient(maintenanceHandler, disposeHandler: true);
        }
    }

    public HttpClient CreateClient(string name) => GetClient();

    /// <summary>
    /// Invalidates and disposes the current handler and pooled connections.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            DisposeCurrent();
            _currentPin = null;
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
