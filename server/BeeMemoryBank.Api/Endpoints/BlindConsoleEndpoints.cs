using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.BlindConsole;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// The blind console's server side (plan §9). The console process itself serves the page; these
/// routes are what it calls with the internal key — console password management and login
/// verification (the Argon2 work and attempt limiting live Api-side so the CLI shares them), the
/// login journal, and "Disconnect and wipe" with its server-side re-verification of both
/// confirmations. Everything here requires the internal key: only the console and the CLI, both
/// local, may call any of it.
/// </summary>
public static class BlindConsoleEndpoints
{
    public static void MapBlindConsoleEndpoints(this WebApplication app)
    {
        // Same gate as /api/blind/backup: internal key + superadmin (the console process and the
        // CLI on the box, as the node's local administrator). /login is inside it too: the CONSOLE
        // process calls it on the browser's behalf before any console session exists.
        var group = app.MapGroup("/api/blind/console").RequireInternalKey().RequireSuperadmin().WithTags("Blind");

        group.MapPost("/password", (PasswordRequest req, BlindConsoleAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
                return Results.BadRequest(new ErrorResponse("console password must be at least 8 characters"));
            // Two different refusals, told apart by what the request carried — not by re-reading
// HasPassword() after the attempt, which cannot distinguish them: a node WITH a password answers
// "wrong password" for a missing current one too, and the CLI's "already set, keep it and move on"
// branch then never fires (review release-a2 agy#1, sec#8). The code is the contract; the message
// is for the operator.
            if (auth.HasPassword() && string.IsNullOrEmpty(req.CurrentPassword))
                return Results.BadRequest(new ErrorResponse(
                    "a console password is already set without a current password offered",
                    ErrorCodes.ConsolePasswordAlreadySet));
            if (!auth.TrySetPassword(req.CurrentPassword, req.NewPassword))
                return Results.BadRequest(new ErrorResponse("the current console password is wrong"));
            return Results.NoContent();
        });

        // 200 with ok=false rather than 401: a refused login is this endpoint's normal answer,
        // and the console needs the locked flag to show the lockout message instead. `remote` is
        // the browser's address as the console saw it — this route's own peer is always the
        // console process. Trusted as far as the internal key is: it only labels the journal.
        group.MapPost("/login", (LoginRequest req, BlindConsoleAuthService auth, HttpContext ctx) =>
        {
            var remote = req.Remote is { Length: > 0 and <= 64 } r ? r : ctx.Connection.RemoteIpAddress?.ToString();
            var (ok, locked) = auth.Verify(req.Password ?? "", remote);
            return Results.Ok(new { ok, locked });
        });

        group.MapGet("/logins", (BlindConsoleAuthService auth) =>
            Results.Ok(auth.RecentLogins()));

        // ── "Disconnect and wipe" ─────────────────────────────────────────────
        // Double confirmation is enforced UI-side AND here: the console password is re-verified
        // (recording the attempt like any login) and the node's own display name must be typed.
        // The wipe destroys only this node's data volume — never the restic repository.
        //
        // Two routes, one handler: the audit trail records WHICH local tool wiped the node, and
        // that must come from the server, not from a label in the body. The console's proxy
        // allowlists only /api/blind/wipe; the CLI calls /api/blind/wipe/cli, which the console
        // cannot reach.
        app.MapPost("/api/blind/wipe", (WipeRequest req, BlindConsoleAuthService auth, BlindWipeService wipe,
                INodeIdentityRepository nodeRepo, HttpContext ctx) => WipeAsync("console", req, auth, wipe, nodeRepo, ctx))
            .RequireInternalKey().RequireSuperadmin().WithTags("Blind");
        app.MapPost("/api/blind/wipe/cli", (WipeRequest req, BlindConsoleAuthService auth, BlindWipeService wipe,
                INodeIdentityRepository nodeRepo, HttpContext ctx) => WipeAsync("cli", req, auth, wipe, nodeRepo, ctx))
            .RequireInternalKey().RequireSuperadmin().WithTags("Blind");
    }

    private static async Task<IResult> WipeAsync(string source, WipeRequest req, BlindConsoleAuthService auth,
        BlindWipeService wipe, INodeIdentityRepository nodeRepo, HttpContext ctx)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity == null)
            return Results.BadRequest(new ErrorResponse("node is not initialized — nothing to wipe"));

        if (!string.Equals(req.ConfirmNodeName?.Trim(), identity.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse(
                $"type the node name ({identity.DisplayName}) to confirm the wipe"));

        var (ok, locked) = auth.Verify(req.ConsolePassword ?? "", ctx.Connection.RemoteIpAddress?.ToString());
        if (locked)
            return Results.StatusCode(StatusCodes.Status423Locked);
        if (!ok)
            return Results.Unauthorized();

        try
        {
            await wipe.WipeAsync(source, ctx.RequestAborted);
        }
        catch (BlindWipeRefusedException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
        catch (BlindWipeFailedException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status500InternalServerError);
        }
        return Results.Ok(new { wiped = true });
    }

    public sealed class PasswordRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
    }

    public sealed class LoginRequest
    {
        public string? Password { get; set; }
        public string? Remote { get; set; }
    }

    public sealed class WipeRequest
    {
        public string? ConsolePassword { get; set; }
        public string? ConfirmNodeName { get; set; }
    }
}
