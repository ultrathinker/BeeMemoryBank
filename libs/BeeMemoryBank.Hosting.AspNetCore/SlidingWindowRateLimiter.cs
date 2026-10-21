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
    NodeReset,
    /// <summary>"Forgot your password": checks a recovery key (an Argon2id derivation per recovery slot) and, on a match, replaces a superadmin's password.</summary>
    RecoverAccess
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
    /// The page of the node wipe. The Admin area has a page per block, and the wipe has its own (<c>/Admin/ResetNode</c>), whose only POST handler is the wipe.
    /// </summary>
    public const string AdminResetNodePath = "/admin/resetnode";
    public const string RecoverAccessPath = "/recoveraccess";

    /// <summary>
    /// Which throttling class a Web request falls into. Pure and testable, because the two
    /// mistakes possible here are both silent: send a destructive route to the permissive budget,
    /// or give two vectors onto the same destructive action a budget each.
    ///
    /// <para>
    /// The node wipe lives only on the superadmin-only Admin area, so an anonymous visitor cannot
    /// reach it; its budget is brute-force protection on the master password an already-signed-in
    /// caller must still supply.
    /// </para>
    /// <para>
    /// Every POST to <see cref="AdminResetNodePath"/> is charged to the wipe budget, whatever handler it names: that page has no other POST handler, and a
    /// request that names none or a wrong one is one more guess at nothing, which costs a guesser and nobody else. The address of the former single Admin
    /// page stays classified with its handler (<see cref="AdminPath"/>), so a handler of that name added to that page one day starts throttled.
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

        if (normalizedPath == AdminResetNodePath) return RateLimitedRoute.NodeReset;

        if (normalizedPath == AdminPath)
            return IsHandler("ResetNode") ? RateLimitedRoute.NodeReset : RateLimitedRoute.None;

        if (normalizedPath == RecoverAccessPath) return RateLimitedRoute.RecoverAccess;

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
/// <para>
/// <paramref name="maxKeys"/> (0 = no bound, what the older users of this class have) caps how many keys are counted at once, for a limiter whose keys a stranger
/// chooses (the public invitation routes: one key per caller address, and an attacker has as many addresses as it can spoof or rent). The cap is exact: every NEW key
/// is admitted to the table under one lock that decides the capacity and inserts, so any number of concurrent newcomers leaves at most <paramref name="maxKeys"/> keys.
/// At the cap a newcomer is admitted only if keys whose window has run out can be dropped to make room (the only keys that are dropped: a key that is still counting is
/// never forgotten, so no caller's partial budget and no ban is reset by pressure from others); if none can, the newcomer is refused (<see cref="TryAcquire"/> says no)
/// until a window runs out. The trade-off, said plainly: while the table is full of keys that are still counting, callers with an address not in it are refused for up to
/// one window - the price of never resetting anybody's budget; what fills the table is bounded by what the callers in front of this limiter let through (the public
/// routes' process-wide budget, and <see cref="Refund"/> for attempts the budget turned away).
/// </para>
/// </summary>
public sealed class SlidingWindowRateLimiter(int maxAttempts, TimeSpan window, int maxKeys = 0)
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new();
    private readonly object _roomLock = new();
    private DateTime _noRoomBefore = DateTime.MinValue;
    private int _requestCounter;

    public int MaxAttempts { get; } = maxAttempts;
    public TimeSpan Window { get; } = window;

    /// <summary>The most keys counted at once; 0 when unbounded.</summary>
    public int MaxKeys { get; } = maxKeys;

    /// <summary>How many keys are counted now. Test/diagnostic helper.</summary>
    public int KeyCount => _attempts.Count;

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
            if (!_attempts.TryGetValue(key, out var timestamps))
            {
                timestamps = AddKey(key, now);
                if (timestamps is null) return false;   // the table is full of keys that are still counting: a newcomer waits
            }
            lock (timestamps)
            {
                // The periodic sweep evicts empty lists, so between the lookup and this lock our
                // list may already have been removed from the dictionary — recording an
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
    /// Takes back the newest attempt recorded for <paramref name="key"/> - for an attempt that was counted here but then turned away by a limit in front of the work
    /// (the process-wide budget of a public route): a request that was refused did not use the caller's budget, and refused traffic must not fill the table either, so a
    /// key left with no attempt is dropped.
    /// </summary>
    public void Refund(string key)
    {
        if (!_attempts.TryGetValue(key, out var timestamps)) return;
        lock (timestamps)
        {
            if (timestamps.Count > 0) timestamps.RemoveAt(timestamps.Count - 1);
            if (timestamps.Count == 0) _attempts.TryRemove(new KeyValuePair<string, List<DateTime>>(key, timestamps));
        }
    }

    /// <summary>
    /// How long until <paramref name="key"/> is allowed again (the oldest attempt in its window leaving it), or null when the key is not counted - for a key refused
    /// because the table is full of banned keys there is no such time, and the caller picks its own.
    /// </summary>
    public TimeSpan? RetryAfter(string key, DateTime? nowUtc = null)
    {
        if (!_attempts.TryGetValue(key, out var timestamps)) return null;
        var now = nowUtc ?? DateTime.UtcNow;
        lock (timestamps)
        {
            timestamps.RemoveAll(t => now - t > Window);
            if (timestamps.Count < MaxAttempts || timestamps.Count == 0) return TimeSpan.Zero;
            var wait = timestamps[0] + Window - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
    }

    /// <summary>
    /// Forgets every attempt recorded for <paramref name="key"/>. Callers invoke this after a
    /// SUCCESSFUL attempt, so a shared egress IP (an office behind NAT, where a dozen people reach
    /// the node as one address) can't lock itself out through ordinary mistyped passwords: the
    /// window only fills while nobody is getting in, which is exactly the brute-force shape.
    /// </summary>
    public void Reset(string key) => _attempts.TryRemove(key, out _);

    /// <summary>
    /// Forgets every attempt recorded for every key. Tests that exercise rate-limit behaviour
    /// start from a clean bucket via this; in production the per-key <see cref="Reset"/> is the
    /// only path. The process-wide shared state is what makes the test hook necessary.
    /// </summary>
    public void ResetAll() => _attempts.Clear();

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

    // A key that is not in the table yet. Unbounded: as it always was. Bounded: the capacity decision and the insertion are ONE step under one lock - the only place a
    // bounded limiter adds a key - so concurrent newcomers cannot all see room and all insert. At the cap only keys whose window has run out are dropped; null when
    // that frees nothing (and not looked for again for a second, so a flood of newcomers does not make every one pay for a scan).
    private List<DateTime>? AddKey(string key, DateTime now)
    {
        if (MaxKeys <= 0) return _attempts.GetOrAdd(key, _ => new List<DateTime>());

        lock (_roomLock)
        {
            if (_attempts.TryGetValue(key, out var existing)) return existing;
            if (_attempts.Count >= MaxKeys)
            {
                if (now < _noRoomBefore) return null;
                Sweep(now);
                if (_attempts.Count >= MaxKeys)
                {
                    _noRoomBefore = now + TimeSpan.FromSeconds(1);
                    return null;
                }
            }
            var created = new List<DateTime>();
            _attempts[key] = created;
            return created;
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
