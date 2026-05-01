using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class KeyEndpoints
{
    public static void MapKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/keys").WithTags("Keys");

        group.MapPost("/change-password", async (ChangePasswordRequest req, KeyManagementService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            await svc.ChangePasswordAsync(req.OldPassword, req.NewPassword);
            return Results.Ok();
        });

        group.MapPost("/add-recovery", async (KeyManagementService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var key = await svc.AddRecoveryKeyAsync();
            return Results.Ok(new RecoveryKeyResponse(key));
        });
    }
}
