using System.Collections.Concurrent;
using System.Text;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>Throttling class of an incoming Web request.</summary>
public enum RateLimitedRoute
{
    /// <summary>Not a throttled route.</summary>
    None,
    /// <summary>Ordinary sign-in.</summary>
    Login,
    /// <summary>Verifies the master password and, on a match, WIPES THE NODE.</summary>
    NodeReset
}

/// <summary>
/// Path normalization shared by every rate limiter, so all of them match a route the same way the
/// router does.
/// </summary>
public static class RateLimitPath
{
    public const string LoginPath = "/login";
    public const string AdminPath = "/admin";

    /// <summary>
    /// Which throttling class a Web request falls into. Pure and testable, because the two
    /// mistakes possible here are both silent: send a destructive route to the permissive budget,
    /// or give two vectors onto the same destructive action a budget each.
    ///
    /// <para>
    /// The node wipe used to be reachable from the anonymous Login screen and through an anonymous
    /// <c>/api-proxy/init/reset</c> route, so it needed its own budget on both. It now lives only on
    /// the superadmin-only Admin page; the budget stays as brute-force protection on the master
    /// password an already-signed-in caller must still supply, but an anonymous visitor can no
    /// longer reach it at all.
    /// </para>
    /// </summary>
    /// <param name="handlerValues">
    /// Every value of the <c>handler</c> query parameter, not a joined string. Razor dispatches on
    /// the FIRST value, so "?handler=ResetNode&amp;handler=x" runs the node wipe — while a joined
    /// "ResetNode,x" compares unequal and would fall through to an unthrottled path. Any value
    /// matching wins: erring toward the stricter limiter only ever costs an attacker.
    /// </param>
    public static RateLimitedRoute Classify(string normalizedPath, IEnumerable<string?>? handlerValues)
    {
        bool IsHandler(string name) =>
            handlerValues?.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) == true;

        if (normalizedPath == AdminPath)
            return IsHandler("ResetNode") ? RateLimitedRoute.NodeReset : RateLimitedRoute.None;

        if (normalizedPath != LoginPath) return RateLimitedRoute.None;

        return RateLimitedRoute.Login;
    }

    /// <summary>
    /// Lower-cases, collapses runs of slashes, and drops a trailing slash.
    ///
    /// <para>
    /// A throttle that matches paths by equality has to normalize exactly as routing does, or the
    /// difference between the two IS the bypass. ASP.NET Core reaches the same endpoint for
    /// <c>/api/init/reset</c> and <c>/api/init/reset/</c>, and (depending on host and proxy) for
    /// <c>//login</c> — a limiter comparing raw strings sees different paths and throttles only one
    /// of them, leaving a node-wiping endpoint wide open behind a one-character change.
    /// </para>
    /// </summary>
    public static string Normalize(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath)) return "";

        var sb = new StringBuilder(rawPath.Length);
        foreach (var ch in rawPath)
        {
            if (ch == '/' && sb.Length > 0 && sb[^1] == '/') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        // A trailing slash carries no meaning for these routes; "/" itself must stay "/".
        if (sb.Length > 1 && sb[^1] == '/') sb.Length--;
        return sb.ToString();
    }
}

/// <summary>
/// Per-key sliding-window attempt limiter, shared by the API's <c>RateLimitMiddleware</c> and the
/// Web layer's public-endpoint limiter so both enforce the same semantics.
/// <para>
/// The two layers need separate instances rather than a shared bucket: the API's limiter keys on
/// the IP that opened the API connection, which for browser traffic is the Web process on loopback,
/// while the Web limiter keys on the actual remote client. Sharing one bucket would let one
/// browser's failures throttle every MCP agent, and vice versa.
/// </para>
/// </summary>
public sealed class SlidingWindowRateLimiter(int maxAttempts, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();
    private int _requestCounter;

    public int MaxAttempts { get; } = maxAttempts;
    public TimeSpan Window { get; } = window;

    /// <summary>
    /// Records an attempt against <paramref name="key"/> and reports whether it is allowed.
    /// A rejected attempt is NOT recorded — otherwise a caller hammering a blocked key would keep
    /// pushing its own unblock time back indefinitely, turning a 5-minute window into a permanent
    /// ban for anyone sharing that IP.
    /// </summary>
    public bool TryAcquire(string key, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        bool allowed;
        while (true)
        {
            var timestamps = _attempts.GetOrAdd(key, _ => new List<DateTime>());
            lock (timestamps)
            {
                // The periodic sweep evicts empty lists, so between GetOrAdd and this lock our
                // brand-new list may already have been removed from the dictionary — recording an
                // attempt on it would write to an orphan nobody reads, silently granting a free
                // attempt. Confirm we still hold the published list, and retry if not.
                if (!_attempts.TryGetValue(key, out var current) || !ReferenceEquals(current, timestamps))
                    continue;

                timestamps.RemoveAll(t => now - t > Window);
                allowed = timestamps.Count < MaxAttempts;
                if (allowed) timestamps.Add(now);
            }
            break;
        }

        // Periodic sweep so keys that stop being used don't accumulate forever.
        if (Interlocked.Increment(ref _requestCounter) % 100 == 0)
            Sweep(now);

        return allowed;
    }

    /// <summary>
    /// Forgets every attempt recorded for <paramref name="key"/>. Callers invoke this after a
    /// SUCCESSFUL attempt, so a shared egress IP (an office behind NAT, where a dozen people reach
    /// the node as one address) can't lock itself out through ordinary mistyped passwords: the
    /// window only fills while nobody is getting in, which is exactly the brute-force shape.
    /// </summary>
    public void Reset(string key) => _attempts.TryRemove(key, out _);

    /// <summary>Attempts currently counted against a key. Test/diagnostic helper.</summary>
    public int CountFor(string key, DateTime? nowUtc = null)
    {
        if (!_attempts.TryGetValue(key, out var timestamps)) return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        lock (timestamps)
        {
            timestamps.RemoveAll(t => now - t > Window);
            return timestamps.Count;
        }
    }

    private void Sweep(DateTime now)
    {
        foreach (var kvp in _attempts)
        {
            lock (kvp.Value)
            {
                kvp.Value.RemoveAll(t => now - t > Window);
                if (kvp.Value.Count == 0) _attempts.TryRemove(kvp.Key, out _);
            }
        }
    }
}
