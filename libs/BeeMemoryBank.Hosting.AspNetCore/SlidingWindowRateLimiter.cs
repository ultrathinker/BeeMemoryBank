using System.Collections.Concurrent;
using System.Text;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Path normalization shared by every rate limiter, so all of them match a route the same way the
/// router does.
/// </summary>
public static class RateLimitPath
{
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
        var timestamps = _attempts.GetOrAdd(key, _ => new List<DateTime>());

        bool allowed;
        lock (timestamps)
        {
            timestamps.RemoveAll(t => now - t > Window);
            allowed = timestamps.Count < MaxAttempts;
            if (allowed) timestamps.Add(now);
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
