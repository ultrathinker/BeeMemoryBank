using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class AutoUnlockEndpoints
{
    public static void MapAutoUnlockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/keys/auto-unlock")
            .WithTags("Keys")
            .RequireInternalKey();

        // GET /api/keys/auto-unlock/status  — returns whether the feature is enabled
        group.MapGet("/status", async (OsAutoUnlockService? svc) =>
        {
            if (svc == null || !OperatingSystem.IsWindows())
                return Results.Ok(new AutoUnlockStatusResponse(false, false));

            var enabled = await svc.IsEnabledAsync();
            return Results.Ok(new AutoUnlockStatusResponse(enabled, OperatingSystem.IsWindows()));
        });

        // POST /api/keys/auto-unlock/enable  — creates the os_auto_unlock slot + DPAPI secret
        group.MapPost("/enable", async (OsAutoUnlockService? svc, SessionService session, HttpContext ctx) =>
        {
            if (!OperatingSystem.IsWindows() || svc == null)
                return Results.Json(new ErrorResponse("OS auto-unlock is only supported on Windows."), statusCode: 400);

            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Only superadmin can enable OS auto-unlock."), statusCode: 403);

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: 403);

            var alreadyEnabled = await svc.IsEnabledAsync();
            if (alreadyEnabled)
                return Results.Json(new ErrorResponse("OS auto-unlock is already enabled."), statusCode: 409);

            await svc.EnableAsync();
            return Results.Ok(new AutoUnlockStatusResponse(true, true));
        }).RequireNonAgent();

        // POST /api/keys/auto-unlock/disable  — removes the os_auto_unlock slot + DPAPI secret
        group.MapPost("/disable", async (OsAutoUnlockService? svc, SessionService session, HttpContext ctx) =>
        {
            if (!OperatingSystem.IsWindows() || svc == null)
                return Results.Json(new ErrorResponse("OS auto-unlock is only supported on Windows."), statusCode: 400);

            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Only superadmin can disable OS auto-unlock."), statusCode: 403);

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: 403);

            await svc.DisableAsync();
            return Results.Ok(new AutoUnlockStatusResponse(false, true));
        }).RequireNonAgent();
    }
}
