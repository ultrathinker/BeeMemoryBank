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
/// 3. SPKI public key pinning for TLS connections against the paired listening node.
/// </summary>
public sealed class BlindHttpHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> ExplicitPin = new("bmb.tls-spki");

    public BlindHttpHandler()
    {
    }

    public BlindHttpHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Enforce HTTPS in the client path
        if (request.RequestUri is null || request.RequestUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException(
                $"Cleartext HTTP is forbidden for blind node communications: {request.RequestUri}");
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
    public static HttpClientHandler CreatePrimaryHandler(BlindPhoneState? state = null) => new()
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            ValidateServerCertificate(request, certificate, errors, state)
    };

    public static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        SslPolicyErrors errors,
        BlindPhoneState? state)
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

        // 2. State call-code pin
        if (state?.CallCode?.SpkiPin is { } statePin && !string.IsNullOrWhiteSpace(statePin))
        {
            return SpkiPin.Matches(certificate, statePin);
        }

        // Pinning is mandatory: reject when no non-empty pin is available (no CA fallback)
        return false;
    }
}
