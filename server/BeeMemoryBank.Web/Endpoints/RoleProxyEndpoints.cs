using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Browser-facing proxy for role management. Every route is superadmin-gated here as well as in
/// the API — the API is the authority, this layer just avoids round-tripping an obvious 403.
/// <para>
/// Errors are forwarded with the API's own status code and message. Role operations refuse
/// things the operator has to read ("that name is reserved", "3 users still have this role"),
/// and collapsing those into a 502 would leave the UI unable to explain itself.
/// </para>
/// </summary>
public static class RoleProxyEndpoints
{
    public static void MapRoleProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api-proxy/roles", async (ApiClient api) =>
        {
            var roles = await api.GetRolesAsync();
            return roles != null ? Results.Ok(roles) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/roles", async (HttpContext ctx, ApiClient api) =>
        {
            // ReadFromJsonAsync throws on a malformed body, and on .NET 9+ also when a
            // non-optional constructor parameter is absent from the JSON. Unhandled, that becomes
            // a 500 with an EMPTY body — which the browser cannot read an error out of, so the
            // dialog falls back to a bare "Request failed" and the operator learns nothing.
            CreateRoleProxyRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<CreateRoleProxyRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Json(new { error = "Malformed request." }, statusCode: 400);
            }
            if (req == null) return Results.Json(new { error = "Empty request." }, statusCode: 400);

            var (role, error, status) = await api.CreateRoleAsync(req.Name, req.DisplayName, req.Description, req.BasePolicy);
            return role != null ? Results.Ok(role) : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPut("/api-proxy/roles/{name}", async (string name, HttpContext ctx, ApiClient api) =>
        {
            UpdateRoleProxyRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<UpdateRoleProxyRequest>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Json(new { error = "Malformed request." }, statusCode: 400);
            }
            if (req == null) return Results.Json(new { error = "Empty request." }, statusCode: 400);

            var (ok, error, status) = await api.UpdateRoleAsync(name, req.DisplayName, req.Description, req.BasePolicy);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/roles/{name}", async (string name, ApiClient api) =>
        {
            var (ok, error, status) = await api.DeleteRoleAsync(name);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── Role folder rules ──────────────────────────────────────────────
        // Deliberately the same shape as /api-proxy/restrictions/user/{id}, so the folder-access
        // dialog on the Users page drives both by swapping one path segment.

        app.MapGet("/api-proxy/restrictions/role/{roleName}", async (string roleName, ApiClient api) =>
        {
            var rules = await api.GetRoleRestrictionsAsync(roleName);
            return rules != null ? Results.Ok(rules) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/restrictions/role/{roleName}", async (string roleName, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<AddAclEntryProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (entry, error, status) = await api.AddRoleRestrictionAsync(roleName, req.FolderId, req.Effect, req.IsReadOnly);
            return entry != null ? Results.Ok(entry) : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapMethods("/api-proxy/restrictions/role/{roleName}/{folderId:guid}", new[] { "PATCH" },
            async (string roleName, Guid folderId, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UpdateAclReadOnlyProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, error, status) = await api.SetRoleRestrictionReadOnlyAsync(roleName, folderId, req.IsReadOnly);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/restrictions/role/{roleName}/{folderId:guid}", async (string roleName, Guid folderId, ApiClient api) =>
        {
            var (ok, error, status) = await api.RemoveRoleRestrictionAsync(roleName, folderId);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));
    }
}
