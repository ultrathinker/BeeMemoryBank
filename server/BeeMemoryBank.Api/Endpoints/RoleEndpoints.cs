// Role management endpoints. Roles are node-local, exactly like the users that hold them and
// the ACL rows they carry: creating a role here does NOT propagate to other nodes.
// See docs/architecture.md → Node Topology.

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/roles").WithTags("Roles").RequireInternalKey();
        group.AddEndpointFilter<RequireNonAgentFilter>();

        // Superadmin-only like everything else here: the only consumer is the admin Users/Roles
        // UI, which is itself superadmin-gated, and the list would otherwise tell a regular user
        // how the organisation is segmented.
        group.MapGet("/", async (RoleService roles, HttpContext ctx) =>
        {
            if (!IsSuperadmin(ctx)) return Forbidden();

            var summaries = await roles.ListAsync();
            return Results.Ok(summaries.Select(s => new RoleResponse(
                s.Role.Name, s.Role.DisplayName, s.Role.Description, s.Role.IsSystem,
                s.Role.BasePolicy, s.UserCount, s.RuleCount, s.Role.CreatedAt, s.Role.UpdatedAt)));
        });

        group.MapGet("/{name}", async (string name, RoleService roles, HttpContext ctx) =>
        {
            if (!IsSuperadmin(ctx)) return Forbidden();

            var role = await roles.GetAsync(name);
            if (role is null) return Results.NotFound(new ErrorResponse("Role not found"));

            return Results.Ok(new RoleResponse(
                role.Name, role.DisplayName, role.Description, role.IsSystem,
                role.BasePolicy, 0, 0, role.CreatedAt, role.UpdatedAt));
        });

        group.MapPost("/", async (CreateRoleRequest req, RoleService roles, HttpContext ctx, IAuditLogRepository audit) =>
        {
            if (!IsSuperadmin(ctx)) return Forbidden();

            return await TranslateAsync(async () =>
            {
                var role = await roles.CreateAsync(req.Name, req.DisplayName, req.Description, req.BasePolicy);
                await audit.LogAsync("role", role.Name, "role_created", "web",
                    $"Role '{role.Name}' (base policy={role.BasePolicy}) created by user {Actor(ctx)}");
                return Results.Ok(new RoleResponse(
                    role.Name, role.DisplayName, role.Description, role.IsSystem,
                    role.BasePolicy, 0, 0, role.CreatedAt, role.UpdatedAt));
            });
        });

        group.MapPut("/{name}", async (string name, UpdateRoleRequest req, RoleService roles, HttpContext ctx, IAuditLogRepository audit) =>
        {
            if (!IsSuperadmin(ctx)) return Forbidden();

            return await TranslateAsync(async () =>
            {
                await roles.UpdateAsync(name, req.DisplayName, req.Description, req.BasePolicy);
                await audit.LogAsync("role", name, "role_updated", "web",
                    $"Role '{name}' updated (base policy={req.BasePolicy}) by user {Actor(ctx)}");
                return Results.NoContent();
            });
        });

        group.MapDelete("/{name}", async (string name, RoleService roles, HttpContext ctx, IAuditLogRepository audit) =>
        {
            if (!IsSuperadmin(ctx)) return Forbidden();

            return await TranslateAsync(async () =>
            {
                await roles.DeleteAsync(name);
                await audit.LogAsync("role", name, "role_deleted", "web",
                    $"Role '{name}' deleted by user {Actor(ctx)}");
                return Results.NoContent();
            });
        });
    }

    internal static bool IsSuperadmin(HttpContext ctx)
        => ctx.Request.Headers["X-User-Role"].FirstOrDefault() == UserRoles.Superadmin;

    internal static IResult Forbidden()
        => Results.Json(new ErrorResponse("Forbidden"), statusCode: 403);

    internal static string Actor(HttpContext ctx)
        => ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";

    /// <summary>
    /// Maps the service layer's exception vocabulary onto status codes, the same way the user
    /// endpoints do: bad input is 400, a conflict with current state is 409, a missing row is
    /// 404. Anything else keeps bubbling up as a 500, so a genuine fault is not disguised as a
    /// caller error — including raw SQLite constraint violations, which RoleService translates
    /// itself precisely because only it knows which constraint was hit.
    /// </summary>
    internal static async Task<IResult> TranslateAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new ErrorResponse(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
        }
    }
}
