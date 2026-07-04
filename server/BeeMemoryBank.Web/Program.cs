using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

// If a published bundle sits next to the binary (wwwroot present), anchor ContentRoot
// there so static files resolve regardless of the launcher's cwd. Without this,
// running `~/bmb/web/BeeMemoryBank.Web` from any other directory silently breaks
// UseStaticFiles and the UI renders unstyled. In dev (`dotnet run`) there is no
// wwwroot next to the binary in bin/<cfg>/<tfm>/ — the Web SDK uses a static-web-
// assets manifest instead, which only works with the default cwd-based ContentRoot.
var publishedWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = Directory.Exists(publishedWwwroot)
    ? WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        })
    : WebApplication.CreateBuilder(args);

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
// W3 (Option A): Web-side cache for security-stamp lookups so OnValidatePrincipal does not
// round-trip the API on every authenticated request. TTL 5 minutes bounds staleness.
builder.Services.AddMemoryCache();

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
        // W3 (Option B): short, NON-sliding cookie lifetime. A stolen/leaked cookie is
        // good for at most 8h and never refreshes, bounding the window for a stale
        // credential (deleted/demoted/password-reset user) to hours, not days. This is
        // the immediate ceiling on its own; Option A (security-stamp revalidation) adds
        // per-event revocation on top.
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;

        // W3 (Option A): revalidate the cookie's embedded security stamp against the API.
        // Runs on every AUTHENTICATED request (static files run before auth, so never on
        // CSS/JS). The stamp lookup is cached per-user for 5 minutes (IMemoryCache) so the
        // hot path normally hits memory. Behaviour rule (F2): the lookup distinguishes three
        // outcomes so an authoritative 404 is not conflated with a transport error:
        //   * absent stamp claim (cookie from before this feature) → REJECT → forced re-login;
        //   * stamp mismatch (password/role change, deletion) → REJECT;
        //   * HTTP 404 / user definitively gone → REJECT (authoritative answer, not fail-open);
        //   * API unreachable / 5xx / lookup throws → FAIL OPEN (an API hiccup must not log out
        //     the whole site; the 8h cookie ceiling already bounds exposure).
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async context =>
            {
                var principal = context.Principal;
                var stampClaim = principal?.FindFirst("SecurityStamp")?.Value;
                // Absent claim → old cookie from before this feature → reject (safe, one-time).
                if (string.IsNullOrEmpty(stampClaim))
                {
                    context.RejectPrincipal();
                    return;
                }

                var userId = principal?.FindFirst("UserId")?.Value;
                if (string.IsNullOrEmpty(userId) || !int.TryParse(userId, out var userIdInt))
                {
                    context.RejectPrincipal();
                    return;
                }

                var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
                var api = context.HttpContext.RequestServices.GetRequiredService<ApiClient>();
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();

                var cacheKey = $"security_stamp_{userId}";
                if (!cache.TryGetValue(cacheKey, out string? currentStamp) || currentStamp is null)
                {
                    SecurityStampLookup lookup;
                    try
                    {
                        lookup = await api.GetSecurityStampAsync(userIdInt);
                    }
                    catch (Exception ex)
                    {
                        // FAIL OPEN: do not reject on unexpected errors.
                        logger.LogWarning(ex,
                            "Security-stamp lookup failed for user {UserId}; allowing request (fail-open).", userId);
                        return;
                    }

                    // F2: HTTP 404 / definitive "user no longer exists" → REJECT. Unlike a transport
                    // error, a 404 is an authoritative answer from the API, so failing open would
                    // wrongly keep a deleted/demoted user's session alive. Not cached (next request
                    // would just reject again anyway).
                    if (lookup.Outcome == SecurityStampLookupOutcome.NotFound)
                    {
                        logger.LogWarning("Security-stamp lookup 404 for user {UserId}; rejecting principal.", userId);
                        context.RejectPrincipal();
                        return;
                    }

                    // Transport error / 5xx / unreachable / malformed body → FAIL OPEN. Do NOT log
                    // everyone out on an API hiccup; the 8h cookie ceiling already bounds exposure.
                    // The 5-min cache is NOT populated so the next request retries the lookup.
                    if (lookup.Outcome != SecurityStampLookupOutcome.Found || string.IsNullOrEmpty(lookup.Stamp))
                        return;

                    currentStamp = lookup.Stamp;
                    // Only cache successful (200) lookups for the TTL.
                    cache.Set(cacheKey, currentStamp,
                        new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
                }

                if (!string.Equals(stampClaim, currentStamp, StringComparison.Ordinal))
                    context.RejectPrincipal();
            }
        };
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
    var framable = ctx.Request.Path.StartsWithSegments("/Article/Preview");
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        // W5b: removed https://maxcdn.bootstrapcdn.com (EasyMDE's FontAwesome CDN). EasyMDE is
        // now pointed away from the CDN via autoDownloadFontAwesome:false (Article/Edit.cshtml).
        // The toolbar icon GLYPHS therefore do not render until a self-hosted FontAwesome subset
        // is vendored under wwwroot/lib/fontawesome — see the TODO in Article/Edit.cshtml.
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        // Shoelace icons are served as data: URIs and fetched (not <img>'d),
        // so they hit connect-src — must allow data: there.
        "connect-src 'self' data:; " +
        "frame-ancestors " + (framable ? "'self'" : "'none'") + "; " +
        "base-uri 'self'; " +
        "form-action 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = framable ? "SAMEORIGIN" : "DENY";
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

