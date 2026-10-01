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
    private HttpClient? _currentClient;

    public BlindHttpClientProvider(BlindPhoneState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets or creates an HttpClient bound to the specified or active CallCode SPKI pin.
    /// Reuses connections within the same pin, but disposes the connection pool whenever the pin changes.
    /// </summary>
    public HttpClient GetClient(string? explicitPin = null)
    {
        lock (_gate)
        {
            var targetPin = explicitPin ?? _state.CallCode?.SpkiPin;
            if (_currentClient != null && _currentPin == targetPin && !string.IsNullOrWhiteSpace(targetPin))
            {
                return _currentClient;
            }

            DisposeCurrent();

            _currentPin = targetPin;
            _currentHandler = BlindHttpHandler.CreatePrimaryHandler(targetPin);
            var blindHandler = new BlindHttpHandler(_currentHandler);
            var maintenanceHandler = new MaintenanceDetectingHandler { InnerHandler = blindHandler };
            _currentClient = new HttpClient(maintenanceHandler, disposeHandler: true);
            return _currentClient;
        }
    }

    public HttpClient CreateClient(string name) => GetClient();

    /// <summary>
    /// Invalidates and disposes the current client and handler, closing all pooled connections.
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
        if (_currentClient != null)
        {
            _currentClient.Dispose();
            _currentClient = null;
        }
        if (_currentHandler != null)
        {
            _currentHandler.Dispose();
            _currentHandler = null;
        }
    }

    public void Dispose() => Invalidate();
}
