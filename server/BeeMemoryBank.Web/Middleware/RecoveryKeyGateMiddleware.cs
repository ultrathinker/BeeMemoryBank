using System.Security.Claims;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Middleware;

/// <summary>
/// The server-side half of the blocking recovery-key step a NEW node shows once its first administrator exists
/// (release 2.7.5): while the node says the step is required and nobody has confirmed it, every page of a
/// superadmin's Web session goes to <c>/RecoveryKeyGate</c> - typing another address does not skip it.
///
/// <para>Deliberately Web-only. The API, MCP and agents keep working untouched, so an unattended node whose owner
/// has not clicked through the step is never stuck; and a node that existed before this release answers "not
/// required", so an upgrade never gates anybody.</para>
///
/// <para>Only full-page GETs are checked. Scripts and styles, /api-proxy calls, logout and the gate page itself pass
/// through; an API that cannot be reached fails open, like the other API-dependent checks of this pipeline.</para>
/// </summary>
public sealed class RecoveryKeyGateMiddleware(RequestDelegate next, ILogger<RecoveryKeyGateMiddleware> logger)
{
    internal const string GatePath = "/RecoveryKeyGate";

    private static readonly string[] PassThroughPrefixes =
    [
        GatePath, "/Logout", "/Login", "/Error", "/api-proxy", "/node",
        "/lib", "/css", "/js", "/images", "/favicon",
    ];

    public async Task InvokeAsync(HttpContext context, ApiClient api)
    {
        if (!ShouldCheck(context))
        {
            await next(context);
            return;
        }

        bool pending;
        try
        {
            pending = await api.GetRecoveryKeyStatusAsync() is { Required: true, Acknowledged: false };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Recovery-key gate check failed; letting the request through.");
            await next(context);
            return;
        }

        if (!pending)
        {
            await next(context);
            return;
        }

        var returnUrl = context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(GatePath + "?returnUrl=" + Uri.EscapeDataString(returnUrl));
    }

    internal static bool ShouldCheck(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        if (context.User.Identity?.IsAuthenticated != true) return false;
        if (!context.User.IsInRole(UserRoles.Superadmin)) return false;

        var path = context.Request.Path;
        foreach (var prefix in PassThroughPrefixes)
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        // Anything with an extension is a file, not a page.
        return !Path.HasExtension(path.Value);
    }
}