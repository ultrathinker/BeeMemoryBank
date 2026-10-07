using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// HTTP message handler and client configuration for the Android blind node.
/// Enforces:
/// 1. Denying cleartext (HTTPS-only) in the client path.
/// 2. Refusing redirects (3xx responses are treated as fatal errors, not followed).
/// 3. The trust the paired node's call code names (<see cref="BlindTrust"/>): SPKI public key pinning for a pinned
///    node, or the normal certificate chain — with the host name — for a node on a public CA, whose client may
///    reach nothing but that node's own origin (the chain alone would accept any site a CA vouches for).
/// </summary>
public sealed class BlindHttpHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> ExplicitPin = new("bmb.tls-spki");

    private readonly string? _onlyAuthority;

    public BlindHttpHandler()
    {
    }

    public BlindHttpHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    /// <param name="onlyAuthority">When set (host[:port], as <see cref="Uri.Authority"/> spells it), the only authority this
    /// client sends to: a request for any other host is refused before a connection is made.</param>
    public BlindHttpHandler(HttpMessageHandler inner, string? onlyAuthority) : base(inner)
    {
        _onlyAuthority = onlyAuthority;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Enforce HTTPS in the client path
        if (request.RequestUri is null || request.RequestUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException(
                $"Cleartext HTTP is forbidden for blind node communications: {request.RequestUri}");
        }

        if (_onlyAuthority is not null
            && !string.Equals(request.RequestUri.Authority, _onlyAuthority, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"This connection only calls {_onlyAuthority}; a request to {request.RequestUri.Authority} was refused.");
        }

        var response = await base.SendAsync(request, ct);

        // Refuse redirects: any 3xx is an error so credentials/data are never leaked to unpinned hosts
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            var target = response.Headers.Location;
            response.Dispose();
            throw new HttpRequestException(
                $"Request to {request.RequestUri} was redirected ({status}) to {target}; redirects are not followed.",
                inner: null,
                statusCode: (HttpStatusCode)status);
        }

        return response;
    }

    /// <summary>
    /// Creates the primary HttpClientHandler with AllowAutoRedirect = false and SPKI pinning.
    /// </summary>
    public static HttpClientHandler CreatePrimaryHandler(string? expectedPin, Action<string>? onPinRefused = null) => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            var accepted = ValidateServerCertificate(request, certificate, errors, expectedPin);
            // The stack only reports "Connection failure" for a refused handshake - the same words as for a node that
            // is off. A node that answers with another key than the pinned one is worth its own line.
            if (!accepted && request.RequestUri is { } uri) onPinRefused?.Invoke(uri.Authority);
            return accepted;
        }
    };

    public static HttpClientHandler CreatePrimaryHandler(BlindPhoneState? state = null) =>
        CreatePrimaryHandler(state?.CallCode?.SpkiPin);

    /// <summary>
    /// The primary handler for a node on a public CA: the certificate must validate through the system's chain, name
    /// included (<see cref="PublicCaTls"/>); no redirect is followed. Pinning plays no part — there is no pin.
    /// </summary>
    /// <param name="onCertificateRefused">Told the host:port of a node whose certificate was refused.</param>
    /// <param name="anchors">Extra roots a test supplies; <c>null</c> (what every app passes) is the platform's trust alone.</param>
    public static HttpClientHandler CreatePublicCaHandler(Action<string>? onCertificateRefused = null, TlsTrustAnchors? anchors = null) => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            var accepted = PublicCaTls.IsValid(certificate, chain, errors, anchors);
            if (!accepted && request.RequestUri is { } uri) onCertificateRefused?.Invoke(uri.Authority);
            return accepted;
        }
    };

    public static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        SslPolicyErrors errors,
        BlindPhoneState? state) =>
        ValidateServerCertificate(request, certificate, errors, state?.CallCode?.SpkiPin);

    public static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        SslPolicyErrors errors,
        string? expectedPin)
    {
        if (certificate is null)
        {
            return false;
        }

        // 1. Explicit pin on request options (e.g. for initial pairing or targeted requests)
        if (request.Options.TryGetValue(ExplicitPin, out var pin) && !string.IsNullOrWhiteSpace(pin))
        {
            return SpkiPin.Matches(certificate, pin);
        }

        // 2. Expected pin bound to this handler
        if (!string.IsNullOrWhiteSpace(expectedPin))
        {
            return SpkiPin.Matches(certificate, expectedPin);
        }

        // Pinning is mandatory: reject when no non-empty pin is available (no CA fallback)
        return false;
    }
}
