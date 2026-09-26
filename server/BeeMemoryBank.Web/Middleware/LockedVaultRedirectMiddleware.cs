using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;

namespace BeeMemoryBank.Web.Middleware;

/// <summary>
/// Sends a signed-in user to /Login when the node's vault is locked, before any page renders.
///
/// <para>The web cookie outlives the API process: after a restart (an app update, a reboot) the
/// cookie is still valid but the vault is locked again. Without this check the Tree page rendered
/// normally, its folder list even loaded, and the first click on an article bounced the user to
/// /Login — a half-open state that looked like a random logout. Checking once per page navigation
/// makes it one consistent answer: locked means the login page, straight away.</para>
///
/// <para>Only full-page GETs are checked: /api-proxy calls, static files and the anonymous pages
/// (/Login, /Logout, /Setup, /Error) pass through untouched. An API that cannot be reached fails
/// open, like the other API-dependent checks in this pipeline.</para>
/// </summary>
public sealed class LockedVaultRedirectMiddleware(RequestDelegate next, ILogger<LockedVaultRedirectMiddleware> logger)
{
    private static readonly string[] PassThroughPrefixes =
    [
        "/Login", "/Logout", "/Setup", "/Error", "/api-proxy", "/node",
        "/lib", "/css", "/js", "/images", "/favicon",
    ];

    public async Task InvokeAsync(HttpContext context, ApiClient api)
    {
        if (!ShouldCheck(context))
        {
            await next(context);
            return;
        }

        bool unlocked;
        try
        {
            unlocked = await api.IsUnlockedAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Vault lock check failed; letting the request through.");
            await next(context);
            return;
        }

        if (unlocked)
        {
            await next(context);
            return;
        }

        await context.SignOutAsync("BeeWebCookie");
        var returnUrl = context.Request.Path + context.Request.QueryString;
        context.Response.Redirect("/Login?returnUrl=" + Uri.EscapeDataString(returnUrl));
    }

    internal static bool ShouldCheck(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        if (context.User.Identity?.IsAuthenticated != true) return false;

        var path = context.Request.Path;
        foreach (var prefix in PassThroughPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // Anything with an extension is a file, not a page.
        return !Path.HasExtension(path.Value);
    }
}
