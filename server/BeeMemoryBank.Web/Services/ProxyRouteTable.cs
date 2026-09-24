using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Web.Services;

/// <summary>Response-shaping behavior the forwarder applies on top of verbatim passthrough.</summary>
[Flags]
public enum ProxyRouteFlags
{
    None = 0,

    /// <summary>
    /// Drop the upstream <c>Content-Disposition</c> header. Media GETs must render INLINE
    /// (they are embedded as <c>&lt;img&gt;</c> / file links), while the API serves
    /// <c>attachment; filename=...</c> because the API's own clients download. Stripping here
    /// preserves the Web contract without the API having to know which caller is a browser.
    /// </summary>
    StripContentDisposition = 1,

    /// <summary>
    /// Apply <see cref="BeeMemoryBank.Hosting.AspNetCore.UserContentResponseHeaders"/> to the
    /// browser response (CSP <c>sandbox</c> + nosniff), the way the hand-written media GET did.
    /// The API sets these on its own media responses, but the forwarder relays only a small
    /// header allow-list, and the Web's global security middleware would otherwise win — media
    /// bytes opened directly as a document would run under the site CSP (script-src 'unsafe-inline',
    /// same origin as the session cookie) instead of the sandbox that closes stored-SVG XSS.
    /// </summary>
    UserContent = 2,
}

/// <summary>
/// One method policy inside a route entry. <see cref="RequiredRole"/> mirrors the Web-side
/// <c>RequireAuthorization(policy =&gt; policy.RequireRole(...))</code> gate the hand-written
/// routes carried: null = any authenticated user, <see cref="UserRoles.Superadmin"/> = superadmin
/// only. The API enforces roles independently (X-User-Role via InternalKeyHandler → endpoint
/// filters); this gate is the same defense-in-depth the explicit routes had.
/// </summary>
/// <param name="Method">Uppercase HTTP method this rule applies to.</param>
/// <param name="RequiredRole">Required role claim; null = any authenticated user.</param>
public sealed record ProxyMethodRule(string Method, string? RequiredRole);

/// <summary>
/// One entry in the catch-all forwarder's route table. Maps a proxy path-prefix (relative to
/// <c>/api-proxy</c>) to its upstream destination plus the method→role matrix it allows.
/// </summary>
/// <param name="UpstreamPrefix">The <c>/api/...</c> prefix to forward to (e.g. <c>/api/concept-tags</c>).</param>
/// <param name="Methods">Allowed methods with their role gates. A method absent from this list is refused with 405.</param>
/// <param name="Flags">Extra passthrough behavior (e.g. header shaping).</param>
public sealed record ProxyRouteEntry(
    string UpstreamPrefix,
    IReadOnlyList<ProxyMethodRule> Methods,
    ProxyRouteFlags Flags = ProxyRouteFlags.None)
{
    /// <summary>Role gate for the given method, or null when the method is not allowed here.</summary>
    public ProxyMethodRule? FindMethod(string method) =>
        Methods.FirstOrDefault(m =>
            string.Equals(m.Method, method, StringComparison.OrdinalIgnoreCase));

    /// <summary>The methods a 405 response should advertise in its Allow header.</summary>
    public string AllowHeader => string.Join(", ", Methods.Select(m => m.Method).Distinct());
}

/// <summary>
/// Declarative, DENY-BY-DEFAULT route table for the catch-all forwarder. A request to
/// <c>/api-proxy/{path}</c> is forwarded ONLY when its longest matching prefix is present
/// here AND the request method is listed for that prefix; an unknown prefix returns 404 and an
/// unlisted method returns 405 (never a blind forward). This mirrors CallerScopeMiddleware's
/// deny-all: a forgotten prefix fails safe, and a new route must be added explicitly.
///
/// <para>Entries replicate what the migrated hand-written routes did, per route: URL mapping,
/// the role gate, and any special response shaping. The API remains the authority for roles and
/// ACLs — this table is the Web layer's own gate (same layering the explicit routes had).</para>
///
/// <para>Identity headers (X-Internal-Key / X-User-*) are injected automatically by
/// <see cref="InternalKeyHandler"/> on every forwarded call, so the table only expresses role
/// gating — not auth. Every hand-written route that carried actual Web-side logic (response
/// reshaping, composed calls, SSE, caches) is intentionally NOT in this table and stays an
/// explicit route; see MiscProxyEndpoints for the forwarder and the kept routes' own files.</para>
/// </summary>
public static class ProxyRouteTable
{
    // path-prefix (relative to /api-proxy, no leading slash, case-insensitive) → entry.
    // Role conventions: null = any authenticated user; UserRoles.Superadmin = superadmin only.
    // Where the Web gate was looser than the API's (e.g. remote-accounts used to reach the API
    // and get 403), the entry carries the EFFECTIVE policy — the API already enforced superadmin,
    // so the Web gate now says so instead of letting the request die as a 502-shaped failure.
    private static readonly Dictionary<string, ProxyRouteEntry> _entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Tree / search / folders / articles (any authenticated user) ──────────
        ["tree"] = new("/api/tree", [new("GET", null)]),
        ["search"] = new("/api/search", [new("GET", null)]),
        // No GET on the collection itself: the API has no GET /api/folders (reads go through
        // /tree) and no old route served one. The one folder read the UI makes lives at
        // /folders/search (used by the tree move dialogs and the access dialog).
        ["folders"] = new("/api/folders", [new("POST", null), new("PATCH", null), new("DELETE", null)]),
        ["folders/search"] = new("/api/folders/search", [new("GET", null)]),
        ["article"] = new("/api/articles", [new("POST", null), new("DELETE", null)]),
        ["articles"] = new("/api/articles", [new("GET", null)]),

