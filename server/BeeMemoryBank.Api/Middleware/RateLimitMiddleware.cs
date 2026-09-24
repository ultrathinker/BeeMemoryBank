using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Per-IP rate limiter for sensitive endpoints (unlock, login, join, node reset).
/// Sliding window: tracks attempts per IP, blocks after maxAttempts within the window.
/// </summary>
public class RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger)
{
    // 5 attempts per 5 minutes — after that, 429 Too Many Requests
    private static readonly SlidingWindowRateLimiter Limiter = new(5, TimeSpan.FromMinutes(5));

    // Endpoints to protect
    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/session/unlock",
        "/api/session/login",
        "/api/join",
        // Cross-instance Phase 3 token issuance — same brute-force risk as
        // /login but bypasses InternalKeyValidator entirely (Claude round-3).
        "/api/auth/remote-token",
        // Verifies a master password and, when it matches, WIPES THE NODE. The Web layer's
        // PublicRateLimitMiddleware covers the browser route into this; listing it here closes
        // the direct-to-API port for any caller that isn't on loopback.
        "/api/init/reset"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        // Normalized the same way the Web limiter does, and for the same reason: this list matched
        // the raw path by equality, so "/api/init/reset/" — which routing happily dispatches to the
        // same endpoint — was not in the set and skipped the limiter entirely. A throttle keyed on
        // string equality has to normalize exactly as the router does, or the difference between
        // the two IS the bypass.
        var path = RateLimitPath.Normalize(context.Request.Path.Value);

        if (context.Request.Method == "POST" && ProtectedPaths.Contains(path))
        {
            // RemoteIpAddress, never a raw X-Forwarded-For read here: the header is only believed
            // when it arrives from a hop this deployment declared trustworthy, and that decision
            // belongs in one place (ForwardedHeadersExtensions / BMB_TRUSTED_PROXIES), which has
            // already rewritten RemoteIpAddress by the time this middleware runs.
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // The "trusted internal caller" exception is the INTERNAL KEY, not loopback. The Web
            // layer (InternalKeyHandler → X-Internal-Key), desktop tray (App.axaml.cs → X-Internal-Key),
            // and CLI (DekRotateCommand/SnapshotCommand → X-Internal-Key) all carry it on the calls
            // they make to these protected paths. Loopback WITHOUT the key is just an anonymous
            // caller that happens to be local — exactly the shape a reverse proxy on the same host
            // produces when BMB_TRUST_LOOPBACK_FORWARDED_HEADERS is not set, and exactly the case
            // B3 fixes: previously every loopback caller skipped the limiter, so an internet client
            // reaching the API through such a proxy got an unlimited password oracle.
            //
            // Browser traffic reaches this hop through the Web layer (which presents the key), so
            // it is still keyed correctly by the key check, not by IP. The Web's own
            // PublicRateLimitMiddleware keeps throttling /Login and the Admin node-reset handler
            // per real client IP, so the key-vs-IP split is unchanged for legitimate browser
            // traffic.
            if (InternalKeyValidator.Validate(context))
            {
                await next(context);
                return;
            }

            if (!Limiter.TryAcquire($"{ip}:{path}"))
            {
                logger.LogWarning("Rate limit exceeded for {IP} on {Path}", ip, path);
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "300";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Too many attempts. Try again later.\"}");
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// TEST-ONLY: drops every recorded attempt so a rate-limit test can start from a clean
    /// bucket. The limiter is process-wide, so without this hook parallel test classes (or any
    /// test re-running under xUnit's per-collection default) would share buckets and flake when
    /// one of them has already burnt the 5-attempt budget for the same IP+path pair.
    /// </summary>
    internal static void ResetForTests() => Limiter.ResetAll();
}
