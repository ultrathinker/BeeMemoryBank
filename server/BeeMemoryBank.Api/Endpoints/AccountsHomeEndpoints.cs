using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Vault.SharedDoors;

namespace BeeMemoryBank.Api.Endpoints;

public static class AccountsHomeEndpoints
{
    public static void MapAccountsHomeEndpoints(this WebApplication app)
    {
        var accountsGroup = app.MapGroup("/api/accounts-home").WithTags("SharedDoors")
            .RequireInternalKey().RequireNonAgent().RequireSuperadmin();

        accountsGroup.MapGet("/", async (AccountsHomeService svc) =>
        {
            var status = await svc.GetStatusAsync();
            return Results.Ok(new AccountsHomeResponse(
                status.AccountsHomeNodeId,
                status.AccountsHomeName,
                status.IsThisNodeAccountsHome,
                status.IsThisNodeShared,
                status.ShouldOfferAccountsHome));
        });

        accountsGroup.MapPut("/", async (SetAccountsHomeRequest req, AccountsHomeService svc, HttpContext ctx) =>
        {
            try
            {
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                await svc.SetAsync(req.NodeId, ctx.RequestAborted, actor, "web");
                var status = await svc.GetStatusAsync();
                return Results.Ok(new AccountsHomeResponse(
                    status.AccountsHomeNodeId,
                    status.AccountsHomeName,
                    status.IsThisNodeAccountsHome,
                    status.IsThisNodeShared,
                    status.ShouldOfferAccountsHome));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        var sharedGroup = app.MapGroup("/api/shared-doors").WithTags("SharedDoors")
            .RequireInternalKey().RequireNonAgent().RequireSuperadmin();

        sharedGroup.MapGet("/status", async (AccountsHomeService svc) =>
        {
            var s = await svc.GetStatusAsync();
            return Results.Ok(new SharedDoorsStatusResponse(
                s.AccountsHomeNodeId,
                s.AccountsHomeName,
                s.IsThisNodeAccountsHome,
                s.IsThisNodeShared,
                s.ShouldOfferAccountsHome,
                s.BlockedSharedNodes.Select(b => new SharedDoorBlockerResponse(b.NodeId, b.DisplayName)).ToList(),
                s.LocalState.LastIssuedVersion,
                s.LocalState.LatestReceivedVersion,
                s.LocalState.AppliedVersion,
                s.LocalState.AppliedAt,
                s.LocalState.LastError,
                (s.SkippedSharedNodes ?? []).Select(b => new SharedDoorBlockerResponse(b.NodeId, b.DisplayName)).ToList(),
                s.LocalState.AccountsHomeChanged));
        });

        // "the accounts node changed" is acknowledged by a superadmin; the attention flag goes until the next change from elsewhere.
        sharedGroup.MapPost("/accounts-home-changed/acknowledge", async (AccountsHomeService svc, HttpContext ctx, IServiceProvider sp) =>
        {
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            var cleared = await svc.AcknowledgeHomeChangeAsync(actor, "web");
            sp.GetService<BeeMemoryBank.Sync.NodeStateAnnouncer>()?.RequestImmediateCheck();
            return Results.Ok(new { acknowledged = cleared });
        });
    }
}
