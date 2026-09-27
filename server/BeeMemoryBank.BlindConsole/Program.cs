using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindConsole;

// The blind console (plan §9): a small local page bound to the host's loopback (the compose file
// publishes 127.0.0.1:5611), talking to the Api over the container network with the internal key.
// Everything the page shows comes from /api/blind/status; everything it does goes through the
// allowlisted proxy below, so the internal key and the Api's raw surface stay server-side.

var builder = WebApplication.CreateBuilder(args);

// Read AFTER CreateBuilder, through configuration (not the raw environment): the factory-based
// tests inject both values with UseSetting, which only exists once the builder exists.
var apiUrl = builder.Configuration["BMB_API_URL"] ?? "http://127.0.0.1:5300";
var internalKey = builder.Configuration["BMB_INTERNAL_KEY"]
    ?? throw new InvalidOperationException(
        "BMB_INTERNAL_KEY is not set — the console cannot talk to the Api without it. " +
        "In production docker-entrypoint exports it to both processes.");

// DNS rebinding: a web page on any site can point its own hostname at 127.0.0.1 and then talk to
// this port as "same origin". Host filtering refuses every Host header that is not a loopback
// name, so such a page gets a 400 instead of the login form. An operator reaching the console
// under another name (a reverse proxy, an ssh tunnel alias) sets AllowedHosts explicitly.
if (string.IsNullOrEmpty(builder.Configuration["AllowedHosts"]))
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";

builder.Services.AddSingleton<IApiProxy>(new ApiProxy(apiUrl, internalKey));
builder.Services.AddSingleton<ConsoleSessions>();
var app = builder.Build();

// Every state-changing request must carry this header. A cross-site form cannot set it, and a
// cross-origin fetch that sets it needs a CORS preflight this app never answers — so a page on
// another localhost port (same SITE for SameSite cookies, different origin) cannot drive a
// logged-in console. The page's own fetch() sets it.
const string CsrfHeader = "X-Bmb-Console";

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "frame-ancestors 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Cache-Control"] = "no-store";

    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && !ctx.Request.Headers.ContainsKey(CsrfHeader))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// The page is an embedded resource (see the csproj): one assembly, one page, nothing to lose
// to a wrong content root.
using (var pageStream = typeof(Program).Assembly
    .GetManifestResourceStream("BeeMemoryBank.BlindConsole.wwwroot.index.html")
    ?? throw new InvalidOperationException("embedded console page missing from the assembly"))
{
    using var reader = new StreamReader(pageStream);
    var pageHtml = reader.ReadToEnd();
    app.MapGet("/", () => Results.Content(pageHtml, "text/html", System.Text.Encoding.UTF8));
}

var sessions = app.Services.GetRequiredService<ConsoleSessions>();
var proxy = app.Services.GetRequiredService<IApiProxy>();

// Which Api routes a LOGGED-IN console session may reach through the proxy. This is the console's
// own attack surface, so it is an allowlist, not a blocklist: the blind role turns most of the Api
// off, but this list is what holds even if that wiring changes.
var allowed = new HashSet<(string Method, string Path)>
{
    ("GET", "api/blind/status"),
    ("GET", "api/blind/backup/settings"),
    ("GET", "api/blind/backup/list"),
    ("GET", "api/blind/console/logins"),
    ("GET", "api/blind/pair-code"),              // Opus-L
    ("GET", "api/blind/restore-codes"),          // Opus-R1 (the issue log, never the codes)
    ("POST", "api/blind/backup/now"),
    ("POST", "api/blind/backup/copy"),
    ("POST", "api/blind/backup/verify"),
    ("POST", "api/blind/backup/mode"),
    ("POST", "api/blind/console/password"),
    ("POST", "api/blind/pair-code/renew"),       // Opus-L
    ("POST", "api/blind/restore-code"),          // Opus-R1
    ("POST", "api/blind/wipe"),
    ("PUT", "api/blind/backup/settings"),
};

app.MapPost("/login", async (HttpContext ctx) =>
{
    var password = await ReadPasswordAsync(ctx);
    var (ok, locked) = await VerifyAsync(password, ctx);
    if (!ok)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, locked });
        return;
    }

    var token = sessions.Issue();
    ctx.Response.Cookies.Append(ApiProxy.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        // The console is served from the host's loopback; Secure would refuse to send the cookie
        // over the http that loopback publishing is.
        Secure = false,
        MaxAge = TimeSpan.FromHours(12),
        Path = "/",
    });
    await ctx.Response.WriteAsJsonAsync(new { ok = true });
});

app.MapPost("/logout", (HttpContext ctx) =>
{
    sessions.Drop(ctx.Request.Cookies[ApiProxy.CookieName]);
    ctx.Response.Cookies.Delete(ApiProxy.CookieName);
    return Results.Ok(new { ok = true });
});

// Authenticated passthrough. Method+path must be in the allowlist above; the session cookie is
// checked before anything is forwarded, so an unauthenticated browser cannot spend the internal
// key even on an allowed route.
app.MapMethods("/proxy/{*path}", new[] { "GET", "POST", "PUT" }, async (string path, HttpContext ctx) =>
{
    if (!sessions.IsValid(ctx.Request.Cookies[ApiProxy.CookieName]))
        return Results.Unauthorized();

    var method = ctx.Request.Method;
    if (!allowed.Contains((method, path)))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    string? body = null;
    if (path == "api/blind/restore-code")
    {
        // "Issue a recovery code" asks for the console password again (plan §9): a restore code hands the
        // whole vault's ciphertext to whoever holds it, so an unattended logged-in tab must not
        // be enough. Verified like a login (journal, attempt limit); the Api gets no password.
        var (ok, locked) = await VerifyAsync(await ReadPasswordAsync(ctx), ctx);
        if (!ok)
            return Results.Json(new { error = locked ? "locked" : "wrong console password" },
                statusCode: locked ? StatusCodes.Status423Locked : StatusCodes.Status401Unauthorized);
        body = "{}";
    }
    else if (method is "POST" or "PUT")
    {
        using var reader = new StreamReader(ctx.Request.Body);
        body = await reader.ReadToEndAsync(ctx.RequestAborted);
    }

    var (status, content, contentType) = await proxy.SendAsync(method, path, body, ctx.RequestAborted);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = contentType;
    await ctx.Response.WriteAsync(content, ctx.RequestAborted);
    return Results.Empty;
});

app.Run();

static async Task<string?> ReadPasswordAsync(HttpContext ctx)
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        return doc.RootElement.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }
    catch (JsonException)
    {
        // No password to check — the failed verification that follows is the honest answer.
        return null;
    }
}

// Verification (Argon2 + attempt limiting + the login journal) lives Api-side: the CLI shares
// it, and the journal must survive a console restart. The browser's address goes along because
// the Api only ever sees the console's own connection — a journal of "127.0.0.1" says nothing.
async Task<(bool Ok, bool Locked)> VerifyAsync(string? password, HttpContext ctx)
{
    var (status, body, _) = await proxy.SendAsync("POST", "api/blind/console/login",
        JsonSerializer.Serialize(new { password, remote = ctx.Connection.RemoteIpAddress?.ToString() }),
        ctx.RequestAborted);
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (status == 200 && root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
            root.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True);
    }
    catch (JsonException)
    {
        return (false, false);
    }
}

// Required for WebApplicationFactory in tests.
public partial class Program { }
