namespace BeeMemoryBank.Web.Services;

/// <summary>
/// One entry in the W1 catch-all forwarder's route table. Maps a proxy path-prefix
/// (relative to <c>/api-proxy</c>) to its upstream destination and gating rules.
/// </summary>
/// <param name="UpstreamPrefix">The <c>/api/...</c> prefix to forward to (e.g. <c>/api/concept-tags</c>).</param>
/// <param name="RequiredRole">Role claim required (currently only "superadmin"); null = any authenticated user.</param>
/// <param name="Streaming">If true, stream the body through (ResponseHeadersRead + Results.File). Pilot routes are non-streaming.</param>
public sealed record ProxyRouteEntry(string UpstreamPrefix, string? RequiredRole, bool Streaming);

/// <summary>
/// Declarative, DENY-BY-DEFAULT route table for the W1 catch-all forwarder. A request to
/// <c>/api-proxy/{path}</c> is forwarded ONLY when its longest matching prefix is present
/// here; an unknown prefix returns 404 (never a blind forward). This mirrors
/// CallerScopeMiddleware's deny-all: a forgotten prefix fails safe, and a new
/// superadmin-only route must be added explicitly.
///
/// Identity headers (X-Internal-Key / X-User-*) are injected automatically by
/// <see cref="InternalKeyHandler"/> on every forwarded call, so the table only needs to
/// express ROLE gating and STREAMING — not auth.
/// </summary>
public static class ProxyRouteTable
{
    // path-prefix (relative to /api-proxy, no leading slash, case-insensitive) → entry.
    private static readonly Dictionary<string, ProxyRouteEntry> _entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── W1 PILOT: concept-tags GET family ───────────────────────────────────
        // Lowest-risk group: GET-only, role: none, non-streaming. The superadmin
        // concept-tags MUTATIONS (rename PUT / merge POST / delete DELETE) stay as
        // explicit hand-written routes — they are more specific than this catch-all, so
        // endpoint routing prefers them and they keep their RequireAuthorization("superadmin")
        // gate. Do NOT add role:none entries for prefixes that also carry superadmin mutations
        // unless those mutations remain explicit (they do, here).
        ["concept-tags"] = new ProxyRouteEntry("/api/concept-tags", RequiredRole: null, Streaming: false),
    };

    /// <summary>
    /// Longest-segment-prefix match. "concept-tags" matches "concept-tags" and
    /// "concept-tags/graph" but the match is segment-aware so a future "concept-tags-x"
    /// prefix would NOT accidentally match "concept-tags".
    /// </summary>
    public static ProxyRouteEntry? Match(string proxyPath, out string? matchedPrefix)
    {
        matchedPrefix = null;
        if (string.IsNullOrEmpty(proxyPath)) return null;

        string? best = null;
        foreach (var key in _entries.Keys)
        {
            var equals = proxyPath.Equals(key, StringComparison.OrdinalIgnoreCase);
            var startsSegment = proxyPath.Length > key.Length
                                && proxyPath[key.Length] == '/'
                                && proxyPath.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase);
            if (equals || startsSegment)
            {
                if (best == null || key.Length > best.Length)
                    best = key;
            }
        }

        if (best == null) return null;
        matchedPrefix = best;
        return _entries[best];
    }

    /// <summary>
    /// Rebuilds the upstream path by swapping the matched proxy prefix for the entry's
    /// upstream prefix and appending the remainder verbatim. E.g. proxy "concept-tags/graph"
    /// with prefix "concept-tags" → upstream "/api/concept-tags/graph".
    /// </summary>
    public static string BuildUpstreamPath(string proxyPath, string matchedPrefix, ProxyRouteEntry entry)
    {
        var remainder = proxyPath.Length > matchedPrefix.Length
            ? proxyPath[matchedPrefix.Length..]
            : "";
        return entry.UpstreamPrefix + remainder;
    }
}
