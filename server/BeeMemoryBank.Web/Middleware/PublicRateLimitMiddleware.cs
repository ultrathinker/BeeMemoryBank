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
/// <c>POST /Login</c> — ordinary sign-in, plus (via a named handler on the same page)
/// <c>ContinueWithoutBackup</c>, which verifies the master password. Both are anonymous by
/// necessity: they exist for people who cannot authenticate yet.
/// </description></item>
/// <item><description>
/// <c>POST /Admin?handler=ResetNode</c> — WIPES THE NODE on a correct master password. Not
/// anonymous (the page is superadmin-only), but throttled anyway: the master password is a second
/// credential in front of a destructive action, and a hijacked admin session must not get unlimited
/// guesses at it. The two anonymous vectors onto this same wipe — <c>/Login?handler=Reset</c> and
/// <c>/api-proxy/init/reset</c> — are gone.
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
        // here are silent ones. The same page serves several handlers and they are not equally
        // dangerous — on /Admin the default POST is routine config, ?handler=ResetNode WIPES THE NODE.
        //
        // The bucket key is the ROUTE CLASS, not the path, so any future second vector onto the same
        // destructive action shares one budget instead of doubling it — which is what happened while
        // /Login?handler=Reset and /api-proxy/init/reset were two doors to the identical wipe.
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
    /// redirects (to the return URL). So for /Login a 3xx is the only success signal and 200
    /// explicitly is not. The Admin reset handler always redirects — to /Setup on success, back to
    /// /Admin with an <c>err</c> query value on a wrong password — so a status code cannot tell the
    /// two apart, and its bucket is deliberately never reset: five wipe attempts per 15 minutes is
    /// already far above what a legitimate admin needs.
    /// </remarks>
    private static bool IsSuccess(string path, int statusCode) => path switch
    {
        RateLimitPath.LoginPath => statusCode is >= 300 and < 400,
        _ => false
    };
}
