using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Which sync peers must present a pinned TLS key (plan 4.4): every active whitelist row with a
/// <c>tls_spki</c>, keyed by the host and port of its api_address. Consulted by the certificate
/// check of every outbound sync client, so the phone pins the blind node exactly as the PC that
/// paired it does.
/// </summary>
public sealed class SpkiPinRegistry(IServiceScopeFactory scopeFactory)
{
    // Re-read at most this often. A new pin arrives with a whitelist event and is needed on the
    // next cycle, not the next second; a stale cache only ever delays trusting a new key.
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A pin carried by the request itself — for the one moment no whitelist row has it yet: the
    /// PC talking to a blind node it is about to add, trusting only the key in the pair code.
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> ExplicitPin = new("bmb.tls-spki");

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
    public bool Validate(HttpRequestMessage request, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        var pin = request.Options.TryGetValue(ExplicitPin, out var explicitPin) ? explicitPin
            : request.RequestUri is { } uri ? PinFor(uri) : null;
        if (pin is null) return errors == SslPolicyErrors.None;
        return certificate is not null && Spki.Equal(Spki.Of(certificate), pin);
    }

    /// <summary>
    /// The primary handler for sync clients: ordinary TLS plus the pins above, never following a
    /// redirect (see <see cref="SpkiPinGuardHandler"/>).
    /// </summary>
    public HttpMessageHandler CreateHandler() => new SpkiPinGuardHandler(this, new HttpClientHandler
    {
        // A redirect would take the request to a host the pin never covered — an unpinned host
        // with any CA-valid certificate passes the ordinary check (review L-stage1 #5).
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            Validate(request, certificate, errors)
    });

    /// <summary>True if this request goes to a pinned peer (by the request's own pin or the whitelist).</summary>
    public bool IsPinned(HttpRequestMessage request) =>
        request.Options.TryGetValue(ExplicitPin, out _) || (request.RequestUri is { } uri && PinFor(uri) is not null);

    private void Reload()
    {
        using var scope = scopeFactory.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>()
            .GetAllActiveAsync().GetAwaiter().GetResult();
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.TlsSpki) || string.IsNullOrEmpty(row.ApiAddress)) continue;
            if (Uri.TryCreate(row.ApiAddress, UriKind.Absolute, out var address))
                pins[Key(address)] = row.TlsSpki;
        }
        _pins = pins;
        _loadedAt = DateTime.UtcNow;
    }

    private static string Key(Uri uri) => $"{uri.Host}:{uri.Port}";
}
