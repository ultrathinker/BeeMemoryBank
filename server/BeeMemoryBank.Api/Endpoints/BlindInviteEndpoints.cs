using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Invitations to a blind copy (release 2.7), full node only - split out like <see cref="BlindNodeManagementEndpoints"/> so that a blind node does not
/// contain the service behind them. Two groups: the owner's management routes (internal key + superadmin, under <c>/api/blind-nodes/invites</c>) and the
/// two routes the invited device calls without any credential but the invitation itself (under <c>/api/sync/</c>, which every documented proxy recipe
/// already forwards, listed one by one in <c>PublicSurface</c>). The device's goodbye (<c>POST /api/sync/leave</c>) is here too: a full-node route, so
/// that the blind host, which links the sync endpoints, does not answer it.
/// </summary>
public static class BlindInviteEndpoints
{
    private static string Actor(HttpContext ctx) => ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";

    private static bool IsInviteId(string id) =>
        id.Length is >= 1 and <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The owner's side: make, list, confirm, refuse, withdraw.</summary>
    public static void MapBlindInviteManagementEndpoints(this WebApplication app)
    {
        // Not an agent, even one owned by a superadmin: the decision whom the network trusts is a person's, with the master password.
        var group = app.MapGroup("/api/blind-nodes/invites").WithTags("Blind").RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        group.MapPost("/", async (IssueBlindInviteRequest req, BlindInviteService invites, HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var issued = await invites.IssueAsync(req, Actor(ctx), ct);
                // The answer carries the bearer capability: nothing between here and the owner's screen may keep a copy of it.
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers.Pragma = "no-cache";
                return Results.Ok(issued);
            }
            catch (BlindInviteException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
            }
        });

        group.MapGet("/", async (BlindInviteService invites) => Results.Ok(await invites.ListAsync()));

        group.MapGet("/defaults", async (BlindInviteService invites) => Results.Ok(await invites.DefaultsAsync()));

