using System.Collections.Concurrent;

namespace BeeMemoryBank.Hosting.AspNetCore;

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
