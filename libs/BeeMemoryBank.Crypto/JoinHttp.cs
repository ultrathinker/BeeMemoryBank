namespace BeeMemoryBank.Crypto;

/// <summary>
/// The HTTP client every join that carries the master password must use (the phone's setup, <c>bmb join</c>;
/// the Web setup join uses the Api's no-redirect named client). It never follows a redirect and treats any
/// 3xx as an error: a 307/308 keeps the method and body, so following one would resend the master password
/// to wherever the server pointed — plain http, another host. With a join code's SPKI pin, TLS completes
/// only with the pinned key as well (<see cref="SpkiPin"/>).
/// </summary>
public static class JoinHttp
{
    public static HttpClient CreateClient(string? spkiPin = null) =>
        new(spkiPin != null
            ? SpkiPin.CreatePinnedHandler(spkiPin)
            : new RedirectRefusingHandler(new SocketsHttpHandler { AllowAutoRedirect = false }));
}

/// <summary>Turns any 3xx into an <see cref="HttpRequestException"/>; the inner handler must not follow redirects.</summary>
internal sealed class RedirectRefusingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"The server answered with a redirect ({status}); a join never follows one.");
        }
        return response;
    }
}
