using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// What a caller without the internal key is allowed to reach — peers, MCP agents, and the two
/// screens a browser can see before anyone has signed in.
///
/// <para>Until now nothing in the node itself knew this. "What is visible from the internet" was
/// decided entirely by whatever sat in front: an Apache vhost on a server deployment, a YARP route
/// table in the desktop node (<c>NodeFront</c>). Two lists, maintained by different people, in
/// different syntaxes, neither reviewed when an endpoint is added — and a mistake in either one
/// publishes <c>/api/session/unlock</c>, a password oracle that unlocks the vault for every user
/// and agent at once, straight to the internet. That has happened.</para>
///
/// <para>So the node now has its own answer, and the proxies become a second layer rather than the
/// only one. Anything not listed here answers 404 to a keyless caller — 404 and not 403, because
/// "this endpoint exists but you may not use it" is itself information about a node an attacker is
/// probing.</para>
///
/// <para>The rule is deliberately about the KEY, not about authentication in general: a request
/// carrying a valid internal key is the web layer, the desktop tray or the node itself, and those
/// see everything exactly as before. Peers and agents authenticate further inside — sync tokens,
/// <c>bee_</c> keys, the join password — and this list only decides whether they get to try.</para>
/// </summary>
public static class PublicSurface
{
    /// <summary>One reachable route. <c>Method</c> null means any method.</summary>
    /// <param name="Pattern">Path template. <c>{x}</c> matches one segment, <c>**</c> matches the
    /// rest of the path.</param>
    public sealed record Entry(string? Method, string Pattern);

    public static readonly Entry[] Entries =
    [
        // ── Liveness and version ────────────────────────────────────────────
        // Deliberately reachable: an operator checking a node from outside, and the container
        // healthcheck. Neither says anything about the vault's contents.
        new(null, "/health"),
        new("GET", "/api/version"),

        // ── MCP ─────────────────────────────────────────────────────────────
        // The whole point of the product: agents connect here with a bee_ key, which
        // AgentAuthMiddleware resolves. All methods — the transport uses POST, GET and DELETE.
        // The whole subtree, not just the root: the transport owns everything under /mcp and the
        // SDK maps it as one handler, so which sub-paths exist is its business and can change with
        // an SDK upgrade. Publishing the root alone would break agents on the next version.
        new(null, "/mcp"),
        new(null, "/mcp/**"),

        // ── Peer-to-peer sync ───────────────────────────────────────────────
        // Every one of these is authenticated by the sync handshake (Ed25519 challenge-response
        // issuing a bearer token), not by the internal key. A peer is by definition a caller that
        // does not have our internal key.
        //
        // Listed one by one rather than as "/api/sync/**", because that subtree is not uniform:
        // /api/sync/{status,ping,invisible,delivery-status,quarantine,quarantine/{id},probe} are
        // RequireInternalKey (most also RequireSuperadmin) operator routes that happen to share
        // the prefix. Publishing the wildcard let a keyless caller reach them and collect a 401
        // where every other unpublished path answers 404 — a small existence oracle, and exactly
        // the distinction the "404, not 403" rule at the top of this file exists to remove.
        //
        // The cost of an explicit list is that a NEW peer route has to be added here or peers get
        // a 404. That is the intended failure direction: forgetting makes sync visibly stop, while
        // forgetting under the wildcard silently published an admin route.
        new("GET", "/api/sync/identity"),
        new("GET", "/api/sync/sentinel"),
        new("POST", "/api/sync/challenge"),
        new("POST", "/api/sync/authenticate"),
        // GET pulls a page of events, POST receives a push. Same path, both peer-facing.
        new(null, "/api/sync/events"),
        new("GET", "/api/sync/snapshot/for-join"),
        new("POST", "/api/sync/report-position"),
        new("POST", "/api/sync/blobs"),
        new("POST", "/api/sync/blobs/check"),
        new("POST", "/api/sync/blobs/get"),
        // A peer asking us to probe a third node's reachability on its behalf.
        new("POST", "/api/sync/probe-relay"),

        // Joining a network. Authorised by the master password inside the handler; a joining node
        // has nothing else to present.
        new("POST", "/api/join"),

        // A peer pulling the snapshot for a network-wide restore we are hosting. Authenticated by
        // the same sync bearer token — see the handler, which checks it explicitly.
        new("GET", "/api/snapshots/restore/{eventId}/file"),

        // Restore progress. Anonymous on purpose so the locked splash screen can poll it, and the
        // handler already strips everything but a coarse status for callers without the key.
        new("GET", "/api/snapshots/restore/progress"),

        // ── Remote accounts (read-only mirrors on someone else's node) ──────
        // Designed to be called BY another person's node using a bmbrt_ token it was issued, so
        // they cannot require the internal key either. Note that being listed here does not
        // publish them: a reverse proxy still decides what it forwards, and the shipped
        // configurations do not forward these.
        new("POST", "/api/auth/remote-token"),
        new("GET", "/api/folders/accessible"),
        new("GET", "/api/folders/by-path/snapshot"),
    ];

    /// <summary>True when a caller with no internal key may reach this request.</summary>
    public static bool Allows(string method, PathString path)
    {
        if (!path.HasValue) return false;
        foreach (var entry in Entries)
        {
            if (entry.Method != null && !string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Matches(entry.Pattern, path.Value!)) return true;
        }
        return false;
    }

    private static bool Matches(string pattern, string path)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < patternSegments.Length; i++)
        {
            // "**" swallows the rest, but only if there IS a rest: "/api/sync/**" must not match a
            // bare "/api/sync", so publishing a subtree never publishes its root by accident.
            if (patternSegments[i] == "**")
                return pathSegments.Length > i;

            if (i >= pathSegments.Length) return false;

            var patternSegment = patternSegments[i];
            if (patternSegment.StartsWith('{') && patternSegment.EndsWith('}'))
                continue; // any one non-empty segment

            if (!string.Equals(patternSegment, pathSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return pathSegments.Length == patternSegments.Length;
    }
}
