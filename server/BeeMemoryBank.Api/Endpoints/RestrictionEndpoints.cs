// Folder ACL entry endpoints. ACL entries are node-local: they
// live only on this node and are not propagated via sync. See
// docs/architecture.md → Node Topology.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class RestrictionEndpoints
{
    public static void MapRestrictionEndpoints(this WebApplication app)
    {
        var userGroup = app.MapGroup("/api/restrictions/user").WithTags("Restrictions").RequireInternalKey();
        // Same boundary /api/users, /api/roles and the role routes below enforce: an agent bearer
        // token must never be able to edit permissions, even when its owner is a superadmin.
        userGroup.AddEndpointFilter<RequireNonAgentFilter>();

        userGroup.MapGet("/{userId:int}", async (int userId, IFolderAclRepository repo, IFolderRepository folderRepo, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            var entries = await repo.GetByUserIdAsync(userId);
            var result = new List<object>();
            foreach (var e in entries)
            {
                var folder = await folderRepo.GetByIdAsync(e.FolderId);
                result.Add(new
                {
                    id = e.Id,
                    folderId = e.FolderId,
                    folderPath = folder?.Path ?? "(deleted)",
                    effect = e.Effect.ToString().ToLowerInvariant(),
                    isReadOnly = e.IsReadOnly,
                    createdAt = e.CreatedAt
                });
            }
            return Results.Ok(result);
        });

        userGroup.MapPost("/{userId:int}", async (int userId, AddAclEntryRequest req, IFolderAclRepository repo, IFolderRepository folderRepo, IUserRepository userRepo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            if (!Enum.TryParse<AclEffect>(req.Effect, ignoreCase: true, out var effect))
                return Results.BadRequest(new ErrorResponse("Invalid effect. Use 'allow' or 'deny'."));

            var folder = await folderRepo.GetByIdAsync(req.FolderId);
            if (folder == null)
                return Results.Json(new ErrorResponse("Folder not found"), statusCode: 404);

            var target = await userRepo.GetByIdAsync(userId);
            if (target is null)
                return Results.Json(new ErrorResponse("User not found"), statusCode: 404);

            // Superadmins bypass every folder rule, so a row here would never be enforced — the
            // same reason rules are refused on the 'superadmin' role. Reads and deletes stay
            // open so an inert row left over from before a promotion can still be cleaned up.
            if (target.Role == UserRoles.Superadmin)
                return Results.Json(new ErrorResponse(
                    "Superadmins bypass all folder restrictions, so rules cannot be added for them. " +
                    "Give this person a different role first."), statusCode: 409);

            // Rules already on a custom-role user keep applying (what exists, applies — silently
            // inert rows are worse than visible ones), but new ones are refused: their
            // permissions are supposed to be readable off the role in one place.
            if (target.Role != UserRoles.User)
                return Results.Json(new ErrorResponse(
                    $"This user's role is '{target.Role}'. Edit that role's folder rules instead — " +
                    "per-user rules are only for users on the built-in 'user' role."), statusCode: 409);

            // is_read_only is only meaningful for allow-entries; deny-entries ignore it.
            var isReadOnly = effect == AclEffect.Allow && req.IsReadOnly;
            var entry = new FolderAclEntry
            {
                UserId = userId,
                FolderId = req.FolderId,
                Effect = effect,
                IsReadOnly = isReadOnly,
                CreatedAt = DateTime.UtcNow
            };
            try
            {
                await repo.AddAsync(entry);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Json(new ErrorResponse("ACL entry already exists"), statusCode: 409);
            }
            folderAccess.InvalidateCache(userId);
            return Results.Ok(new
            {
                entry.Id,
                folderId = entry.FolderId,
                folderPath = folder.Path,
                effect = entry.Effect.ToString().ToLowerInvariant(),
                isReadOnly = entry.IsReadOnly
            });
        });

        // Toggle is_read_only on an existing allow-entry.
        userGroup.MapPatch("/{userId:int}/{folderId:guid}", async (
            int userId,
            Guid folderId,
            UpdateAclReadOnlyRequest req,
            IFolderAclRepository repo,
            FolderAccessService folderAccess,
            HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            // Only allow-entries can be RO; deny-entries are unaffected.
            await repo.SetReadOnlyAsync(userId, folderId, AclEffect.Allow, req.IsReadOnly);
            folderAccess.InvalidateCache(userId);
            return Results.NoContent();
        });

        userGroup.MapDelete("/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, IFolderAclRepository repo, FolderAccessService folderAccess, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

            await repo.RemoveByUserAndFolderAsync(userId, folderId);
            folderAccess.InvalidateCache(userId);
            return Results.NoContent();
        });

        // ---- role-scoped rules --------------------------------------------------------
        // Same shape as the user routes above (/api/restrictions/{type}/{id}) so the Web
        // folder-access dialog, which is already parameterised by type, drives both.
        var roleGroup = app.MapGroup("/api/restrictions/role").WithTags("Restrictions").RequireInternalKey();
        roleGroup.AddEndpointFilter<RequireNonAgentFilter>();

        roleGroup.MapGet("/{roleName}", async (string roleName, RoleService roles, HttpContext ctx) =>
        {
            if (!RoleEndpoints.IsSuperadmin(ctx)) return RoleEndpoints.Forbidden();

            return await RoleEndpoints.TranslateAsync(async () =>
            {
                var rules = await roles.ListRulesAsync(roleName);
                return Results.Ok(rules.Select(r => new
                {
                    roleName = r.Entry.RoleName,
                    folderId = r.Entry.FolderId,
                    folderPath = r.FolderPath,
                    effect = r.Entry.Effect.ToString().ToLowerInvariant(),
                    isReadOnly = r.Entry.IsReadOnly,
                    createdAt = r.Entry.CreatedAt
                }));
            });
        });

        roleGroup.MapPost("/{roleName}", async (string roleName, AddAclEntryRequest req, RoleService roles, IFolderRepository folderRepo, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!RoleEndpoints.IsSuperadmin(ctx)) return RoleEndpoints.Forbidden();

            if (!Enum.TryParse<AclEffect>(req.Effect, ignoreCase: true, out var effect))
                return Results.BadRequest(new ErrorResponse("Invalid effect. Use 'allow' or 'deny'."));

            return await RoleEndpoints.TranslateAsync(async () =>
            {
                RoleAclEntry entry;
                try
                {
                    entry = await roles.AddRuleAsync(roleName, req.FolderId, effect, req.IsReadOnly);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // Lost the race against a concurrent identical add. RoleService pre-checks for
                    // this; only a simultaneous request reaches here.
                    return Results.Json(new ErrorResponse(
                        $"Role '{roleName}' already has a {req.Effect.ToLowerInvariant()} rule on that folder."),
                        statusCode: 409);
                }

                await audit.LogAsync("role", entry.RoleName, "role_rule_added", "web",
                    $"Rule {entry.Effect.ToString().ToLowerInvariant()} on folder {entry.FolderId} " +
                    $"added to role '{entry.RoleName}' by user {RoleEndpoints.Actor(ctx)}");
                // Same field set as the GET above: the Web client deserializes both into one DTO,
                // and a response missing folderPath/createdAt silently yields null/default there.
                var folder = await folderRepo.GetByIdAsync(entry.FolderId, includeDeleted: true);
                return Results.Ok(new
                {
                    roleName = entry.RoleName,
                    folderId = entry.FolderId,
                    folderPath = folder?.Path ?? "(deleted)",
                    effect = entry.Effect.ToString().ToLowerInvariant(),
                    isReadOnly = entry.IsReadOnly,
                    createdAt = entry.CreatedAt
                });
            });
        });

        roleGroup.MapPatch("/{roleName}/{folderId:guid}", async (string roleName, Guid folderId, UpdateAclReadOnlyRequest req, RoleService roles, HttpContext ctx) =>
        {
            if (!RoleEndpoints.IsSuperadmin(ctx)) return RoleEndpoints.Forbidden();

            return await RoleEndpoints.TranslateAsync(async () =>
            {
                await roles.SetRuleReadOnlyAsync(roleName, folderId, req.IsReadOnly);
                return Results.NoContent();
            });
        });

        roleGroup.MapDelete("/{roleName}/{folderId:guid}", async (string roleName, Guid folderId, RoleService roles, IAuditLogRepository audit, HttpContext ctx) =>
        {
            if (!RoleEndpoints.IsSuperadmin(ctx)) return RoleEndpoints.Forbidden();

            return await RoleEndpoints.TranslateAsync(async () =>
            {
                await roles.RemoveRuleAsync(roleName, folderId);
                await audit.LogAsync("role", roleName, "role_rule_removed", "web",
                    $"Rule on folder {folderId} removed from role '{roleName}' by user {RoleEndpoints.Actor(ctx)}");
                return Results.NoContent();
            });
        });
    }
}
