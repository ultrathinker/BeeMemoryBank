using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class ArticleProxyEndpoints
{
    public static void MapArticleProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api-proxy/tree/children", async (ApiClient api, string path = "/") =>
        {
            var result = await api.GetChildrenAsync(path);
            // Null means API returned non-success (typically 404 for ACL-denied folders or
            // paths that don't exist). Surface as 404 so the caller can show "not found"
            // rather than a 502 or 500.
            return result != null ? Results.Ok(result) : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet("/api-proxy/tree", async (ApiClient api) =>
        {
            var result = await api.GetFullTreeAsync();
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

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
            var (ok, status, content, error) = await api.UnlockArticleAsync(id, req.Passphrase);
            return ok ? Results.Ok(new { content }) : Results.Json(new { error = error ?? "Unlock failed" }, statusCode: status);
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

        app.MapDelete("/api-proxy/article/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, status, error) = await api.DeleteArticleAsync(id);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/search", async (ApiClient api, string? q = null, bool content = false) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest();
            var results = await api.SearchAsync(q, content);
            return results != null ? Results.Ok(results) : Results.StatusCode(502);
        }).RequireAuthorization();

        // Concept tag proxy routes
        // W1 PILOT: the concept-tags GET family (list, graph, graph/home, graph/search,
        // graph/neighbors, {name}/articles) is now served by the catch-all forwarder via the
        // ProxyRouteTable "concept-tags" entry — see the catch-all registered before app.Run().
        // The superadmin MUTATIONS below stay as explicit routes (they win over the catch-all and
        // keep their RequireAuthorization("superadmin") gate).

        app.MapPut("/api-proxy/concept-tags/{name}", async (string name, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<RenameTagDto>();
            if (req == null || string.IsNullOrWhiteSpace(req.NewName))
                return Results.BadRequest(new { error = "newName required" });
            var (ok, status, error) = await api.RenameConceptTagAsync(name, req.NewName);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapPost("/api-proxy/concept-tags/merge", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<MergeConceptTagDto>();
            if (req == null || string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Target))
                return Results.BadRequest(new { error = "source and target required" });
            var (ok, status, error) = await api.MergeConceptTagsAsync(req.Source, req.Target);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapDelete("/api-proxy/concept-tags/{name}", async (string name, ApiClient api) =>
        {
            var (ok, status, error) = await api.DeleteConceptTagAsync(name);
            return ok ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

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

        app.MapPost("/api-proxy/article/{id:guid}/move", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<MoveArticleProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.MoveArticleAsync(id, req.NewPath);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
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

        app.MapGet("/api-proxy/articles/{id:guid}/versions", async (Guid id, ApiClient api) =>
        {
            var versions = await api.GetArticleVersionsAsync(id);
            return versions != null ? Results.Ok(versions) : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet("/api-proxy/articles/{id:guid}/versions/{versionNumber:int}", async (Guid id, int versionNumber, ApiClient api) =>
        {
            var version = await api.GetArticleVersionContentAsync(id, versionNumber);
            return version != null ? Results.Ok(version) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/api-proxy/media/upload", async (HttpRequest req, ApiClient api) =>
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "No file provided" });
            var articleId = form["articleId"].FirstOrDefault();
            var result = await api.UploadMediaAsync(file, articleId);
            return result != null
                ? Results.Ok(new { id = result.Id, fileName = result.FileName, contentType = result.ContentType, fileSize = result.FileSize })
                : Results.StatusCode(502);
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/api-proxy/import/obsidian", async (HttpRequest req, ApiClient api) =>
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "No file provided" });
            try
            {
                var result = await api.ImportObsidianAsync(file);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization().DisableAntiforgery();

        app.MapGet("/api-proxy/media/{id:guid}", async (Guid id, ApiClient api, HttpContext ctx) =>
        {
            var result = await api.DownloadMediaAsync(id);
            if (result == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(result.Data, result.ContentType);
        }).RequireAuthorization();
    }
}
