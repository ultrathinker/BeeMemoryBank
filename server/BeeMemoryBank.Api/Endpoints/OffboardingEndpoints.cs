// The removal of a superadmin (release 2.12.0, plan WS-E): start, list, follow, continue, decide the review, revoke a node that is waited for,
// take over. Superadmins only, never an agent; every refusal is a fixed sentence (no exception text).

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class OffboardingEndpoints
{
    public static void MapOffboardingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/offboarding").WithTags("Offboarding").RequireInternalKey().RequireSuperadmin();
        group.AddEndpointFilter<RequireNonAgentFilter>();

        group.MapGet("/", async (OffboardingService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, OffboardingService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } view ? Results.Ok(view) : Results.NotFound(new ErrorResponse("There is no such removal.")));

        group.MapPost("/", async (StartOffboardingRequest req, OffboardingService service, IUserRepository users, HttpContext ctx) =>
            await RunAsync(async () =>
            {
                var actor = await ActorAsync(ctx, users, req.ActingUser);
                var rules = ToRules(req.FolderRules);
                var id = await service.StartAsync(
                    new OffboardingStartRequest(req.UserId, req.Target?.Trim().ToLowerInvariant() ?? "", req.Role, rules, req.AllowOpenRole, req.Password ?? ""),
                    actor, ctx.RequestAborted);
                return Results.Accepted($"/api/offboarding/{id:D}", await service.GetAsync(id));
            }));

        group.MapPost("/{id:guid}/continue", async (Guid id, ContinueOffboardingRequest? req, OffboardingService service, IUserRepository users, HttpContext ctx) =>
            await RunAsync(async () =>
            {
                var actor = string.IsNullOrEmpty(req?.Password) ? 0 : await ActorAsync(ctx, users, req.ActingUser);
                await service.ContinueAsync(id, req?.Password, actor, ctx.RequestAborted);
                return await service.GetAsync(id) is { } view ? Results.Ok(view) : Results.NotFound(new ErrorResponse("There is no such removal."));
            }));

        group.MapPost("/{id:guid}/review", async (Guid id, ResolveOffboardingReviewRequest req, OffboardingService service, IUserRepository users, HttpContext ctx) =>
            await RunAsync(async () =>
            {
                var actor = await ActorAsync(ctx, users, req.ActingUser);
                var decisions = (req.Decisions ?? []).Select(d => new OffboardingDecision(d.ItemId ?? "", d.Decision ?? "", d.Reason,
                    string.IsNullOrEmpty(d.Target) ? null : new OffboardingSuperadminTarget(d.Target.Trim().ToLowerInvariant(), d.Role, ToRules(d.FolderRules), d.AllowOpenRole))).ToList();
                await service.ResolveReviewAsync(id, decisions, req.AcceptDefaults, actor, req.Password, ctx.RequestAborted);
                return await service.GetAsync(id) is { } view ? Results.Ok(view) : Results.NotFound(new ErrorResponse("There is no such removal."));
            }));

        group.MapPost("/{id:guid}/nodes/{nodeId:guid}/revoke", async (Guid id, Guid nodeId, OffboardingService service, IUserRepository users, HttpContext ctx) =>
            await RunAsync(async () =>
            {
                var actor = await ActorAsync(ctx, users, null, allowMachineLocal: true);
                await service.RevokeNodeAsync(id, nodeId, actor, ctx.RequestAborted);
                return await service.GetAsync(id) is { } view ? Results.Ok(view) : Results.NotFound(new ErrorResponse("There is no such removal."));
            }));

        group.MapPost("/{id:guid}/take-over", async (Guid id, TakeOverOffboardingRequest req, OffboardingService service, IUserRepository users, HttpContext ctx) =>
            await RunAsync(async () =>
            {
                var actor = await ActorAsync(ctx, users, req.ActingUser);
                await service.TakeOverAsync(id, req.Password ?? "", actor, ctx.RequestAborted);
                return await service.GetAsync(id) is { } view ? Results.Ok(view) : Results.NotFound(new ErrorResponse("There is no such removal."));
            }));
    }

    /// <summary>Folder rules of a demotion from the request shape the Users page already sends.</summary>
    internal static List<DemotionFolderRule>? ToRules(List<AddAclEntryRequest>? rules)
    {
        if (rules is null) return null;
        var list = new List<DemotionFolderRule>();
        foreach (var rule in rules)
        {
            if (!Enum.TryParse<AclEffect>(rule.Effect, ignoreCase: true, out var effect))
                throw new OffboardingRefusedException("Invalid effect. Use 'allow' or 'deny'.", 400);
            list.Add(new DemotionFolderRule(rule.FolderId, effect, rule.IsReadOnly));
        }
        return list;
    }

    /// <summary>
    /// Who acts: the signed-in person (the Web always forwards them; the API reads their role from its own table), or - for a machine-local caller that
    /// holds the internal key and names nobody (the CLI) - the superadmin the request names, whose password the service checks.
    /// </summary>
    internal static async Task<int> ActorAsync(HttpContext ctx, IUserRepository users, string? actingUser, bool allowMachineLocal = false)
    {
        if (CallerIdentity.Extract(ctx).UserId is { } userId) return userId;
        if (!string.IsNullOrWhiteSpace(actingUser) && await users.GetByUsernameAsync(actingUser.Trim()) is { } named) return named.Id;
        if (allowMachineLocal) return 0;
        throw new OffboardingRefusedException("Say which superadmin acts (your user name), and confirm with your password.", 400);
    }

    internal static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (OffboardingRefusedException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
        }
        catch (SessionLockedException)
        {
            return Results.Json(new ErrorResponse("Unlock this node first."), statusCode: 409);
        }
    }
}
