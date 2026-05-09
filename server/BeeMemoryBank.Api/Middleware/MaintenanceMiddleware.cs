using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Middleware;

public partial class MaintenanceMiddleware(RequestDelegate next, MaintenanceModeService maintenance)
{
    private static readonly HashSet<string> AllowedPaths =
    [
        "/api/snapshots/restore",
        "/api/snapshots/restore/progress",
        "/api/snapshots/restore/continue-without-backup",
        "/api/snapshots/restore/cancel",
        "/api/session/unlock",
        // Distributed-seeding handshake: when WE are the originator and just entered
        // maintenance for our own apply-phase, peers still need to authenticate against us
        // to download the snapshot from /api/snapshots/restore/{guid}/file. Both endpoints
        // require Ed25519 signature verification anyway, so they're safe to expose.
        "/api/sync/challenge",
        "/api/sync/authenticate",
        "/health",
    ];

    // Distributed-seeding endpoint: /api/snapshots/restore/{guid}/file
    // Strict regex prevents arbitrary subpaths like /restore/../foo/file from sneaking through.
    [GeneratedRegex(@"^/api/snapshots/restore/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/file$")]
    private static partial Regex RestoreFileRegex();

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var isAllowed = AllowedPaths.Contains(path)
                        || RestoreFileRegex().IsMatch(path)
                        // DEK rotation drives the maintenance state itself; all sub-paths must remain
                        // reachable so admins can observe progress, cancel, or retry while the node is in maintenance.
                        || path.StartsWith("/api/dek-rotation/")
                        || path.StartsWith("/_framework/")
                        || path.StartsWith("/lib/")
                        || path.StartsWith("/css/")
                        || path.StartsWith("/js/")
                        || path == "/Login"
                        || path == "/";

        if (maintenance.IsInMaintenance && !isAllowed)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "System is in maintenance mode",
                reason = maintenance.Reason
            }));
            return;
        }

        await next(ctx);
    }
}
