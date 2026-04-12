using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Internal API address
var apiBaseUrl = builder.Configuration["BeeMemoryBank:ApiBaseUrl"]
    ?? Environment.GetEnvironmentVariable("BMB_API_URL")
    ?? "http://localhost:5300";

builder.Services.AddTransient<InternalKeyHandler>();
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
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
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ─── API-Proxy routes (for browser JavaScript) ───────────────────────────

app.MapGet("/api-proxy/tree/children", async (ApiClient api, string path = "/") =>
{
    var result = await api.GetChildrenAsync(path);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
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
    var result = await api.UpdateArticleAsync(id, req.Title, req.TreePath, req.Tags, req.Content);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapDelete("/api-proxy/article/{id:guid}", async (Guid id, ApiClient api) =>
{
    var ok = await api.DeleteArticleAsync(id);
    return ok ? Results.NoContent() : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/search", async (ApiClient api, string? q = null, bool content = false) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest();
    var results = await api.SearchAsync(q, content);
    return results != null ? Results.Ok(results) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapGet("/api-proxy/tags", async (ApiClient api) =>
{
    var tags = await api.GetTagsAsync();
    return tags != null ? Results.Ok(tags) : Results.StatusCode(502);
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
}).RequireAuthorization();

app.MapGet("/api-proxy/sync/delivery-status", async (ApiClient api) =>
{
    var result = await api.GetAsync("sync/delivery-status");
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapPost("/api-proxy/folders", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CreateFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.CreateFolderAsync(req.Path);
    return ok ? Results.Ok() : Results.StatusCode(502);
}).RequireAuthorization();

app.MapMethods("/api-proxy/folders", ["PATCH"], async (HttpContext ctx, ApiClient api, string path) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<RenameFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.RenameFolderAsync(path, req.NewPath);
    return ok ? Results.Ok() : Results.StatusCode(502);
}).RequireAuthorization();

app.MapDelete("/api-proxy/folders", async (ApiClient api, string path) =>
{
    var ok = await api.DeleteFolderAsync(path);
    return ok ? Results.Ok() : Results.StatusCode(502);
}).RequireAuthorization();

app.MapPost("/api-proxy/folders/move", async (HttpContext ctx, ApiClient api, string path) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<MoveFolderProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.MoveFolderAsync(path, req.NewParentPath);
    return ok ? Results.Ok() : Results.StatusCode(502);
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
    return Results.File(stream, "application/zip", folderName + ".zip");
}).RequireAuthorization();

app.MapPost("/api-proxy/article/{id:guid}/move", async (Guid id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<MoveArticleProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.MoveArticleAsync(id, req.NewPath);
    return ok ? Results.Ok() : Results.StatusCode(502);
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
    var user = await api.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role);
    return user != null ? Results.Ok(user) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPut("/api-proxy/users/{id:int}", async (int id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<UpdateUserProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.UpdateUserAsync(id, req.DisplayName, req.Role);
    return ok ? Results.Ok() : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapDelete("/api-proxy/users/{id:int}", async (int id, ApiClient api) =>
{
    var ok = await api.DeleteUserAsync(id);
    return ok ? Results.NoContent() : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPost("/api-proxy/users/{id:int}/change-password", async (int id, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<ChangeUserPasswordProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.ChangeUserPasswordAsync(id, req.NewPassword);
    return ok ? Results.Ok() : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// ─── Folder Restrictions ────────────────────────────────────────────────────

app.MapGet("/api-proxy/restrictions/user/{userId:int}", async (int userId, ApiClient api) =>
{
    var restrictions = await api.GetUserRestrictionsAsync(userId);
    return restrictions != null ? Results.Ok(restrictions) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPost("/api-proxy/restrictions/user/{userId:int}", async (int userId, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AddRestrictionProxyRequest>();
    if (req == null) return Results.BadRequest();
    var result = await api.AddUserRestrictionAsync(userId, req.FolderId);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapDelete("/api-proxy/restrictions/user/{userId:int}/{folderId:guid}", async (int userId, Guid folderId, ApiClient api) =>
{
    var ok = await api.RemoveUserRestrictionAsync(userId, folderId);
    return ok ? Results.NoContent() : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapGet("/api-proxy/restrictions/agent/{agentId:int}", async (int agentId, ApiClient api) =>
{
    var restrictions = await api.GetAgentRestrictionsAsync(agentId);
    return restrictions != null ? Results.Ok(restrictions) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapPost("/api-proxy/restrictions/agent/{agentId:int}", async (int agentId, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AddRestrictionProxyRequest>();
    if (req == null) return Results.BadRequest();
    var result = await api.AddAgentRestrictionAsync(agentId, req.FolderId);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapDelete("/api-proxy/restrictions/agent/{agentId:int}/{folderId:guid}", async (int agentId, Guid folderId, ApiClient api) =>
{
    var ok = await api.RemoveAgentRestrictionAsync(agentId, folderId);
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

app.MapGet("/api-proxy/media/{id:guid}", async (Guid id, ApiClient api, HttpContext ctx) =>
{
    var result = await api.DownloadMediaAsync(id);
    if (result == null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(result.Data, result.ContentType);
}).RequireAuthorization();

// ─── Razor Pages ──────────────────────────────────────────────────────────────

app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/Tree"));

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

app.MapGet("/api-proxy/maintenance", async (ApiClient api) =>
{
    var unlocked = await api.IsUnlockedAsync();
    return Results.Ok(new { isUnlocked = unlocked });
}).RequireAuthorization();

app.Run();

public partial class Program { }

internal record AddCommentProxyRequest(Guid ArticleId, string Text);

internal record UpdateArticleProxyRequest(
    string? Title,
    string? TreePath,
    List<string>? Tags,
    string? Content);

internal record CreateFolderProxyRequest(string Path);
internal record RenameFolderProxyRequest(string NewPath);
internal record MoveArticleProxyRequest(string NewPath);
internal record MoveFolderProxyRequest(string NewParentPath);
internal record CreateUserProxyRequest(string Username, string DisplayName, string Password, string Role);
internal record UpdateUserProxyRequest(string DisplayName, string? Role);
internal record ChangeUserPasswordProxyRequest(string NewPassword);
internal record ChangeOwnPasswordProxyRequest(string OldPassword, string NewPassword);
internal record AddRestrictionProxyRequest(Guid FolderId);