        // ── Media (uploads stream straight through; GET is inline, no Content-Disposition) ──
        // "media/upload" must map to the API's POST /api/media/ (route root), so it is its own
        // entry: the upstream prefix IS the full path and the proxy path has no remainder.
        ["media/upload"] = new("/api/media/", [new("POST", null)]),
        ["media"] = new("/api/media",
            [new("GET", null), new("POST", null), new("DELETE", null)],
            ProxyRouteFlags.StripContentDisposition | ProxyRouteFlags.UserContent),

        // ── Imports (multipart streamed verbatim; the JS always sends destinationPath) ──
        ["import/obsidian"] = new("/api/import/obsidian", [new("POST", null)]),
        ["import/bee"] = new("/api/import/bee", [new("POST", null)]),

        // ── Downloads: prepare (POST) + single-use token fetch (GET, token is the credential,
        //    but the explicit route kept RequireAuthorization — preserved here). ──
        ["downloads"] = new("/api/downloads", [new("GET", null), new("POST", null)]),

        // ── Concept tags: reads for everyone, mutations superadmin (the API enforces too). ──
        ["concept-tags"] = new("/api/concept-tags",
            [new("GET", null), new("PUT", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),

        // ── Per-user stuff (the API scopes by X-User-Id) ─────────────────────────
        ["favorites"] = new("/api/favorites", [new("POST", null), new("DELETE", null)]),
        ["comments"] = new("/api/comments", [new("GET", null), new("POST", null), new("DELETE", null)]),
        ["agents"] = new("/api/agents", [new("GET", null), new("POST", null), new("DELETE", null)]),

        // ── Session / activity (GET-only passthroughs; PUT /session/settings stays explicit:
        //    it pushes live values into WebSessionSettingsService + the cookie options cache) ──
        ["session/status"] = new("/api/session/status", [new("GET", null)]),
        ["session/settings"] = new("/api/session/settings", [new("GET", UserRoles.Superadmin)]),
        ["activity"] = new("/api/activity", [new("GET", null)]),
        ["maintenance"] = new("/api/session/status", [new("GET", null)]),

        // ── Sync (invisible GET: the API is superadmin-only and returns the same
        //    {isInvisible} shape the old Web wrapper did, so the entry mirrors that;
        //    the old Web gate was any-user only because the API's 403 used to surface
        //    as a quiet `false` — the table now states the real policy) ──
        ["sync/status"] = new("/api/sync/status", [new("GET", UserRoles.Superadmin)]),
        ["sync/delivery-status"] = new("/api/sync/delivery-status", [new("GET", UserRoles.Superadmin)]),
        ["sync/invisible"] = new("/api/sync/invisible",
            [new("GET", UserRoles.Superadmin), new("POST", null)]),

        // ── Snapshots / compaction / search admin ────────────────────────────────
        ["snapshots"] = new("/api/snapshots",
            [new("GET", null), new("POST", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["compact"] = new("/api/admin/compact", [new("GET", UserRoles.Superadmin)]),
        ["admin/search/embeddings-enabled"] = new("/api/admin/search/embeddings-enabled",
            [new("GET", UserRoles.Superadmin), new("PUT", UserRoles.Superadmin)]),
        ["admin/search/embeddings/backfill"] = new("/api/admin/search/embeddings/backfill",
            [new("POST", UserRoles.Superadmin)]),

        // ── Chat: JSON routes. The SSE passthroughs (chat/stream, chat/{id}/confirm) stay
        //    explicit — see ChatProxyEndpoints. Role split mirrors the old per-route gates. ──
        ["chat/models/all"] = new("/api/chat/models/all", [new("GET", UserRoles.Superadmin)]),
        ["chat/models"] = new("/api/chat/models",
            [new("GET", null), new("POST", UserRoles.Superadmin), new("PATCH", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["chat/keys"] = new("/api/chat/keys",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("PATCH", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["chat/settings/chat-enabled"] = new("/api/chat/settings/chat-enabled",
            [new("GET", UserRoles.Superadmin), new("PATCH", UserRoles.Superadmin)]),
        ["chat/settings/defaults"] = new("/api/chat/settings/defaults",
            [new("GET", UserRoles.Superadmin), new("PATCH", UserRoles.Superadmin)]),
        ["chat/settings/auto-approve"] = new("/api/chat/settings/auto-approve",
            [new("GET", null), new("PATCH", null)]),
        ["chat/settings/effective-text-model"] = new("/api/chat/settings/effective-text-model", [new("GET", null)]),
        ["chat/access"] = new("/api/chat/access", [new("GET", null)]),
        ["chat/conversations"] = new("/api/chat/conversations",
            [new("GET", null), new("PATCH", null), new("DELETE", null)]),
        ["chat/home-pinned"] = new("/api/chat/home-pinned", [new("GET", null), new("DELETE", null)]),
        ["chat/attachments"] = new("/api/chat/attachments", [new("GET", null)]),

        // ── User & role administration (superadmin; /me/* password change is per-user) ──
        ["users/me/change-password"] = new("/api/users/me/change-password", [new("POST", null)]),
        ["users"] = new("/api/users",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("PUT", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["restrictions"] = new("/api/restrictions",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("PATCH", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["roles"] = new("/api/roles",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("PUT", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
        ["hard-delete"] = new("/api/hard-delete",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin)]),
        ["keys"] = new("/api/keys", [new("POST", UserRoles.Superadmin)]),

        // ── Remote accounts: the whole API surface is superadmin-only (its group filter);
        //    the entries now state that instead of relaying a 403-shaped 502. ──
        ["remote-accounts"] = new("/api/remote-accounts",
            [new("GET", UserRoles.Superadmin), new("POST", UserRoles.Superadmin), new("DELETE", UserRoles.Superadmin)]),
    };

    /// <summary>
    /// Outcome of <see cref="Match"/>. <see cref="Matched"/> means a prefix matched (entry +
    /// rule carry the forwarding instructions); the two denials mean the request never leaves
    /// the Web process.
    /// </summary>
    public enum MatchOutcome
    {
        /// <summary>Prefix and method both listed — forward through the matched rule.</summary>
        Matched,

        /// <summary>No table entry claims this path — deny with 404 (deny-by-default).</summary>
        UnknownPrefix,

        /// <summary>Prefix listed but this method is not — deny with 405.</summary>
        MethodNotAllowed,
    }

    /// <summary>
    /// Read-only view over the table for tests and diagnostics: the Web-side role gate is
    /// data-driven, so the integration suite walks every entry and asserts the declared gate
    /// over real HTTP (one request per entry and method, as a regular user).
    /// </summary>
    public static IReadOnlyDictionary<string, ProxyRouteEntry> Entries => _entries;

    /// <summary>
    /// Longest-segment-prefix match plus the method rule. "concept-tags" matches
    /// "concept-tags" and "concept-tags/graph" but the match is segment-aware so
    /// "concept-tags-x" does NOT accidentally match "concept-tags". When several entries
    /// overlap (e.g. "media" and "media/upload", "session" and "session/settings") the
    /// longest prefix wins, so specific routes override broad ones.
    /// </summary>
    public static (MatchOutcome Outcome, ProxyRouteEntry? Entry, ProxyMethodRule? Rule, string? MatchedPrefix)
        Match(string proxyPath, string httpMethod)
    {
        if (string.IsNullOrEmpty(proxyPath)) return (MatchOutcome.UnknownPrefix, null, null, null);

        string? best = null;
        foreach (var key in _entries.Keys)
        {
            var equals = proxyPath.Equals(key, StringComparison.OrdinalIgnoreCase);
            var startsSegment = proxyPath.Length > key.Length
                                && proxyPath[key.Length] == '/'
                                && proxyPath.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase);
            if ((equals || startsSegment)
                && (best == null || key.Length > best.Length))
            {
                best = key;
            }
        }

        if (best == null) return (MatchOutcome.UnknownPrefix, null, null, null);

        var entry = _entries[best];
        var rule = entry.FindMethod(httpMethod);
        return rule != null
            ? (MatchOutcome.Matched, entry, rule, best)
            : (MatchOutcome.MethodNotAllowed, entry, null, best);
    }

    /// <summary>
    /// Rebuilds the upstream path by swapping the matched proxy prefix for the entry's
    /// upstream prefix and appending the remainder verbatim. E.g. proxy "concept-tags/graph"
    /// with prefix "concept-tags" → upstream "/api/concept-tags/graph"; proxy "media/upload"
    /// with prefix "media/upload" → upstream "/api/media/".
    /// </summary>
    public static string BuildUpstreamPath(string proxyPath, string matchedPrefix, ProxyRouteEntry entry)
    {
        var remainder = proxyPath.Length > matchedPrefix.Length
            ? proxyPath[matchedPrefix.Length..]
            : "";
        return entry.UpstreamPrefix + remainder;
    }
}
