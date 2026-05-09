using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Auto-resolve BMB_INTERNAL_KEY from shared key file if not set (non-Docker / local dev).
// API generates the file; Web reads it.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY")))
{
    var dataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
    var keyFile = Path.Combine(dataPath, ".internal-key");
    if (File.Exists(keyFile))
    {
        var key = File.ReadAllText(keyFile).Trim();
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", key);
    }
}

// Internal API address
var apiBaseUrl = builder.Configuration["BeeMemoryBank:ApiBaseUrl"]
    ?? Environment.GetEnvironmentVariable("BMB_API_URL")
    ?? "http://localhost:5300";

builder.Services.AddTransient<InternalKeyHandler>();
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(30);
}).AddHttpMessageHandler<InternalKeyHandler>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorPages();

builder.Services.AddAuthentication("BeeWebCookie")
    .AddCookie("BeeWebCookie", options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "bee_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // SameAsRequest would let the cookie travel over HTTP if a proxy ever
        // terminated TLS in front (passive sniffing). Always require Secure;
        // Development can still log in over http://localhost because Chrome
        // exempts localhost from the Secure cookie restriction.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 500L * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    o.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
});

var app = builder.Build();

// ─── Init-status redirect (cache forever once initialized) ────────────────
// Only redirect to /Setup when the API explicitly confirms the node is NOT initialized.
// If the API is unreachable (null), let the request through — don't block existing nodes.
var initCheckedFlag = 0;
app.Use(async (context, next) =>
{
    if (Volatile.Read(ref initCheckedFlag) == 0)
    {
        var api = context.RequestServices.GetRequiredService<ApiClient>();
        var initialized = await api.GetInitStatusAsync(); // true, false, or null (API unreachable)

        if (initialized == true)
        {
            Volatile.Write(ref initCheckedFlag, 1);
        }
        else if (initialized == false)
        {
            var path = context.Request.Path.Value ?? "";
            if (!string.Equals(path, "/Setup", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api-proxy", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/Setup");
                return;
            }
        }
        // initialized == null → API unreachable, let the request through
    }
    await next();
});

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Security response headers — defense-in-depth for XSS / clickjacking / MIME sniffing.
// script-src 'unsafe-inline' is currently required because Razor pages embed JS in
// inline <script> blocks (Article/View, Edit, Folder, Layout, …). Migrate to
// nonce-based CSP later for full defense-in-depth. style-src 'unsafe-inline' is
// required by Shoelace components. data:/blob: support encrypted media rendering.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        // maxcdn.bootstrapcdn.com — EasyMDE injects a <link> for FontAwesome
        // from this CDN when its toolbar renders. Until we self-host, allow it.
        "style-src 'self' 'unsafe-inline' https://maxcdn.bootstrapcdn.com; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data: https://maxcdn.bootstrapcdn.com; " +
        // Shoelace icons are served as data: URIs and fetched (not <img>'d),
        // so they hit connect-src — must allow data: there.
        "connect-src 'self' data:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ─── API-Proxy routes (for browser JavaScript) ───────────────────────────

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
    var result = await api.UpdateArticleAsync(id, req.Title, req.TreePath, req.Content);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
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
app.MapGet("/api-proxy/concept-tags", async (ApiClient api, string? q = null, int limit = 500) =>
{
    var tags = await api.GetAllConceptTagsAsync(q, limit);
    return tags != null ? Results.Ok(tags) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/concept-tags/graph", async (ApiClient api) =>
{
    var edges = await api.GetConceptGraphAsync();
    return edges != null ? Results.Ok(edges) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/concept-tags/graph/home", async (ApiClient api) =>
{
    var data = await api.GetConceptGraphHomeAsync();
    return data.HasValue ? Results.Ok(data.Value) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/concept-tags/graph/search", async (ApiClient api, string? q, int depth = 2, int maxNodes = 200) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q required" });
    var data = await api.GetConceptGraphSearchAsync(q, depth, maxNodes);
    return data.HasValue ? Results.Ok(data.Value) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/concept-tags/graph/neighbors", async (ApiClient api, string tag) =>
{
    var result = await api.GetConceptGraphNeighborsAsync(tag);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization();

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

app.MapGet("/api-proxy/concept-tags/{name}/articles", async (string name, ApiClient api) =>
{
    var articles = await api.GetArticlesByConceptTagAsync(name);
    return articles != null ? Results.Ok(articles) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapPut("/api-proxy/article/{id:guid}/concept-tags", async (Guid id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<SetConceptTagsDto>();
    if (req == null) return Results.BadRequest(new { error = "body required" });
    var ok = await api.SetArticleConceptTagsAsync(id, req.ConceptTags ?? []);
    return ok ? Results.NoContent() : Results.StatusCode(502);
}).RequireAuthorization();

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
    var result = await api.GetSyncStatusAsync();
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapGet("/api-proxy/sync/delivery-status", async (ApiClient api) =>
{
    var result = await api.GetAsync("sync/delivery-status");
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPost("/api-proxy/folders", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CreateFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (ok, status, error) = await api.CreateFolderAsync(req.Path);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
}).RequireAuthorization();

app.MapMethods("/api-proxy/folders", ["PATCH"], async (HttpContext ctx, ApiClient api, string path) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<RenameFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (ok, status, error) = await api.RenameFolderAsync(path, req.NewPath);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
}).RequireAuthorization();

app.MapDelete("/api-proxy/folders", async (ApiClient api, string path) =>
{
    var (ok, status, error) = await api.DeleteFolderAsync(path);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
}).RequireAuthorization();

app.MapPost("/api-proxy/folders/move", async (HttpContext ctx, ApiClient api, string path) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<MoveFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (ok, status, error) = await api.MoveFolderAsync(path, req.NewParentPath);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
}).RequireAuthorization();

app.MapPost("/api-proxy/users/me/change-password", async (HttpContext ctx, ApiClient api) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ChangeOwnPasswordProxyRequest>();
    if (body == null) return Results.BadRequest();
    var (ok, error) = await api.ChangeOwnPasswordAsync(body.OldPassword, body.NewPassword);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: 400);
}).RequireAuthorization();

app.MapGet("/api-proxy/folders/search", async (ApiClient api, string? q, int limit = 12) =>
{
    var result = await api.SearchFoldersAsync(q ?? "", limit);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/folders/download", async (ApiClient api, string path) =>
{
    var result = await api.DownloadFolderZipAsync(path);
    if (result == null) return Results.StatusCode(502);
    if (!result.IsSuccessStatusCode)
        return Results.StatusCode((int)result.StatusCode);

    var folderName = path.TrimEnd('/').Split('/').LastOrDefault("folder");
    var stream = await result.Content.ReadAsStreamAsync();
    return Results.File(new DisposingStreamWrapper(stream, result), "application/zip", folderName + ".zip");
}).RequireAuthorization();

app.MapPost("/api-proxy/downloads/prepare", async (HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync("downloads/prepare", json);
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization();

app.MapGet("/api-proxy/downloads/{token}", async (string token, ApiClient api) =>
{
    var result = await api.DownloadByTokenAsync(token);
    if (result == null) return Results.StatusCode(502);
    if (!result.IsSuccessStatusCode) return Results.StatusCode((int)result.StatusCode);
    var stream = await result.Content.ReadAsStreamAsync();
    var contentType = result.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
    var fileName = result.Content.Headers.ContentDisposition?.FileNameStar
        ?? result.Content.Headers.ContentDisposition?.FileName?.Trim('"')
        ?? "download";
    return Results.File(new DisposingStreamWrapper(stream, result), contentType, fileName);
}).RequireAuthorization();

app.MapPost("/api-proxy/article/{id:guid}/move", async (Guid id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<MoveArticleProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (ok, status, error) = await api.MoveArticleAsync(id, req.NewPath);
    return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
}).RequireAuthorization();

app.MapGet("/api-proxy/article/{id:guid}/related", async (Guid id, ApiClient api, int page = 1, int pageSize = 10) =>
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

app.MapGet("/api-proxy/users", async (ApiClient api) =>
{
    var users = await api.GetUsersAsync();
    return users != null ? Results.Ok(users) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPost("/api-proxy/users", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CreateUserProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (user, error, status) = await api.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role);
    if (user != null) return Results.Ok(user);
    return Results.Json(new { error = error ?? "Failed to create user" }, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPut("/api-proxy/users/{id:int}", async (int id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<UpdateUserProxyRequest>();
    if (req == null) return Results.BadRequest();
    var (ok, error, status) = await api.UpdateUserAsync(id, req.DisplayName, req.Role);
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
    var result = await api.AddUserRestrictionAsync(userId, req.FolderId, req.Effect);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapDelete("/api-proxy/restrictions/user/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, ApiClient api) =>
{
    var ok = await api.RemoveUserRestrictionAsync(userId, folderId);
    return ok ? Results.NoContent() : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

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

// ─── Razor Pages ──────────────────────────────────────────────────────────────

app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/Tree"));

app.MapPost("/api-proxy/init/reset", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<ResetProxyRequest>();
    if (req == null || string.IsNullOrWhiteSpace(req.MasterPassword))
        return Results.BadRequest(new { error = "masterPassword required" });
    var (ok, err) = await api.ResetNodeAsync(req.MasterPassword);
    return ok ? Results.Ok() : Results.BadRequest(err);
});

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

app.MapGet("/api-proxy/maintenance", async (ApiClient api) =>
{
    var unlocked = await api.IsUnlockedAsync();
    return Results.Ok(new { isUnlocked = unlocked });
}).RequireAuthorization();

// Backfill Orphan Media Links proxy — disabled. Auto-link on save handles new uploads.
// Uncomment together with the UI in Admin.cshtml and the API endpoint if ever needed.
// app.MapPost("/api-proxy/admin/backfill-media-links", async (ApiClient api) =>
// {
//     var (ok, body, status) = await api.PostRawAsync("admin/backfill-media-links", "");
//     return Results.Content(body ?? "", "application/json", null, status);
// }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.Run();

public partial class Program { }

internal record AddCommentProxyRequest(Guid ArticleId, string Text);
internal record CreateAgentProxyRequest(string Name, string? Description);

internal record UpdateArticleProxyRequest(
    string? Title,
    string? TreePath,
    string? Content);

internal record CreateFolderProxyRequest(string Path);
internal record RenameFolderProxyRequest(string NewPath);
internal record MoveArticleProxyRequest(string NewPath);
internal record MoveFolderProxyRequest(string NewParentPath);
internal record CreateUserProxyRequest(string Username, string DisplayName, string Password, string Role);
internal record UpdateUserProxyRequest(string DisplayName, string? Role);
internal record ChangeUserPasswordProxyRequest(string NewPassword);
internal record ChangeOwnPasswordProxyRequest(string OldPassword, string NewPassword);
internal record AddAclEntryProxyRequest(Guid FolderId, string Effect);
internal record RenameTagDto(string NewName);
internal record MergeConceptTagDto(string Source, string Target);
internal record SetConceptTagsDto(List<string>? ConceptTags);
internal record PreviewFolderRequest(string Path);
internal record HardDeleteFolderRequest(string Path);
internal record ResetProxyRequest(string MasterPassword);

internal sealed class DisposingStreamWrapper(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void CopyTo(Stream destination, int bufferSize) => inner.CopyTo(destination, bufferSize);
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) owner.Dispose();
    }
}
