using System.Net;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Keeps every sync request on the host it was sent to (review L-stage1 #5). A pinned peer is only
/// ever reached over HTTPS — plain HTTP would skip the certificate check where the pin is enforced —
/// and no 3xx is followed or accepted: sync endpoints never redirect, and a redirect is exactly how
/// a request would leave the pinned host for one the pin says nothing about.
/// </summary>
public sealed class SpkiPinGuardHandler(SpkiPinRegistry pins, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (pins.IsPinned(request) && request.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException($"Refusing plain {request.RequestUri?.Scheme} to a pinned peer.");   // no URL: this text reaches logs and screens

        var response = await base.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            // Neither the address asked nor the one it was sent on to is named: both may carry what must not reach a log or a dialog (a token in a query).
            throw new HttpRequestException(
                $"Sync request was redirected ({status}); redirects are not followed.",
                inner: null, statusCode: (HttpStatusCode)status);
        }
        return response;
    }
}
