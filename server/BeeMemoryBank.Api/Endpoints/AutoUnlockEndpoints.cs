using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.AutoUnlock;
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
            // On a server the OS secret store is the wrong place for this secret: "Start unlocked after a restart" is the server's checkbox
            // (docs/design/step-b-spec.md 11.2). A slot the owner created earlier keeps working; only turning it ON is refused.
            if (CurrentRole(ctx) == HostRole.Server)
                return Results.Json(new ErrorResponse("On a server use 'Start unlocked after a restart'.", "role_server"), statusCode: StatusCodes.Status409Conflict);

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

        MapServerAutoUnlock(app);
    }

    /// <summary>The role of this process: the registered one, else the environment's (anything but <c>desktop</c> is a server, spec 11.0).</summary>
    private static HostRole CurrentRole(HttpContext ctx) =>
        ctx.RequestServices.GetService<HostRole?>() ?? HostRoles.FromEnvironment();

    private sealed record ServerAutoUnlockEnableRequest(string? Password, string? Confirmation);

    private sealed record ServerAutoUnlockDisableRequest(string? Password);

    private sealed record ServerAutoUnlockStatusBody(string Role, bool Enabled, string State, bool KeyFileConfigured, string? Notice);

    private static ServerAutoUnlockStatusBody Body(ServerAutoUnlockStatus s) =>
        new(s.Role.Word(), s.Enabled, s.State.ToString().ToLowerInvariant(), s.KeyFileConfigured, s.Notice);

    /// <summary>
    /// "Start unlocked after a restart" (docs/design/step-b-spec.md 11.2): <c>status</c>, <c>enable</c> (two warnings, the password and the exact
    /// phrase) and <c>disable</c> (the password). Superadmin, internal key, never an agent or a remote token. The Desktop role answers 409
    /// <c>role_desktop</c> to all three: a desktop has the OS card.
    /// </summary>
    private static void MapServerAutoUnlock(WebApplication app)
    {
        var group = app.MapGroup("/api/keys/server-auto-unlock")
            .WithTags("Keys")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        static IResult? Refuse(HttpContext ctx, out ServerAutoUnlockService? svc)
        {
            svc = ctx.RequestServices.GetService<ServerAutoUnlockService>();
            if (CurrentRole(ctx) == HostRole.Desktop)
                return Results.Json(new ErrorResponse("On a desktop use the OS auto-unlock card.", "role_desktop"), statusCode: StatusCodes.Status409Conflict);
            if (svc == null)
                return Results.Json(new ErrorResponse("This node cannot start unlocked after a restart."), statusCode: StatusCodes.Status400BadRequest);
            return null;
        }

        group.MapGet("/status", async (HttpContext ctx) =>
        {
            if (Refuse(ctx, out var svc) is { } refusal) return refusal;
            return Results.Ok(Body(await svc!.GetStatusAsync(ctx.RequestAborted)));
        });

        group.MapPost("/enable", async (ServerAutoUnlockEnableRequest req, HttpContext ctx, SessionService session) =>
        {
            if (Refuse(ctx, out var svc) is { } refusal) return refusal;
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: StatusCodes.Status403Forbidden);

            // The second step of the two (spec 11.2): the phrase, exactly. Checked before the password so that a request without the phrase costs no Argon2.
            if (!string.Equals(req.Confirmation, ServerAutoUnlockService.PhraseRequired, StringComparison.Ordinal))
                return Results.Json(new ErrorResponse($"Type {ServerAutoUnlockService.PhraseRequired} to confirm.", "confirmation_required"), statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                await svc!.EnableAsync(ctx.RequestAborted);
            }
            catch (ServerAutoUnlockException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message, ex.Code), statusCode: ex.Code == "role_desktop" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            }
            return Results.Ok(Body(await svc.GetStatusAsync(ctx.RequestAborted)));
        });

        group.MapPost("/disable", async (ServerAutoUnlockDisableRequest req, HttpContext ctx, SessionService session) =>
        {
            if (Refuse(ctx, out var svc) is { } refusal) return refusal;
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked. Unlock first."), statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);

            await svc!.DisableAsync(ctx.RequestAborted);
            // An operator's key file is never removed by the node: say so.
            var status = Body(await svc.GetStatusAsync(ctx.RequestAborted));
            return Results.Ok(new { status.Role, status.Enabled, status.State, status.KeyFileConfigured, status.Notice,
                RemoveKeyFileYourself = svc.ConfiguredKeyFilePath is not null });
        });
    }
}
