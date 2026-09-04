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

            // AUDIT NOTE: Localhost skip is intentional — the Web proxy calls the API on localhost
            // and handles its own authentication (cookie-based session + CSRF protection via
            // SameSite=Strict). Rate limiting the Web→API channel would break normal login flow,
            // since every browser login would be attributed to the single loopback address.
            //
            // Browser traffic is NOT therefore unthrottled: BeeMemoryBank.Web's
            // PublicRateLimitMiddleware limits /Login and the Admin node-reset handler per real
            // client IP before they ever reach this hop. Do not "fix" this skip by removing it —
            // the two layers are deliberately split, one keyed on the real client and one on the
            // API peer.
            //
            // "0.0.0.0" and "unknown" used to be skipped alongside loopback. They are not a trusted
            // internal caller: "unknown" means the connection had no remote address the server
            // could read at all, which is precisely the case where throttling should apply rather
            // than be waived. They now share one bucket, which is the correct conservative default.
            if (ip is "127.0.0.1" or "::1")
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
}
