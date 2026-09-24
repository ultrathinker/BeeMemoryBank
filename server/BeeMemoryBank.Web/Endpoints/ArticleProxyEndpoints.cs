using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// Hand-written article proxy routes. Everything here carries real Web-side logic the
/// catch-all forwarder cannot express:
/// <list type="bullet">
/// <item>GET /api-proxy/article/{id} composes two upstream calls (metadata + content) into one shape.</item>
/// <item>PUT /api-proxy/article/{id} branches on the passphrase (protected-article re-wrap).</item>
/// <item>The protected-article POSTs (and both copy routes) reshape the API's response
/// ({"protected": true} / {"relocked": true} / {"changed": true} / {"newArticleId": ...}) into the
/// {"ok": true} contract the browser JavaScript relies on.</item>
/// <item>GET /api-proxy/article/{id}/related does Web-side ordering + pagination.</item>
/// <item>GET/PUT /api-proxy/article/{id}/concept-tags reshape {"conceptTags":[...]} into a bare
/// array / 204, which the API does not do.</item>
/// </list>
/// The pure passthroughs (/api-proxy/tree, /search, /article (move), /articles (versions, media
/// list), /concept-tags, /media, /media/upload, /import/*) are <see cref="ProxyRouteTable"/> entries.
/// </summary>
public static class ArticleProxyEndpoints
{
    public static void MapArticleProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api-proxy/article/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var article = await api.GetArticleAsync(id);
            if (article == null) return Results.NotFound();
            var content = await api.GetArticleContentAsync(id);
            return Results.Ok(new { article, content = content?.Content });
        }).RequireAuthorization();

        app.MapPut("/api-proxy/article/{id:guid}", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UpdateArticleProxyRequest>();
            if (req == null) return Results.BadRequest();
            // Editing a protected article: forward the passphrase so the API re-wraps the new body.
            if (!string.IsNullOrEmpty(req.Passphrase) && req.Content != null)
            {
                var (art, status, error) = await api.UpdateProtectedArticleAsync(id, req.Title, req.TreePath, req.Content, req.Passphrase);
                return art != null ? Results.Ok(art) : Results.Json(new { error = error ?? "Update failed" }, statusCode: status);
            }
            var result = await api.UpdateArticleAsync(id, req.Title, req.TreePath, req.Content);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        // ─── Protected ("second-layer") articles ───────────────────────────────────
        app.MapPost("/api-proxy/article/{id:guid}/unlock", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UnlockArticleProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, content, expiresInSeconds, error) = await api.UnlockArticleAsync(id, req.Passphrase);
            return ok
                ? Results.Ok(new { content, unlockExpiresInSeconds = expiresInSeconds })
                : Results.Json(new { error = error ?? "Unlock failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/article/{id:guid}/relock", async (Guid id, ApiClient api) =>
        {
            var ok = await api.RelockArticleAsync(id);
            return ok ? Results.Ok(new { ok = true }) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/article/{id:guid}/protect", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<ProtectArticleProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.ProtectArticleAsync(id, req.Passphrase, req.Hint);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = error ?? "Protect failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/article/{id:guid}/unprotect", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UnlockArticleProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.UnprotectArticleAsync(id, req.Passphrase);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = error ?? "Unprotect failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/article/{id:guid}/change-passphrase", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<ChangePassphraseProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.ChangeArticlePassphraseAsync(id, req.OldPassphrase, req.NewPassphrase, req.Hint);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = error ?? "Change failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/article/{id:guid}/copy", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CopyArticleProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.TargetFolderPath))
                return Results.BadRequest(new { error = "targetFolderPath is required" });
            var (ok, status, error) = await api.CopyArticleAsync(id, req.TargetFolderPath);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = error ?? "Copy failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/folder/{id:guid}/copy", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CopyFolderProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.TargetParentPath))
                return Results.BadRequest(new { error = "targetParentPath is required" });
            var (ok, status, error) = await api.CopyFolderAsync(id, req.TargetParentPath);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = error ?? "Copy failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/article/{id:guid}/concept-tags", async (Guid id, ApiClient api) =>
        {
            var tags = await api.GetArticleConceptTagsAsync(id);
            return tags != null ? Results.Ok(tags) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPut("/api-proxy/article/{id:guid}/concept-tags", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<SetConceptTagsDto>();
            if (req == null) return Results.BadRequest(new { error = "body required" });
            var ok = await api.SetArticleConceptTagsAsync(id, req.ConceptTags ?? []);
            return ok ? Results.NoContent() : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/article/{id:guid}/related", async (Guid id, ApiClient api, int page = 1, int pageSize = 5) =>
        {
            var all = await api.GetRelatedArticlesAsync(id) ?? [];
            var ordered = all.OrderByDescending(r => r.Strength).ToList();
            var total = ordered.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize);
            return Results.Ok(new { items, total, page, pageSize, totalPages });
        }).RequireAuthorization();
    }
}
