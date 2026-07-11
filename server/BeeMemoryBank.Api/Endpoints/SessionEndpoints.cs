using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/session").WithTags("Session").RequireInternalKey();

        group.MapPost("/unlock", async (UnlockRequest req, SessionService session) =>
        {
            var success = await session.UnlockAsync(req.Password);
            if (!success)
                return Results.Json(new ErrorResponse("Invalid password"), statusCode: 401);

            var migratedSynthetic = session.LastMigrationResult?.Migrated == true
                ? session.LastMigrationResult!.SyntheticAdminUsername
                : null;
            return Results.Ok(new UnlockResponse(true, migratedSynthetic));
        }).RequireNonAgent().WithMetadata(new SkipInternalKey());

        group.MapPost("/login", async (LoginRequest req, SessionService session, UserService userService, IUserRepository userRepo) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Json(new ErrorResponse("Username and password are required"), statusCode: 400);

            var allUsers = await userRepo.ListActiveAsync();
            if (allUsers.Count == 0)
            {
                // Legacy-node bootstrap path: nodes upgraded from before Phase A1/A2 have a legacy
                // "password" key slot but no users in tbl_user yet. Trying to Unlock with the
                // entered password triggers LegacyPasswordSlotMigrationService, which promotes
                // the legacy slot to a "user" slot bound to a freshly-created synthetic admin.
                // We then return a LoginResponse for that synthetic user.
                // Wrong password → unlock fails → generic 401 like normal failed login.
                var unlocked = await session.UnlockAsync(req.Password);
                if (!unlocked)
                    return Results.Json(new ErrorResponse("Invalid username or password"), statusCode: 401);

                var migration = session.LastMigrationResult;
                if (migration?.Migrated == true && migration.SyntheticAdminUsername != null)
                {
                    var synthUser = await userRepo.GetByUsernameAsync(migration.SyntheticAdminUsername);
                    if (synthUser != null)
                        return Results.Ok(new LoginResponse(
                            synthUser.Id, synthUser.Username, synthUser.DisplayName,
                            synthUser.Role, true, migration.SyntheticAdminUsername, synthUser.SecurityStamp));
                }
                // Defensive: unlock succeeded but no user was created — fresh post-Setup nodes
                // never reach here (Setup creates a user), so this is the bare-disk-no-init case.
                return Results.Json(new ErrorResponse("Node not initialized. Complete setup first."), statusCode: 400);
            }

            var user = await userService.AuthenticateAsync(req.Username, req.Password);
            if (user == null)
                return Results.Json(new ErrorResponse("Invalid username or password"), statusCode: 401);

            bool isUnlocked = session.IsUnlocked;

            if (user.Role == UserRoles.Superadmin)
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

            var migratedSynthetic = session.LastMigrationResult?.Migrated == true
                ? session.LastMigrationResult!.SyntheticAdminUsername
                : null;

            return Results.Ok(new LoginResponse(user.Id, user.Username, user.DisplayName, user.Role, isUnlocked, migratedSynthetic, user.SecurityStamp));
        }).WithMetadata(new SkipInternalKey());

        group.MapPost("/lock", (SessionService session, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();

            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Only superadmin can lock the server"), statusCode: 403);

            session.Lock();
            return Results.Ok(new SessionStatusResponse(false));
        }).RequireNonAgent();

        group.MapGet("/status", (SessionService session) =>
            Results.Ok(new SessionStatusResponse(session.IsUnlocked))).WithMetadata(new SkipInternalKey());

        // Admin-configurable web login cookie lifetime (Web project applies these to its
        // own CookieAuthenticationOptions — see BeeWebCookie config in Web's Program.cs).
        // Bearer-token (agent) access never touches this cookie at all, so it is intentionally
        // NOT part of this setting's scope — RequireNonAgent below just keeps write access to
        // human superadmins via the browser, same as /lock.
        group.MapGet("/settings", async (INodeIdentityRepository nodeRepo) =>
        {
            var (hours, sliding) = await nodeRepo.GetSessionSettingsAsync();
            return Results.Ok(new SessionSettingsResponse(hours, sliding));
        });

        group.MapPut("/settings", async (SessionSettingsRequest req, INodeIdentityRepository nodeRepo, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Only superadmin can change session settings"), statusCode: 403);

            if (req.ExpireHours < 1 || req.ExpireHours > 24 * 30)
                return Results.Json(new ErrorResponse("expireHours must be between 1 and 720"), statusCode: 400);

            await nodeRepo.SetSessionSettingsAsync(req.ExpireHours, req.SlidingExpiration);
            return Results.Ok(new SessionSettingsResponse(req.ExpireHours, req.SlidingExpiration));
        }).RequireNonAgent();
    }
}
