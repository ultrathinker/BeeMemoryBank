using System.Text.Json;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Middleware;

public class MaintenanceMiddleware(RequestDelegate next, MaintenanceModeService maintenance)
{
    private static readonly HashSet<string> AllowedPaths =
    [
        "/api/snapshots/restore",
        "/api/session/unlock",
        "/health",
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (maintenance.IsInMaintenance && !AllowedPaths.Contains(ctx.Request.Path.Value ?? string.Empty))
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
