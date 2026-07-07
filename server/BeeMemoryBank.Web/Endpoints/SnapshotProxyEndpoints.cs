using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class SnapshotProxyEndpoints
{
    public static void MapSnapshotProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api-proxy/snapshots", async (ApiClient api) =>
        {
            var list = await api.GetSnapshotsAsync();
            return list != null ? Results.Ok(list) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/snapshots", async (ApiClient api) =>
        {
            var snap = await api.CreateSnapshotAsync();
            return snap != null ? Results.Ok(snap) : Results.StatusCode(502);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/snapshots/{fileName}", async (string fileName, ApiClient api) =>
        {
            var ok = await api.DeleteSnapshotAsync(fileName);
            return ok ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/compact/preview", async (ApiClient api) =>
        {
            var preview = await api.GetCompactionPreviewAsync();
            return preview != null ? Results.Ok(preview) : Results.NotFound();
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/compact/checkpoints", async (ApiClient api) =>
        {
            var cps = await api.GetSnapshotCheckpointsAsync();
            return cps != null ? Results.Ok(cps) : Results.NotFound();
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/activity", async (ApiClient api, int limit = 50, int offset = 0) =>
        {
            var result = await api.GetActivityAsync(limit, offset);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/activity/article/{articleId:guid}", async (Guid articleId, ApiClient api, int limit = 50) =>
        {
            var result = await api.GetActivityByArticleAsync(articleId, limit);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/comments", async (ApiClient api, Guid articleId) =>
        {
            var comments = await api.GetCommentsAsync(articleId);
            return comments != null ? Results.Ok(comments) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/comments", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<AddCommentProxyRequest>();
            if (req == null) return Results.BadRequest();
            var comment = await api.AddCommentAsync(req.ArticleId, req.Text);
            return comment != null ? Results.Ok(comment) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/comments/{id:int}", async (int id, ApiClient api) =>
        {
            var ok = await api.DeleteCommentAsync(id);
            return ok ? Results.NoContent() : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/session/status", async (ApiClient api) =>
        {
            var unlocked = await api.IsUnlockedAsync();
            return Results.Ok(new { isUnlocked = unlocked });
        }).RequireAuthorization();

        app.MapGet("/api-proxy/sync/status", async (ApiClient api) =>
        {
            // W2: pass upstream status + body through verbatim (was: null → 502, hiding real errors).
            var f = await api.ForwardGetAsync("sync/status");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/sync/delivery-status", async (ApiClient api) =>
        {
            // W2: pass upstream status + body through verbatim (was: null → 502).
            var f = await api.ForwardGetAsync("sync/delivery-status");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapGet("/api-proxy/sync/invisible", async (ApiClient api) =>
        {
            var isInvisible = await api.GetInvisibleModeAsync();
            return Results.Ok(new { isInvisible });
        }).RequireAuthorization();

        app.MapPost("/api-proxy/sync/invisible", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<bool>();
            var ok = await api.SetInvisibleModeAsync(req);
            return ok ? Results.Ok() : Results.StatusCode(502);
        }).RequireAuthorization();
    }
}
