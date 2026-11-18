// User management endpoints. Users are node-local: creating a user here
// does NOT propagate to other nodes. See docs/architecture.md → Node Topology.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class UserEndpoints
{
    /// <summary>The answer to a demotion or deletion of a superadmin that does not carry the administrator's password: nothing was changed.</summary>
    internal const string RemovalNeedsPassword =
        "Removing a superadmin is a process that locks the person out at once and closes the account at its end. Start it from the Users page, " +
        "or send your own password with the request (adminPassword). Nothing was changed.";

    private static async Task<DeleteUserRequest?> ReadDeleteBodyAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is null or 0 && !ctx.Request.Headers.ContainsKey("Transfer-Encoding")) return null;
        try { return await ctx.Request.ReadFromJsonAsync<DeleteUserRequest>(ctx.RequestAborted); }
        catch (System.Text.Json.JsonException) { return null; }
        catch (InvalidOperationException) { return null; }   // not JSON
    }

    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireInternalKey();
        group.AddEndpointFilter<RequireNonAgentFilter>();

        // The node's whole roster — username, display name, role, last login, chat access — for
        // every active user. It answered any signed-in caller until now, which handed a regular
        // user the list of who the superadmins are and when each of them last signed in: a
        // ready-made target list for password guessing. Nothing loses access by gating it, because
        // the only consumers are already superadmin-only (the Users and Admin pages, and the
        // /api-proxy/users route that backs them).
        group.MapGet("/", async (IUserRepository repo, HttpContext ctx) =>
        {
            var users = await repo.ListActiveAsync();
            // An account a removal holds says so ("being removed, step k of n"): never "removed" or "user" while a way back is still open (decision 4).
            var removals = ctx.RequestServices.GetService<OffboardingService>() is { } offboarding
                ? await offboarding.OpenByPersonAsync(ctx.RequestAborted)
                : new Dictionary<Guid, OffboardingView>();
            return Results.Ok(users.Select(u => new UserListItemResponse(
                u.Id, u.Username, u.DisplayName, u.Role, u.CreatedAt, u.LastLoginAt, u.ChatAccess, u.PersonId,
                u.AccountState, removals.TryGetValue(u.PersonId, out var removal) ? removal.Status : null,
                removals.TryGetValue(u.PersonId, out var r2) ? r2.ProcessId : null, u.Replicated)));
        }).RequireSuperadmin();

        // GET /api/users/me/stamp — returns this caller's node-local security stamp.
        // Used by the Web layer's OnValidatePrincipal to revoke stale cookies after an
        // identity-affecting change (password/role change, deletion). The forwarded
        // X-User-Id identifies the caller; the group's RequireInternalKey filter already
        // authenticated the internal key, and RequireNonAgentFilter blocks agents.
        // Deliberately NOT RequireSuperadmin, unlike the listing above: the subject is always the
        // caller, and every signed-in user's cookie is revalidated through here on every request —
        // gating it would sign out everyone who is not a superadmin.
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
                // Mandatory at every entry point that has a person: there is no e-mail recovery, so the sign-in page
                // must be able to show a hint. The service keeps accepting null for seeds and tools.
                if (PasswordHintPolicy.Validate(req.PasswordHint, req.Password, req.Username) is { } hintProblem)
                    return Results.BadRequest(new ErrorResponse(hintProblem));

                var user = await userService.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role, req.ChatAccess, req.PasswordHint);
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
                var target = await userRepo.GetByIdAsync(id);
                if (target is null) return Results.NotFound(new ErrorResponse("User not found"));

                // A promotion that sets a password is also a password creation point, so it needs a hint too.
                if (!string.IsNullOrWhiteSpace(req.Password)
                    && PasswordHintPolicy.Validate(req.PasswordHint, req.Password, target.Username) is { } hintProblem)
                    return Results.BadRequest(new ErrorResponse(hintProblem));

                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";

                // Taking superadmin rights away is a removal process (release 2.12.0, plan E1): it STARTS here, with the administrator's own password, and
                // the account changes inside it (locked out at once, an ordinary user only at its end). Without the password nothing is touched.
                if (target.Role == UserRoles.Superadmin
                    && !string.IsNullOrWhiteSpace(req.Role)
                    && !string.Equals(req.Role.Trim(), UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(req.AdminPassword))
                        return Results.Json(new ErrorResponse(RemovalNeedsPassword), statusCode: 409);
                    return await OffboardingEndpoints.RunAsync(async () =>
                    {
                        var service = ctx.RequestServices.GetRequiredService<OffboardingService>();
                        var acting = await OffboardingEndpoints.ActorAsync(ctx, userRepo, null);
                        var processId = await service.StartAsync(new OffboardingStartRequest(id, "demote", req.Role.Trim(),
                            OffboardingEndpoints.ToRules(req.FolderRules), req.AllowOpenRole == true, req.AdminPassword), acting, ctx.RequestAborted);
                        return Results.Accepted($"/api/offboarding/{processId:D}", await service.GetAsync(processId));
                    });
                }

                var appliedPw = await userService.UpdateUserAsync(id, req.DisplayName, req.Role, req.Password, req.ChatAccess, req.PasswordHint);
                // Report what actually happened, not what was asked: a password is only applied
                // when it provisions a key slot for a promotion, and is ignored otherwise; the role is the canonical one the account now has, and a
                // change of role is named as such (the review of a removal reads "role changed from X to superadmin").
                var applied = (await userRepo.GetByIdAsync(id))?.Role ?? target.Role;
                var roleChange = applied != target.Role ? $", role changed from {target.Role} to {applied}" : "";
                await auditRepo.LogAsync("user", id.ToString(), "user_updated", "web",
                    $"User #{id} updated (role={applied}{roleChange}, displayName changed={req.DisplayName != null}, password changed={appliedPw}) by user {actor}");
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

        group.MapDelete("/{id:int}", async (int id, UserService userService, IUserRepository userRepo, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                // Deleting a superadmin is a removal process (release 2.12.0, plan E1): it starts with the administrator's own password (JSON body with
                // adminPassword); the account is locked out at once and deleted at the end. An ordinary user's deletion stays immediate.
                if (await userRepo.GetByIdAsync(id) is { Role: UserRoles.Superadmin })
                {
                    var body = await ReadDeleteBodyAsync(ctx);
                    if (body is null || string.IsNullOrEmpty(body.AdminPassword))
                        return Results.Json(new ErrorResponse(RemovalNeedsPassword), statusCode: 409);
                    return await OffboardingEndpoints.RunAsync(async () =>
                    {
                        var service = ctx.RequestServices.GetRequiredService<OffboardingService>();
                        var acting = await OffboardingEndpoints.ActorAsync(ctx, userRepo, body.ActingUser);
                        var processId = await service.StartAsync(new OffboardingStartRequest(id, "delete", null, null, false, body.AdminPassword), acting, ctx.RequestAborted);
                        return Results.Accepted($"/api/offboarding/{processId:D}", await service.GetAsync(processId));
                    });
                }

                await userService.DeleteUserAsync(id, CallerIdentity.Extract(ctx).UserId?.ToString());
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

        // GET /api/users/me/password-hint — the signed-in caller's own hint (null when they have none). The person wrote
        // it for themselves and it is already shown to anyone who types their name on the sign-in page, so reading it
        // back needs no confirmation.
        group.MapGet("/me/password-hint", async (HttpContext ctx, IUserRepository users) =>
        {
            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (!int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);
            var user = await users.GetByIdAsync(userId);
            if (user is null) return Results.NotFound(new ErrorResponse("User not found"));
            return Results.Ok(new CurrentPasswordHintResponse(await users.GetPasswordHintAsync(user.Username)));
        });

        // POST /api/users/me/password-hint — the Profile page's soft "Add a password hint" for accounts created before
        // hints existed (and for a hint that no longer fits). Nothing is forced on such an account until its next
        // password change; this lets its owner close the gap now. Any signed-in user, acting on themselves only.
        group.MapPost("/me/password-hint", async (SetPasswordHintRequest req, HttpContext ctx, UserService userService) =>
        {
            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (!int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);
            try
            {
                await userService.SetPasswordHintAsync(userId, req.PasswordHint);
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (ReplicatedUserException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        // Self-service password change — any authenticated user can change their own password.
        // Not RequireSuperadmin: the rule here is "acting on yourself", which the filter cannot
        // express — it is the forwarded X-User-Id, not the role, that decides who is affected.
        group.MapPost("/me/change-password", async (ChangePasswordRequest req, UserService userService, HttpContext ctx, IUserRepository users, RecoveryTriggers recovery) =>
        {
            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);

            try
            {
                var caller = await users.GetByIdAsync(userId);
                if (caller is null) return Results.NotFound(new ErrorResponse("User not found"));
                if (PasswordHintPolicy.Validate(req.PasswordHint, req.NewPassword, caller.Username) is { } hintProblem)
                    return Results.BadRequest(new ErrorResponse(hintProblem));

                using var stop = BeeMemoryBank.Api.Helpers.RequestCancellation.Linked(ctx);
                await userService.ChangePasswordAsync(userId, req.OldPassword, req.NewPassword, stop.Token, req.PasswordHint);
                // A superadmin's password is a master password: rebuild this node's strong recovery
                // box with it while it is in memory (plan 6.3). Background; the response does not wait.
                if (caller.KeySlotId != null)
                    _ = recovery.OnPasswordChanged(req.NewPassword);
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
        })
        // A password replacement is Argon2 and a rewrite of the key slot: once at a time per user, whoever starts it (the user, or a superadmin for them), so that
        // two of them never race over one slot (BMB-311). The same key as the admin route below.
        .SingleFlight("user-password:{userId}", "The password change of this user", ctx =>
            int.TryParse(ctx.HttpContext.Request.Headers["X-User-Id"].FirstOrDefault(), out var userId) ? "user-password:" + userId : null);

        group.MapPost("/{id:int}/change-password", async (int id, ChangeUserPasswordRequest req, UserService userService, IUserRepository userRepo, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            try
            {
                var target = await userRepo.GetByIdAsync(id);
                if (target is null) return Results.NotFound(new ErrorResponse("User not found"));
                if (PasswordHintPolicy.Validate(req.PasswordHint, req.NewPassword, target.Username) is { } hintProblem)
                    return Results.BadRequest(new ErrorResponse(hintProblem));

                using var stop = BeeMemoryBank.Api.Helpers.RequestCancellation.Linked(ctx);
                await userService.AdminChangePasswordAsync(id, req.NewPassword, stop.Token, req.PasswordHint);
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
        }).RequireSuperadmin().SingleFlight("user-password:{id}", "The password change of this user");
    }
}
