using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Node;

/// <summary>
/// What each surface of the LAN listener serves (docs/design/lan-peer-sync.md section 5). The SYNC surface is the one a paired peer needs to sync with
/// this node directly: the node-key-authenticated peer routes and nothing else - no web UI, no MCP, no join, no invitations, no blind-node, restore or
/// remote-account routes. It is one list, a strict subset of <see cref="PublicSurface"/>, and every route but the four of the handshake needs a sync
/// token (tests hold both). The JOIN surface is the door of "Connect a device" (<see cref="LanJoinListener"/>); the WEB surface is the whole front
/// ("Devices on my network").
/// </summary>
public static class LanSurfaces
{
    /// <summary>The routes of the sync surface.</summary>
    public static readonly (string Method, string Path)[] Sync =
    [
        ("GET", "/api/sync/identity"),
        ("GET", "/api/sync/sentinel"),
        ("POST", "/api/sync/challenge"),
        ("POST", "/api/sync/authenticate"),
        ("GET", "/api/sync/events"),
        ("POST", "/api/sync/events"),
        ("POST", "/api/sync/report-position"),
        ("POST", "/api/sync/blobs"),
        ("POST", "/api/sync/blobs/check"),
        ("POST", "/api/sync/blobs/get"),
        ("GET", "/api/sync/my-standing"),
        ("POST", "/api/sync/leave"),
    ];

    /// <summary>The routes of the sync surface a caller reaches before it has a token: the handshake. Each is rate limited per address.</summary>
    public static readonly (string Method, string Path)[] Handshake =
    [
        ("GET", "/api/sync/identity"),
        ("GET", "/api/sync/sentinel"),
        ("POST", "/api/sync/challenge"),
        ("POST", "/api/sync/authenticate"),
    ];

    /// <summary>The sync routes whose body may be large (a page of pushed events, the blobs they name): allowed only with a bearer token.</summary>
    public static readonly (string Method, string Path)[] LargeBody =
    [
        ("POST", "/api/sync/events"),
        ("POST", "/api/sync/blobs"),
    ];

    /// <summary>The body limit of a handshake request and of any request that has no token: a challenge or a signature is a few hundred bytes.</summary>
    public const long PreAuthBodyLimit = 16 * 1024;

    /// <summary>The body limit of the other token routes (a list of blob hashes, a position report).</summary>
    public const long TokenBodyLimit = 2L * 1024 * 1024;

    /// <summary>The body limit of a push or a blob shipment with a token: the Api's own cap for one push request is 32 MB.</summary>
    public const long LargeBodyLimit = 40L * 1024 * 1024;

    public static bool IsSyncRoute(string method, PathString path) => Matches(Sync, method, path);

    public static bool IsHandshake(string method, PathString path) => Matches(Handshake, method, path);

    public static bool IsLargeBody(string method, PathString path) => Matches(LargeBody, method, path);

    private static bool Matches((string Method, string Path)[] routes, string method, PathString path) =>
        path.HasValue && routes.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Path, path.Value!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
