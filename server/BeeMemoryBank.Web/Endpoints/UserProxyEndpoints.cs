using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class UserProxyEndpoints
{
    public static void MapUserProxyEndpoints(this WebApplication app)
    {
        app.MapPost("/api-proxy/users/me/change-password", async (HttpContext ctx, ApiClient api) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<ChangeOwnPasswordProxyRequest>();
            if (body == null) return Results.BadRequest();
            var (ok, error) = await api.ChangeOwnPasswordAsync(body.OldPassword, body.NewPassword);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: 400);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/users", async (ApiClient api) =>
        {
            var users = await api.GetUsersAsync();
            return users != null ? Results.Ok(users) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/users", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CreateUserProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (user, error, status) = await api.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role, req.ChatAccess);
            if (user != null) return Results.Ok(user);
            return Results.Json(new { error = error ?? "Failed to create user" }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPut("/api-proxy/users/{id:int}", async (int id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UpdateUserProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, error, status) = await api.UpdateUserAsync(id, req.DisplayName, req.Role, req.ChatAccess);
            if (ok) return Results.Ok();
            return Results.Json(new { error = error ?? "Failed to update user" }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/users/{id:int}", async (int id, ApiClient api) =>
        {
            var (ok, err, status) = await api.DeleteUserAsync(id);
            if (ok) return Results.NoContent();
            return Results.Json(new { error = err ?? "Failed to delete user" }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/users/{id:int}/change-password", async (int id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<ChangeUserPasswordProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, error, status) = await api.ChangeUserPasswordAsync(id, req.NewPassword);
            if (ok) return Results.Ok();
            return Results.Json(new { error = error ?? "Failed to change password" }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── Folder Restrictions ────────────────────────────────────────────────────

        app.MapGet("/api-proxy/restrictions/user/{userId:int}", async (int userId, ApiClient api) =>
        {
            var restrictions = await api.GetUserRestrictionsAsync(userId);
            return restrictions != null ? Results.Ok(restrictions) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/restrictions/user/{userId:int}", async (int userId, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<AddAclEntryProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (entry, error, status) = await api.AddUserRestrictionAsync(userId, req.FolderId, req.Effect, req.IsReadOnly);
            return entry != null ? Results.Ok(entry) : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapMethods("/api-proxy/restrictions/user/{userId:int}/{folderId:guid}", new[] { "PATCH" },
            async (int userId, Guid folderId, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UpdateAclReadOnlyProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, error, status) = await api.SetUserRestrictionReadOnlyAsync(userId, folderId, req.IsReadOnly);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/restrictions/user/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, ApiClient api) =>
        {
            var (ok, error, status) = await api.RemoveUserRestrictionAsync(userId, folderId);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── Hard Delete ────────────────────────────────────────────────────────────

        app.MapGet("/api-proxy/hard-delete/list", async (int? page, int? pageSize, string? filter, HardDeleteStatusFilter? status, ApiClient api) =>
        {
            var result = await api.HardDeleteListAsync(page ?? 1, pageSize ?? 100, filter, status ?? HardDeleteStatusFilter.All);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/hard-delete/folder/preview", async (PreviewFolderRequest req, ApiClient api) =>
        {
            var result = await api.HardDeletePreviewFolderAsync(req.Path);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/hard-delete/article/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var result = await api.HardDeleteArticleAsync(id);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/hard-delete/folder", async (HardDeleteFolderRequest req, ApiClient api) =>
        {
            var result = await api.HardDeleteFolderAsync(req.Path);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/hard-delete/restore/article/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, error, body) = await api.RestoreArticleAsync(id);
            return ok ? Results.Ok(body) : Results.BadRequest(new { error });
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/hard-delete/restore/folder/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, error, body) = await api.RestoreFolderAsync(id);
            return ok ? Results.Ok(body) : Results.BadRequest(new { error });
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/hard-delete/audit", async (int page, int pageSize, ApiClient api) =>
        {
            var result = await api.HardDeleteAuditAsync(page, pageSize);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/agents", async (ApiClient api) =>
        {
            var agents = await api.GetAgentsAsync();
            return Results.Ok(agents ?? []);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/agents", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CreateAgentProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest();
            var result = await api.CreateAgentAsync(req.Name, req.Description);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/agents/{id:int}", async (int id, ApiClient api) =>
        {
            var ok = await api.DeleteAgentAsync(id);
            return ok ? Results.NoContent() : Results.StatusCode(502);
        }).RequireAuthorization();
    }
}
