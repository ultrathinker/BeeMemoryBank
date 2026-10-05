using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;

namespace BeeMemoryBank.Api.Endpoints;

public static class AutoUnlockEndpoints
{
    public static void MapAutoUnlockEndpoints(this WebApplication app)
    {
        // Superadmin for the whole group, including the read: /status answers "does this node open
        // its own vault on boot without a password", which is reconnaissance for anyone deciding
        // whether getting onto the machine is worth the effort. Its enable/disable siblings were
        // already superadmin; the read was not, and the only consumer is the Admin page. RequireNonAgent
        // for the same reason /api/keys carries it — a leaked agent key must not be able to enumerate,
        // or change, what opens this vault.
        var group = app.MapGroup("/api/keys/auto-unlock")
            .WithTags("Keys")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        // GET /api/keys/auto-unlock/status  — returns whether the feature is enabled
        //
        // OsAutoUnlockService is only registered in DI where the OS has a secret store (Windows,
        // macOS; see ApiServices). Declaring it
        // as a raw nullable handler parameter is fragile: ASP.NET Core's minimal-API parameter
        // binding infers a complex-type parameter as [FromServices] only when
        // IServiceProviderIsService reports it as registered — on a platform where it ISN'T
        // registered (e.g. the Linux/Docker deployment, which this same Api.exe also serves),
        // the parameter would instead be inferred as [FromBody], breaking these endpoints in a
        // way that happens to look like it works today only by coincidence of nullable-body
        // handling. Resolve it explicitly from HttpContext.RequestServices instead, which has no
        // such ambiguity.
        group.MapGet("/status", async (HttpContext ctx) =>
        {
            var svc = ctx.RequestServices.GetService<OsAutoUnlockService>();
            if (svc == null || !svc.IsSupported)
                return Results.Ok(new AutoUnlockStatusResponse(false, false));

            // A secret store that cannot answer (a locked Keychain, a refused or damaged item) is not "auto-unlock is off": say that it
            // is unavailable, with a plain text that names no purpose and no secret.
            bool enabled;
            try
            {
                enabled = await svc.IsEnabledAsync();
            }
            catch (UserSecretStoreException)
            {
                return Results.Json(new ErrorResponse("The OS secret store is locked or unavailable."), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok(new AutoUnlockStatusResponse(enabled, svc.IsSupported));
        });

        // POST /api/keys/auto-unlock/enable  — creates the os_auto_unlock slot + the OS-protected secret (DPAPI / Keychain)
        group.MapPost("/enable", async (HttpContext ctx, SessionService session) =>
        {
            var svc = ctx.RequestServices.GetService<OsAutoUnlockService>();
            if (svc == null || !svc.IsSupported)
                return Results.Json(new ErrorResponse("OS auto-unlock is unavailable on this platform."), statusCode: 400);

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: 403);

            var alreadyEnabled = await svc.IsEnabledAsync();
            if (alreadyEnabled)
                return Results.Json(new ErrorResponse("OS auto-unlock is already enabled."), statusCode: 409);

            await svc.EnableAsync();
            return Results.Ok(new AutoUnlockStatusResponse(true, true));
        }).RequireSuperadmin().RequireNonAgent();

        // POST /api/keys/auto-unlock/disable  — removes the os_auto_unlock slot + the OS-protected secret
        group.MapPost("/disable", async (HttpContext ctx, SessionService session) =>
        {
            var svc = ctx.RequestServices.GetService<OsAutoUnlockService>();
            if (svc == null || !svc.IsSupported)
                return Results.Json(new ErrorResponse("OS auto-unlock is unavailable on this platform."), statusCode: 400);

            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: 403);

            await svc.DisableAsync();
            return Results.Ok(new AutoUnlockStatusResponse(false, true));
        }).RequireSuperadmin().RequireNonAgent();
    }
}
