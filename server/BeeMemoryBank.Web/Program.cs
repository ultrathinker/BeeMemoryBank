using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core; // AddMdnsBrowser (join-wizard LAN node discovery)
using BeeMemoryBank.Web.Endpoints;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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

builder.Services.AddLoopbackForwardedHeaders(builder.Configuration);

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
// mDNS browser: powers the "Found nodes on your network" list in the Setup join wizard.
// Web only browses (the API does the announcing); the manual-URL-entry path stays fully functional.
builder.Services.AddMdnsBrowser();
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
        // W3 (Option B): the actual ExpireTimeSpan/SlidingExpiration values are admin-
        // configurable (default 48h, sliding ON) — see the AddOptions<CookieAuthenticationOptions>
        // .Configure<WebSessionSettingsService> registration below, which runs AFTER this
        // delegate and overrides these two properties with the current DB-backed values.
        // Option A (security-stamp revalidation) adds independent per-event revocation on top,
        // regardless of the configured lifetime.

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

// Admin-configurable web login cookie lifetime (default 48h, sliding ON — see
// WebSessionSettingsService). Registered as a separate named-options Configure so it
// composes with (and runs after, overriding) the base AddCookie(...) setup above.
// Runtime changes take effect immediately via IOptionsMonitorCache<CookieAuthenticationOptions>
// invalidation — see the session-settings lazy-load middleware and the
// /api-proxy/session/settings PUT handler.
builder.Services.AddSingleton<WebSessionSettingsService>();
builder.Services.AddSingleton<BrandingService>();
builder.Services.AddOptions<CookieAuthenticationOptions>("BeeWebCookie")
    .Configure<WebSessionSettingsService>((opts, settings) =>
    {
        opts.ExpireTimeSpan = TimeSpan.FromHours(settings.ExpireHours);
        opts.SlidingExpiration = settings.SlidingExpiration;
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

app.UseLoopbackForwardedHeaders();

// BMB_READY_FILE: signals startup completion to a parent orchestrator (bmbd) by writing
// {pid, urls, applicationName, version, startupTimeUtc} once Kestrel has bound its actual
// port — ApplicationStarted fires after that. Off by default: standalone/Docker/tests don't
// set this env var and see no behavior change.
var readyFilePath = Environment.GetEnvironmentVariable("BMB_READY_FILE");
if (!string.IsNullOrEmpty(readyFilePath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var readyInfo = new BeeMemoryBank.Hosting.ReadyFileInfo(
            Pid: Environment.ProcessId,
            Urls: app.Urls.ToList(),
            ApplicationName: "BeeMemoryBank.Web",
            Version: "1.0.1",
            StartupTimeUtc: DateTime.UtcNow
        );
        BeeMemoryBank.Hosting.ReadyFileManager.Write(readyFilePath, readyInfo);
    });
}

// BMB_STDIN_LIFELINE: when the parent orchestrator closes this process's stdin (graceful
// stop signal) or dies without closing it (still an EOF from this end), trigger a normal
// graceful shutdown via StopApplication() instead of relying solely on a hard kill.
if (Environment.GetEnvironmentVariable("BMB_STDIN_LIFELINE") == "1")
{
    BeeMemoryBank.Hosting.StdinLifeline.Start(() => app.Lifetime.StopApplication());
}

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

// ─── Session (cookie) settings — lazy-fetch once, refresh live on save ────────────
// Fetches the admin-configured cookie lifetime/sliding-expiration from the API on the
// first request (same "try once, stick once confirmed" idiom as the init-status check
// above), then invalidates the named CookieAuthenticationOptions cache so the framework
// picks up the DB-backed values instead of WebSessionSettingsService's hardcoded
// defaults. If the API is unreachable, defaults are used and the next request retries.
var sessionSettingsLoaded = 0;
app.Use(async (context, next) =>
{
    if (Volatile.Read(ref sessionSettingsLoaded) == 0)
    {
        var settings = context.RequestServices.GetRequiredService<WebSessionSettingsService>();
        if (!settings.Loaded)
        {
            var api = context.RequestServices.GetRequiredService<ApiClient>();
            var fetched = await api.GetSessionSettingsAsync();
            if (fetched != null && !settings.Loaded)
            {
                settings.ExpireHours = fetched.ExpireHours;
                settings.SlidingExpiration = fetched.SlidingExpiration;
                settings.Loaded = true;
                context.RequestServices.GetRequiredService<IOptionsMonitorCache<CookieAuthenticationOptions>>()
                    .TryRemove("BeeWebCookie");
            }
        }
        // Stick only on a definitive answer — same idiom as the init-status middleware
        // above: stop retrying once real values exist (a successful fetch OR an admin save
        // that set Loaded=true underneath us). If the API was unreachable and no save has
        // happened, leave the flag at 0 so the next request retries.
        if (settings.Loaded)
            Volatile.Write(ref sessionSettingsLoaded, 1);
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
        // pointed away from the CDN via autoDownloadFontAwesome:false, and its toolbar icons are
        // swapped for the app's own vendored Shoelace <sl-icon> set immediately after construction
        // (see replaceToolbarIcons in Article/Edit.cshtml) — no FontAwesome vendoring needed.
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
// Reverse-proxy route handlers live in server/BeeMemoryBank.Web/Endpoints/*.cs and are wired up
// here in registration order. MapMiscProxyEndpoints() MUST stay last — it owns the catch-all
// forwarder /api-proxy/{**path} which must be registered after every explicit route so the
// explicit routes win.
app.MapArticleProxyEndpoints();
app.MapFolderProxyEndpoints();
app.MapSnapshotProxyEndpoints();
app.MapUserProxyEndpoints();
app.MapRoleProxyEndpoints();
app.MapFavoriteProxyEndpoints();
app.MapChatProxyEndpoints();
// ca.crt download for the "Connect a device" page — anonymous, no proxy/auth.
app.MapConnectEndpoints();
app.MapMiscProxyEndpoints();

// ─── Razor Pages ──────────────────────────────────────────────────────────────

app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/Tree"));

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
internal record CreateUserProxyRequest(string Username, string DisplayName, string Password, string Role, bool ChatAccess = true);
internal record UpdateUserProxyRequest(string DisplayName, string? Role, bool? ChatAccess = null);
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
