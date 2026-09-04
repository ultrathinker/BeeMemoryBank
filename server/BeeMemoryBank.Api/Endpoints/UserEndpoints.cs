// User management endpoints. Users are node-local: creating a user here
// does NOT propagate to other nodes. See docs/architecture.md → Node Topology.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireInternalKey();
        group.AddEndpointFilter<RequireNonAgentFilter>();

        group.MapGet("/", async (IUserRepository repo, HttpContext ctx) =>
        {
            var users = await repo.ListActiveAsync();
            return Results.Ok(users.Select(u => new UserListItemResponse(
                u.Id, u.Username, u.DisplayName, u.Role, u.CreatedAt, u.LastLoginAt, u.ChatAccess)));
        });

        // GET /api/users/me/stamp — returns this caller's node-local security stamp.
        // Used by the Web layer's OnValidatePrincipal to revoke stale cookies after an
        // identity-affecting change (password/role change, deletion). The forwarded
        // X-User-Id identifies the caller; the group's RequireInternalKey filter already
        // authenticated the internal key, and RequireNonAgentFilter blocks agents.
        group.MapGet("/me/stamp", async (HttpContext ctx, IUserRepository repo) =>
        {
            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (!int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);

            var stamp = await repo.GetSecurityStampAsync(userId);
            if (stamp == null)
                return Results.NotFound();
            return Results.Ok(new { stamp });
        });

        group.MapPost("/", async (CreateUserRequest req, IUserRepository userRepo, UserService userService, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                var user = await userService.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role, req.ChatAccess);
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                await auditRepo.LogAsync("user", user.Id.ToString(), "user_created", "web",
                    $"User '{user.Username}' (role={user.Role}) created by user {actor}");
                return Results.Ok(new UserListItemResponse(user.Id, user.Username, user.DisplayName, user.Role, user.CreatedAt, user.LastLoginAt, user.ChatAccess));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        }).RequireSuperadmin();

        group.MapPut("/{id:int}", async (int id, UpdateUserRequest req, IUserRepository userRepo, UserService userService, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                var appliedPw = await userService.UpdateUserAsync(id, req.DisplayName, req.Role, req.Password, req.ChatAccess);
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                // Report what actually happened, not what was asked: a password is only applied
                // when it provisions a key slot for a promotion, and is ignored otherwise.
                await auditRepo.LogAsync("user", id.ToString(), "user_updated", "web",
                    $"User #{id} updated (role={req.Role}, displayName changed={req.DisplayName != null}, password changed={appliedPw}) by user {actor}");
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
            // Rejected role changes (last superadmin, last key slot) are the caller's problem,
            // not a server fault — without this they surfaced as a 500 with no usable message.
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        }).RequireSuperadmin();

        group.MapDelete("/{id:int}", async (int id, UserService userService, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                await userService.DeleteUserAsync(id);
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                await auditRepo.LogAsync("user", id.ToString(), "user_deleted", "web", $"User #{id} deleted by user {actor}");
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Json(new ErrorResponse("Cannot delete user: they own one or more agents. Delete the agents first."), statusCode: 409);
            }
            // "Cannot delete the last superadmin" / "their key slot is the only remaining way to
            // unlock the vault" — refusals the caller can act on, previously surfacing as a 500.
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        }).RequireSuperadmin();

        // Self-service password change — any authenticated user can change their own password.
        // Not RequireSuperadmin: the rule here is "acting on yourself", which the filter cannot
        // express — it is the forwarded X-User-Id, not the role, that decides who is affected.
        group.MapPost("/me/change-password", async (ChangePasswordRequest req, UserService userService, HttpContext ctx) =>
        {
            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);

            try
            {
                await userService.ChangePasswordAsync(userId, req.OldPassword, req.NewPassword);
                return Results.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Incorrect current password"), statusCode: 403);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
        });

        group.MapPost("/{id:int}/change-password", async (int id, ChangeUserPasswordRequest req, UserService userService, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                await userService.AdminChangePasswordAsync(id, req.NewPassword);
                // Admin password reset bypasses old-password verification — high-impact
                // and silent to the target user. Logged so target/reviewer can spot it.
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                await auditRepo.LogAsync("user", id.ToString(), "user_password_admin_reset", "web",
                    $"Admin reset password for user #{id} by user {actor}");
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
        }).RequireSuperadmin();
    }
}