        group.MapPost("/{inviteId}/confirm", async (string inviteId, ConfirmBlindInviteRequest req, BlindInviteService invites, HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsInviteId(inviteId)) return Results.NotFound();
            try
            {
                return Results.Ok(await invites.ConfirmAsync(inviteId, req.Password, Actor(ctx), ct));
            }
            catch (BlindInviteException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
            }
            catch (BlindPreflightFailedException ex)
            {
                return Results.Json(new { error = ex.Message, problems = ex.Problems, details = ex.Details }, statusCode: 409);
            }
        })
            .SingleFlight("blind-invite-confirm", "Confirming an invited blind copy");

        group.MapPost("/{inviteId}/refuse", async (string inviteId, BlindInviteService invites, HttpContext ctx) =>
        {
            if (!IsInviteId(inviteId)) return Results.NotFound();
            try
            {
                await invites.RefuseAsync(inviteId, Actor(ctx));
                return Results.Ok();
            }
            catch (BlindInviteException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
            }
        });

        group.MapDelete("/{inviteId}", async (string inviteId, BlindInviteService invites, HttpContext ctx) =>
        {
            if (!IsInviteId(inviteId)) return Results.NotFound();
            try
            {
                await invites.WithdrawAsync(inviteId, Actor(ctx));
                return Results.NoContent();
            }
            catch (BlindInviteException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
            }
        });
    }

    /// <summary>
    /// The device's side: <c>POST /api/sync/invite/redeem</c> and <c>POST /api/sync/invite/status</c>. No token, no key: the invitation's secret and the device's
    /// signature are the credentials. Small bodies, a budget per caller address, and one answer (410) for everything that is not a valid invitation.
    /// </summary>
    public static void MapBlindInvitePublicEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sync/invite/redeem", async (HttpContext ctx, BlindInviteService invites, BlindInviteRateLimits limits, CancellationToken ct) =>
        {
            // The first bytes of the body say which lane (a named open invitation: reserved; anything else: the caller's budget, then the shared one) - all of it before
            // the rest of the body is read, let alone parsed or verified.
            var prefix = await ReadPrefixAsync(ctx.Request, ct);
            using var admission = limits.Admit(BlindInviteRoute.Redeem, ctx.Connection.RemoteIpAddress, BlindInviteBodyPeek.InviteId(prefix));
            if (!admission.Allowed) return Refused(admission.Status, admission.RetryAfterSeconds, admission.Answer);
            if (await ReadBodyAsync(ctx.Request, BlindInviteRedeemRequest.MaxBodyBytes, prefix, ct) is not { } body) return Results.StatusCode(413);

            var reply = await invites.RedeemAsync(body, CallerText(ctx), ct);
            return reply.Outcome == BlindInviteOutcome.Ok
                ? Results.Text(System.Text.Encoding.UTF8.GetString(reply.Body!.ToJson()), "application/json", System.Text.Encoding.UTF8, 202)
                : Refusal(reply.Outcome);
        }).WithTags("Sync");

        app.MapPost("/api/sync/invite/status", async (HttpContext ctx, BlindInviteService invites, BlindInviteRateLimits limits, CancellationToken ct) =>
        {
            var prefix = await ReadPrefixAsync(ctx.Request, ct);
            using var admission = limits.Admit(BlindInviteRoute.Status, ctx.Connection.RemoteIpAddress, BlindInviteBodyPeek.InviteId(prefix));
            if (!admission.Allowed) return Refused(admission.Status, admission.RetryAfterSeconds, admission.Answer);
            if (await ReadBodyAsync(ctx.Request, BlindInviteStatusRequest.MaxBodyBytes, prefix, ct) is not { } body) return Results.StatusCode(413);

            var reply = await invites.StatusAsync(body, ct);
            return reply.Outcome == BlindInviteOutcome.Ok
                ? Results.Text(System.Text.Encoding.UTF8.GetString(reply.Body!.ToJson()), "application/json", System.Text.Encoding.UTF8, 200)
                : Refusal(reply.Outcome);
        }).WithTags("Sync");
    }

    /// <summary>
    /// <c>POST /api/sync/leave</c> (release 2.7), under the sync token of a blind copy: its owner removed it from the device. The hub notes it in memory, for the
    /// Blind nodes page and the network map ("removed by the device's owner"), and writes one audit line. It changes nothing else: only a superadmin's event
    /// can revoke a device, so the owner of the network does that, with a button. Best effort on the device's side; idempotent.
    /// </summary>
    public static void MapBlindLeaveEndpoint(this WebApplication app)
    {
        app.MapPost("/api/sync/leave", async (HttpContext ctx, SyncTokenStore store, BlindInviteService invites, BlindInviteRateLimits limits) =>
        {
            using var entry = limits.LeaveBudget.TryEnter();
            if (!entry.Allowed) return Refused(entry.Status, entry.RetryAfterSeconds, entry.Status == 503 ? BlindInviteRateLimits.BusyMessage : BlindInviteRateLimits.TooManyMessage);
            if (await SyncEndpoints.AuthenticatePeerAsync(ctx, store) is not { } nodeId) return Results.Unauthorized();
            // Only a blind copy leaves this way; another kind of peer is revoked by its owner on its own side.
            if (!BlindNodeId.IsBlind(nodeId)) return Results.NotFound();

            await invites.NoteLeftAsync(nodeId);
            return Results.NoContent();
        }).WithTags("Sync");
    }

    // The caller's key is BlindInviteCaller.KeyOf(connection address): behind a proxy that is the real client only where the deployment declared the proxy trusted
    // (BMB_TRUSTED_PROXIES), and the proxy's own address otherwise - one budget for everybody, which fails closed.

    private static string CallerText(HttpContext ctx) => BeeMemoryBank.Api.Services.NetworkMapBuilder.CallerAddress(ctx.Connection.RemoteIpAddress) ?? "unknown";

    // A budget said no: 429 when a rate is spent, 503 when too many requests are being served at once. Either way it says when to come back.
    private static IResult Refused(int status, int retryAfterSeconds, string message) => new RetryAfterResult(status, retryAfterSeconds, message);

    private sealed class RetryAfterResult(int status, int seconds, string message) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = message }, statusCode: status, contentType: "application/json").ExecuteAsync(httpContext);
        }
    }

    private static IResult Refusal(BlindInviteOutcome outcome) => outcome switch
    {
        BlindInviteOutcome.Locked => Results.Json(new { error = BlindInviteErrors.Locked }, statusCode: 503),
        BlindInviteOutcome.NotValid => Results.Json(new { error = BlindInviteErrors.NotValid }, statusCode: 410),
        BlindInviteOutcome.DeviceKnownUnderOtherKey => Results.Json(new { error = BlindInviteErrors.DeviceKnownUnderOtherKey }, statusCode: 409),
        _ => Results.Json(new { error = "bad_request" }, statusCode: 400),
    };

    /// <summary>At most the first <see cref="BlindInviteBodyPeek.PrefixBytes"/> bytes of the body (fewer when it is shorter): all that is read before a request is admitted.</summary>
    private static async Task<byte[]> ReadPrefixAsync(HttpRequest request, CancellationToken ct)
    {
        var buffer = new byte[BlindInviteBodyPeek.PrefixBytes];
        var filled = 0;
        int read;
        while (filled < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(filled), ct)) > 0) filled += read;
        return buffer[..filled];
    }

    /// <summary>The body (the <paramref name="prefix"/> already read, then the rest), or null when it is longer than <paramref name="max"/> bytes (read no further than one byte past it).</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int max, byte[] prefix, CancellationToken ct)
    {
        if (request.ContentLength is { } declared && declared > max) return null;
        if (prefix.Length > max) return null;
        using var stream = new MemoryStream();
        stream.Write(prefix);
        var buffer = new byte[2048];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (stream.Length + read > max) return null;
            stream.Write(buffer, 0, read);
        }
        return stream.ToArray();
    }
}

/// <summary>The body of the confirmation. The master password is in it, so it is left out of <c>ToString()</c> (a generated one prints every member).</summary>
public sealed record ConfirmBlindInviteRequest(string? Password)
{
    public override string ToString() => "ConfirmBlindInviteRequest { Password = *** }";
}
