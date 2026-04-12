using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/session").WithTags("Session");

        group.MapPost("/unlock", async (UnlockRequest req, SessionService session) =>
        {
            var success = await session.UnlockAsync(req.Password);
            return success
                ? Results.Ok(new SessionStatusResponse(true))
                : Results.Json(new ErrorResponse("Invalid password"), statusCode: 401);
        });

        group.MapPost("/login", async (LoginRequest req, SessionService session, UserService userService, IUserRepository userRepo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new ErrorResponse("Username and password are required"), statusCode: 400);

            // Bootstrap: if no users exist, try master password to create first superadmin
            var allUsers = await userRepo.ListActiveAsync();
            if (allUsers.Count == 0)
            {
                var unlocked = await session.UnlockAsync(req.Password);
                if (!unlocked)
                    return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 401);

                var admin = await userService.CreateUserAsync(req.Username.Trim(), req.Username.Trim(), req.Password, "superadmin");
                return Results.Ok(new LoginResponse(admin.Id, admin.Username, admin.DisplayName, admin.Role, true));
            }

            var user = await userService.AuthenticateAsync(req.Username, req.Password);
            if (user == null)
                return Results.Json(new ErrorResponse("Invalid username or password"), statusCode: 401);

            bool isUnlocked = session.IsUnlocked;

            if (user.Role is "superadmin" or "unlocker")
            {
                if (!isUnlocked)
                {
                    isUnlocked = await session.UnlockAsync(req.Password);
                }
            }
            else
            {
                if (!isUnlocked)
                    return Results.Json(new ErrorResponse("Server is locked. Contact administrator."), statusCode: 403);
            }

            return Results.Ok(new LoginResponse(user.Id, user.Username, user.DisplayName, user.Role, isUnlocked));
        });

        group.MapPost("/lock", (SessionService session, HttpContext ctx) =>
        {
            var actorType = ctx.Items["ActorType"] as string;
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();

            if (actorType != "agent" && role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can lock the server"), statusCode: 403);

            if (actorType != "agent" && !InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Only superadmin can lock the server"), statusCode: 403);

            session.Lock();
            return Results.Ok(new SessionStatusResponse(false));
        });

        group.MapGet("/status", (SessionService session) =>
            Results.Ok(new SessionStatusResponse(session.IsUnlocked)));
    }
}