// Remote Accounts proxy ────────────────────────────────────────────────
app.MapGet("/api-proxy/remote-accounts", async (ApiClient api) =>
{
    var list = await api.ListRemoteAccountsAsync();
    return list != null ? Results.Ok(list) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapPost("/api-proxy/remote-accounts", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CreateRemoteAccountProxyRequest>();
    if (req == null || string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest(new { error = "displayName, baseUrl, username, password required" });
    var (ok, status, body, error) = await api.CreateRemoteAccountAsync(req.DisplayName, req.BaseUrl, req.Username, req.Password);
    if (ok) return Results.Ok(body);
    return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
}).RequireAuthorization();

app.MapDelete("/api-proxy/remote-accounts/{id:guid}", async (Guid id, ApiClient api) =>
{
    await api.DeleteRemoteAccountAsync(id);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api-proxy/remote-accounts/{id:guid}/accessible", async (Guid id, ApiClient api) =>
{
    var (ok, status, body, error) = await api.ListAccessibleRemoteFoldersAsync(id);
    if (ok) return Results.Ok(body);
    return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
}).RequireAuthorization();

app.MapGet("/api-proxy/remote-accounts/{id:guid}/subscriptions", async (Guid id, ApiClient api) =>
{
    var list = await api.ListRemoteSubscriptionsAsync(id);
    return list != null ? Results.Ok(list) : Results.StatusCode(502);
}).RequireAuthorization();

app.MapPost("/api-proxy/remote-accounts/subscriptions", async (HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<AddRemoteSubscriptionProxyRequest>();
    if (req == null || string.IsNullOrWhiteSpace(req.MountPath))
        return Results.BadRequest(new { error = "mountPath required" });
    var (ok, status, body, error) = await api.AddRemoteSubscriptionAsync(req.RemoteAccountId, req.RemoteFolderId, req.RemoteFolderPath, req.MountPath);
    if (ok) return Results.Ok(body);
    return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
}).RequireAuthorization();

app.MapDelete("/api-proxy/remote-accounts/subscriptions/{id:guid}", async (Guid id, ApiClient api) =>
{
    await api.DeleteRemoteSubscriptionAsync(id);
    return Results.NoContent();
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
    var result = await api.AddUserRestrictionAsync(userId, req.FolderId, req.Effect, req.IsReadOnly);
    return result != null ? Results.Ok(result) : Results.StatusCode(502);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

app.MapMethods("/api-proxy/restrictions/user/{userId:int}/{folderId:guid}", new[] { "PATCH" },
    async (int userId, Guid folderId, HttpContext ctx, ApiClient api) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<UpdateAclReadOnlyProxyRequest>();
    if (req == null) return Results.BadRequest();
    var ok = await api.SetUserRestrictionReadOnlyAsync(userId, folderId, req.IsReadOnly);
    return ok ? Results.NoContent() : Results.StatusCode(502);
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

// POST /api-proxy/init/reset — INTENTIONALLY ANONYMOUS (no RequireAuthorization).
// Purpose: lockout / forgotten-password recovery. When an admin is locked out of the
// vault (forgotten master password) they cannot authenticate, so this route must be
// reachable from the locked /Login screen to wipe-and-rejoin the node.
// The REAL security control is API-side: POST /api/init/reset requires the master
// password in the body (SessionService.UnlockAsync) and refuses if it is wrong. The
// Web layer adds nothing here — it only forwards the master password. Keeping this
// route anonymous is deliberate; do not add RequireAuthorization without breaking
// the only recovery path for a forgotten master password.
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

// ─── AI Chat proxy (Phase 1) ─────────────────────────────────────────────────
// Thin JSON passthroughs to the Api /api/chat/* endpoints. Identity headers
// (X-Internal-Key / X-User-Id / X-User-Role) are injected by InternalKeyHandler on ApiClient's
// HttpClient. These are EXPLICIT routes (registered before the W1 catch-all) — they must NOT be
// served by the catch-all forwarder (plan §2 Phase 1, §4). The catch-all is GET-pilot-only anyway.
// Phase 2 streaming will add a dedicated SSE route here (plan §2 Phase 2).

// Per-conversation model picker — open to any authenticated user (Api returns enabled models only).
app.MapGet("/api-proxy/chat/models", async (ApiClient api) =>
{
    var f = await api.ForwardGetAsync("chat/models");
    return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
}).RequireAuthorization();

// Admin catalogue (all models incl. disabled) — superadmin only.
app.MapGet("/api-proxy/chat/models/all", async (ApiClient api) =>
{
    var f = await api.ForwardGetAsync("chat/models/all");
    return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Add a model — superadmin only.
app.MapPost("/api-proxy/chat/models", async (HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync("chat/models", json);
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Toggle a model — superadmin only.
app.MapMethods("/api-proxy/chat/models/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync($"chat/models/{id}", json, method: "PATCH");
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Delete a model — superadmin only.
app.MapDelete("/api-proxy/chat/models/{id:guid}", async (Guid id, ApiClient api) =>
{
    var (ok, body, status) = await api.PostRawAsync($"chat/models/{id}", "", method: "DELETE");
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// List API keys — superadmin only.
app.MapGet("/api-proxy/chat/keys", async (ApiClient api) =>
{
    var f = await api.ForwardGetAsync("chat/keys");
    return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Add an API key — superadmin only.
app.MapPost("/api-proxy/chat/keys", async (HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync("chat/keys", json);
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Toggle an API key — superadmin only.
app.MapMethods("/api-proxy/chat/keys/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync($"chat/keys/{id}", json, method: "PATCH");
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// Delete an API key — superadmin only.
app.MapDelete("/api-proxy/chat/keys/{id:guid}", async (Guid id, ApiClient api) =>
{
    var (ok, body, status) = await api.PostRawAsync($"chat/keys/{id}", "", method: "DELETE");
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// ─── AI Chat proxy (Phase 2) — SSE streaming + conversation history ───────────
// See docs/ai-chat-implementation-plan.md §2 Phase 2 + §6 ("Streaming disambiguated").

// DEDICATED SSE passthrough — MUST NOT be served by the W1 catch-all (it buffers via
// ReadAsStringAsync, which would break streaming) and MUST NOT use Results.File (download/seek
// semantics). Instead: forward with HttpCompletionOption.ResponseHeadersRead (api.SendForwardAsync),
// set text/event-stream + X-Accel-Buffering:no on the Web response, and copy the upstream stream to
// ctx.Response.Body chunk-by-chunk with a flush after each write. ctx.RequestAborted is forwarded so
// a browser disconnect cancels the upstream Api call (which in turn cancels the OpenRouter stream).
// Identity headers (X-Internal-Key / X-User-Id) are injected by InternalKeyHandler as usual.
//
// Non-SSE upstream responses (the Api writes a normal JSON error BEFORE committing to the stream —
// e.g. 409 vault-locked, 400 bad request) are passed through as ordinary JSON with their status, so
// the UI can render the error instead of a dead event-stream.
app.MapPost("/api-proxy/chat/stream", async (HttpContext ctx, ApiClient api) =>
{
    var upstreamReq = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream");
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        upstreamReq.Content = new StreamContent(ctx.Request.Body);
        if (!string.IsNullOrEmpty(ctx.Request.ContentType))
            upstreamReq.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
    }

    HttpResponseMessage upstream;
    try
    {
        // Forward the client's abort token: closing the tab / navigating away cancels this send,
        // which cancels the Api-side OpenRouter stream (no wasted tokens/billing).
        upstream = await api.SendForwardAsync(upstreamReq, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { return; } // client gone
    catch { ctx.Response.StatusCode = 502; return; } // API unreachable

    using (upstream)
    {
        var upstreamMediaType = upstream.Content.Headers.ContentType?.MediaType ?? "";

        // The Api commits to text/event-stream only AFTER all pre-stream validation passes. A
        // different content-type means a normal (buffered) JSON error — pass it through verbatim
        // with its status so the UI shows the real reason.
        if (!string.Equals(upstreamMediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = string.IsNullOrEmpty(upstreamMediaType) ? "application/json" : upstreamMediaType;
            await upstream.Content.CopyToAsync(ctx.Response.Body);
            return;
        }

        // True streaming passthrough — copy the upstream body to the client as it arrives, flushing
        // per chunk so the browser receives SSE frames incrementally.
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
}).RequireAuthorization();

// ─── AI Chat proxy (Phase 3) — confirm-gate SSE passthrough ───────────────────
// Phase 3 human-in-the-loop: the /stream loop pauses on a write tool call (confirm_required) and
// the user picks Allow/Deny. This route forwards that decision to the Api confirm endpoint, which
// executes the write (or denial) and streams the CONTINUATION as a fresh SSE response. Same SSE
// passthrough contract as /stream above (ResponseHeadersRead + per-chunk flush + abort forwarding;
// non-SSE upstream = a JSON error passed through verbatim). See ChatEndpoints /confirm.

app.MapPost("/api-proxy/chat/{conversationId:guid}/confirm", async (Guid conversationId, HttpContext ctx, ApiClient api) =>
{
    var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"api/chat/stream/{conversationId}/confirm");
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        upstreamReq.Content = new StreamContent(ctx.Request.Body);
        if (!string.IsNullOrEmpty(ctx.Request.ContentType))
            upstreamReq.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
    }

    HttpResponseMessage upstream;
    try
    {
        upstream = await api.SendForwardAsync(upstreamReq, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { return; } // client gone
    catch { ctx.Response.StatusCode = 502; return; } // API unreachable

    using (upstream)
    {
        var upstreamMediaType = upstream.Content.Headers.ContentType?.MediaType ?? "";
        if (!string.Equals(upstreamMediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = string.IsNullOrEmpty(upstreamMediaType) ? "application/json" : upstreamMediaType;
            await upstream.Content.CopyToAsync(ctx.Response.Body);
            return;
        }

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
}).RequireAuthorization();

// Conversation history (thin JSON passthroughs — these do NOT need SSE handling). Identity headers
// (X-User-Id in particular) are injected by InternalKeyHandler, and the Api scopes every read/write
// to the caller's own user_id (plan §2 Phase 2: "a user must never see another user's conversations").

app.MapGet("/api-proxy/chat/conversations", async (ApiClient api) =>
{
    var f = await api.ForwardGetAsync("chat/conversations");
    return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
}).RequireAuthorization();

app.MapGet("/api-proxy/chat/conversations/{id:guid}/messages", async (Guid id, ApiClient api) =>
{
    var f = await api.ForwardGetAsync($"chat/conversations/{id}/messages");
    return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
}).RequireAuthorization();

app.MapMethods("/api-proxy/chat/conversations/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
{
    using var sr = new StreamReader(ctx.Request.Body);
    var json = await sr.ReadToEndAsync();
    var (ok, body, status) = await api.PostRawAsync($"chat/conversations/{id}", json, method: "PATCH");
    return Results.Content(body ?? "", "application/json", null, statusCode: status);
}).RequireAuthorization();

app.MapDelete("/api-proxy/chat/conversations/{id:guid}", async (Guid id, ApiClient api) =>
{
    var (ok, body, status) = await api.PostRawAsync($"chat/conversations/{id}", "", method: "DELETE");
    return Results.Content(body ?? "", "application/json", null, status);
}).RequireAuthorization();

// Phase 5: serve a chat attachment's bytes (vision uploads + generated images). Thin passthrough to
// the Api GET /api/chat/attachments/{id}; ownership is enforced API-side (chat_attachment → message
// → conversation(user_id)). Read to bytes (matches /api-proxy/media/{id}) so Results.File owns the
// payload cleanly. CSP img-src allows 'self' so this renders without any CSP change.
app.MapGet("/api-proxy/chat/attachments/{id:guid}", async (Guid id, ApiClient api) =>
{
    var upstreamReq = new HttpRequestMessage(HttpMethod.Get, $"api/chat/attachments/{id}");
    HttpResponseMessage upstream;
    try { upstream = await api.SendForwardAsync(upstreamReq); }
    catch { return Results.StatusCode(502); }

    using (upstream)
    {
        if (!upstream.IsSuccessStatusCode) return Results.StatusCode((int)upstream.StatusCode);
        var data = await upstream.Content.ReadAsByteArrayAsync();
        var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return Results.File(data, contentType);
    }
}).RequireAuthorization();

// Backfill Orphan Media Links proxy — disabled. Auto-link on save handles new uploads.
// Uncomment together with the UI in Admin.cshtml and the API endpoint if ever needed.
// app.MapPost("/api-proxy/admin/backfill-media-links", async (ApiClient api) =>
// {
//     var (ok, body, status) = await api.PostRawAsync("admin/backfill-media-links", "");
//     return Results.Content(body ?? "", "application/json", null, status);
// }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

// ─── W1 catch-all forwarder (registered LAST so explicit routes win) ──────────
// Routes /api-proxy/{**path} requests that are NOT matched by an explicit hand-written route
// through the deny-by-default ProxyRouteTable. Identity headers (X-Internal-Key / X-User-*) are
// injected automatically by InternalKeyHandler, so the forwarder only expresses ROLE gating and
// STREAMING. Pilot: only the concept-tags GET family is in the table. Unknown prefix → 404.
app.MapMethods("/api-proxy/{**path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" },
    async (string path, HttpContext ctx, ApiClient api) =>
{
    var entry = ProxyRouteTable.Match(path, out var matchedPrefix);
    if (entry is null)
        return Results.NotFound(); // deny-by-default: unknown prefix → 404, never a blind forward

    // Role gate — preserves the per-path RequireAuthorization("superadmin") semantics that a
    // single catch-all registration cannot otherwise attach per route.
    if (entry.RequiredRole == "superadmin" && !ctx.User.IsInRole("superadmin"))
        return Results.Json(new { error = "Forbidden — superadmin only" }, statusCode: 403);

    var upstreamPath = ProxyRouteTable.BuildUpstreamPath(path, matchedPrefix, entry)
                        + ctx.Request.QueryString.Value;
    var method = new HttpMethod(ctx.Request.Method);
    var upstreamReq = new HttpRequestMessage(method, upstreamPath);

    // Forward a body when present (POST/PUT/PATCH). The pilot is GET-only; the general path is
    // kept so the table can grow into mutation routes in a supervised follow-up.
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        upstreamReq.Content = new StreamContent(ctx.Request.Body);
        if (!string.IsNullOrEmpty(ctx.Request.ContentType))
            upstreamReq.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
    }

    HttpResponseMessage upstream;
    try
    {
        upstream = await api.SendForwardAsync(upstreamReq);
    }
    catch
    {
        return Results.StatusCode(502); // API unreachable
    }

    using (upstream)
    {
        // Status + content-type + body passthrough — this is the W2 fix, for free.
        var body = await upstream.Content.ReadAsStringAsync();
        var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "application/json";
        return Results.Content(body, contentType, Encoding.UTF8, (int)upstream.StatusCode);
    }
}).RequireAuthorization();

app.Run();

public partial class Program { }

internal record AddCommentProxyRequest(Guid ArticleId, string Text);
internal record CreateAgentProxyRequest(string Name, string? Description);

internal record UpdateArticleProxyRequest(
    string? Title,
    string? TreePath,
    string? Content,
    string? Passphrase = null);

internal record ProtectArticleProxyRequest(string Passphrase, string? Hint = null);
internal record UnlockArticleProxyRequest(string Passphrase);
internal record ChangePassphraseProxyRequest(string OldPassphrase, string NewPassphrase, string? Hint = null);

internal record CreateFolderProxyRequest(string Path);
internal record RenameFolderProxyRequest(string NewPath);
internal record MoveArticleProxyRequest(string NewPath);
internal record MoveFolderProxyRequest(string NewParentPath);
internal record CreateUserProxyRequest(string Username, string DisplayName, string Password, string Role);
internal record UpdateUserProxyRequest(string DisplayName, string? Role);
internal record ChangeUserPasswordProxyRequest(string NewPassword);
internal record ChangeOwnPasswordProxyRequest(string OldPassword, string NewPassword);
internal record AddAclEntryProxyRequest(Guid FolderId, string Effect, bool IsReadOnly = false);

internal record UpdateAclReadOnlyProxyRequest(bool IsReadOnly);

internal record CopyArticleProxyRequest(string TargetFolderPath);

internal record CopyFolderProxyRequest(string TargetParentPath);

internal record CreateRemoteAccountProxyRequest(string DisplayName, string BaseUrl, string Username, string Password);

internal record AddRemoteSubscriptionProxyRequest(Guid RemoteAccountId, Guid RemoteFolderId, string RemoteFolderPath, string MountPath);
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
