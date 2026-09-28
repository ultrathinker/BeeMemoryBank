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
            throw new HttpRequestException($"Refusing plain {request.RequestUri?.Scheme} to a pinned peer: {request.RequestUri}");

        var response = await base.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            var target = response.Headers.Location;
            response.Dispose();
            throw new HttpRequestException(
                $"Sync request to {request.RequestUri} was redirected ({status}) to {target}; redirects are not followed.",
                inner: null, statusCode: (HttpStatusCode)status);
        }
        return response;
    }
}
