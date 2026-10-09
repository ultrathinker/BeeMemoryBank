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
    /// <summary>
    /// How long one request of the key exchange (the join, the challenge, the authentication) may take. Not <see cref="HttpClient.Timeout"/>:
    /// that one is a single value for the whole client, and the snapshot request of the same join must be allowed far longer, because the host
    /// builds the whole snapshot (a copy of the database, the media, the archive, its signature) before it sends the first header, and a vault
    /// of a few gigabytes takes minutes. So the client has no timeout of its own and every request is bounded by <see cref="BoundAsync{T}"/>.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static HttpClient CreateClient(string? spkiPin = null) =>
        new(spkiPin != null
            ? SpkiPin.CreatePinnedHandler(spkiPin)
            : new RedirectRefusingHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    /// <summary>
    /// Runs one request of a join with its own time limit (<paramref name="limit"/>, <see cref="RequestTimeout"/> by default). A request that
    /// runs out of time fails as a <see cref="HttpRequestException"/> (inner <see cref="TimeoutException"/>), so every caller's handling of an
    /// unreachable host applies to it; the caller's own cancellation stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<T> BoundAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken ct = default, TimeSpan? limit = null)
    {
        var span = limit ?? RequestTimeout;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(span);
        try
        {
            return await request(bound.Token);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"The other computer did not answer within {span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} seconds.", new TimeoutException());
        }
    }
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
