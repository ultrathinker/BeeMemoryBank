using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindStatus;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// <c>GET /api/blind/status</c> — one JSON object describing the whole blind node for the local
/// console page and for the Windows app (plan §9, CONTRACTS §5). The handler owns nothing but
/// authorization and assembly; every section comes from an <see cref="IBlindStatusContributor"/>.
/// </summary>
public static class BlindStatusEndpoints
{
    public static void MapBlindStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/blind/status", async (
            HttpContext ctx,
            IEnumerable<IBlindStatusContributor> contributors,
            SyncTokenStore tokenStore,
            IWhitelistRepository whitelistRepo,
            CancellationToken ct) =>
        {
            if (await AuthorizeAsync(ctx, tokenStore, whitelistRepo) is { } refused)
                return refused;

            var b = new BlindStatusBuilder();
            foreach (var contributor in contributors)
                await contributor.ContributeAsync(b, ct);

            return Results.Ok(b.Build());
        }).WithTags("Blind");
    }

    /// <summary>
    /// "Local admin or superadmin sync token" (CONTRACTS §2). The console reaches this with the
    /// node's internal key; a Windows peer — which by definition does not hold that key — reaches
    /// it with its sync bearer token, but only while its own whitelist row says superadmin, the
    /// same standing superadmin sync events are checked against. A plain peer token is refused:
    /// the status names every node in the mesh and its storage layout.
    ///
    /// <para>Not <c>.RequireSuperadmin()</c>: that filter only knows local callers, and the peer path
    /// has no internal key by definition. So the role is checked here, for both paths: behind the
    /// internal key the caller must be a superadmin too (on a full node the Web layer forwards a
    /// regular user's role), 403 otherwise. Listed in EndpointAuthGuardrailTests.NonRoleGatedReads
    /// for that reason.</para>
    /// </summary>
    /// <returns>Null when authorized, else the refusal.</returns>
    private static async Task<IResult?> AuthorizeAsync(
        HttpContext ctx, SyncTokenStore tokenStore, IWhitelistRepository whitelistRepo)
    {
        if (Middleware.InternalKeyValidator.Validate(ctx))
            return CallerIdentity.Extract(ctx).IsSuperadmin
                ? null
                : Results.Json(new Models.ErrorResponse("Superadmin required"), statusCode: StatusCodes.Status403Forbidden);

        if (await SyncEndpoints.AuthenticatePeerAsync(ctx, tokenStore) is { } nodeId &&
            await whitelistRepo.GetByNodeIdAsync(nodeId) is { IsSuperadmin: true })
            return null;

        return Results.Unauthorized();
    }
}
