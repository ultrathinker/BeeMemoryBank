using System.Collections.Concurrent;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Per-IP rate limiter for sensitive endpoints (unlock, join).
/// Sliding window: tracks attempts per IP, blocks after maxAttempts within the window.
/// </summary>
public class RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger)
{
    private static readonly ConcurrentDictionary<string, List<DateTime>> Attempts = new();
    private static int _requestCounter;

    // 5 attempts per 5 minutes — after that, 429 Too Many Requests
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Endpoints to protect
    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/session/unlock",
        "/api/session/login",
        "/api/join"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (context.Request.Method == "POST" && ProtectedPaths.Contains(path))
        {
                // Security: use RemoteIpAddress directly instead of trusting X-Forwarded-For,
                // which can be spoofed by any client. The real client IP is determined by the
                // reverse proxy (Nginx) at the connection level, not via headers.
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // AUDIT NOTE: Localhost skip is intentional — the Web proxy calls the API on localhost
                // and handles its own authentication (cookie-based session + CSRF protection via
                // SameSite=Strict). Rate limiting the Web→API channel would break normal login flow.
                if (ip is "127.0.0.1" or "::1" or "0.0.0.0" or "unknown")
            {
                await next(context);
                return;
            }

            var key = $"{ip}:{path}";
            var now = DateTime.UtcNow;

            var timestamps = Attempts.GetOrAdd(key, _ => new List<DateTime>());

            bool rateLimited;
            lock (timestamps)
            {
                // Evict expired entries
                timestamps.RemoveAll(t => now - t > Window);

                rateLimited = timestamps.Count >= MaxAttempts;
                if (rateLimited)
                {
                    logger.LogWarning("Rate limit exceeded for {IP} on {Path} ({Count} attempts in window)",
                        ip, path, timestamps.Count);
                }
                else
                {
                    timestamps.Add(now);
                }
            }

            if (rateLimited)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "300";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Too many attempts. Try again later.\"}");
                return;
            }

            // Periodic cleanup: every 100 requests, evict empty entries to prevent unbounded growth
            if (Interlocked.Increment(ref _requestCounter) % 100 == 0)
            {
                foreach (var kvp in Attempts)
                {
                    lock (kvp.Value)
                    {
                        kvp.Value.RemoveAll(t => now - t > Window);
                        if (kvp.Value.Count == 0)
                            Attempts.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        await next(context);
    }
}
