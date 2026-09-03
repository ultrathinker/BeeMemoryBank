using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Web.Middleware;

/// <summary>
/// Per-IP throttling for the Web layer's anonymous, password-checking endpoints.
///
/// <para>
/// The API has its own <c>RateLimitMiddleware</c>, but it deliberately skips loopback callers —
/// and every browser request reaches the API through this Web process on loopback, so browser
/// traffic was never throttled by anything. That left two unbounded password oracles open to the
/// internet, both of which spend a full Argon2id derivation (64 MiB, t=3) per guess:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>POST /Login</c> — ordinary sign-in, plus (via named handlers on the same page) the
/// <c>Reset</c> handler, which WIPES THE NODE on a correct master password, and
/// <c>ContinueWithoutBackup</c>, which also verifies one. All three are anonymous by necessity:
/// they exist for people who cannot authenticate yet.
/// </description></item>
/// <item><description>
/// <c>POST /api-proxy/init/reset</c> — the same node wipe, reachable directly.
/// </description></item>
/// </list>
///
/// <para>
/// Runs after <c>UseLoopbackForwardedHeaders()</c> so <c>RemoteIpAddress</c> is the real client
/// when a trusted loopback reverse proxy is configured, and the direct peer otherwise. It never
/// reads <c>X-Forwarded-For</c> itself — an untrusted client could forge a fresh IP per request
/// and shed the limit entirely.
/// </para>
/// </summary>
public class PublicRateLimitMiddleware(RequestDelegate next, ILogger<PublicRateLimitMiddleware> logger)
{
    // Sign-in: generous enough that an office sharing one NAT address does not lock itself out on
    // a Monday morning, and any success clears the bucket outright (see OnSuccess below), so the
    // window only fills while nobody is getting in. Still turns "unlimited guesses" into ~240/hour.
    private static readonly SlidingWindowRateLimiter LoginLimiter = new(20, TimeSpan.FromMinutes(5));

    // Node reset: destructive and never routine. A legitimate admin needs one or two tries.
    private static readonly SlidingWindowRateLimiter ResetLimiter = new(5, TimeSpan.FromMinutes(15));

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            await next(context);
            return;
        }

        var path = RateLimitPath.Normalize(context.Request.Path.Value);

        // Classification lives in RateLimitPath so it can be unit-tested: both mistakes possible
        // here are silent ones. /Login carries three handlers and they are not equally dangerous —
        // the default signs in, ?handler=Reset WIPES THE NODE on a correct master password.
        //
        // The bucket key is the ROUTE CLASS, not the path. Both reset vectors perform the identical
        // wipe, so keying them separately handed an attacker 5 attempts on /Login?handler=Reset
        // plus another 5 on /api-proxy/init/reset — double the budget the limiter exists to impose.
        var route = RateLimitPath.Classify(path, context.Request.Query["handler"]);
        var (limiter, label, keySuffix) = route switch
        {
            RateLimitedRoute.NodeReset => (ResetLimiter, "node reset", "reset"),
            RateLimitedRoute.Login => (LoginLimiter, "login", "login"),
            _ => (null, "", "")
        };
        if (limiter == null)
        {
            await next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{ip}:{keySuffix}";

        if (!limiter.TryAcquire(key))
        {
            logger.LogWarning("Rate limit exceeded for {IP} on {Path} ({Label})", ip, path, label);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = ((int)limiter.Window.TotalSeconds).ToString();
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Too many attempts. Try again later.");
            return;
        }

        await next(context);

        if (IsSuccess(path, context.Response.StatusCode))
            limiter.Reset(key);
    }

    /// <summary>
    /// Whether the completed request actually got in — the signal that this IP is a legitimate
    /// user rather than a guesser, so its window can be forgiven.
    /// </summary>
    /// <remarks>
    /// A failed Razor login re-renders the page as 200 with an error message; only success
    /// redirects (to the return URL, or to /Setup after a reset). So for /Login a 3xx is the only
    /// success signal and 200 explicitly is not. The proxy reset endpoint is a plain API route:
    /// 200 on success, 400 on a wrong password.
    /// </remarks>
    private static bool IsSuccess(string path, int statusCode) => path switch
    {
        RateLimitPath.LoginPath => statusCode is >= 300 and < 400,
        RateLimitPath.ResetProxyPath => statusCode is >= 200 and < 300,
        _ => false
    };
}
