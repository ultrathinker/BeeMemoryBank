using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// The PC side of "Blind nodes" (plan 4.2, 5.2, 9): add a blind node by its pair code, reseed it. Full node only - it is split out of
/// <see cref="BlindEndpoints"/> (which holds what the blind node itself serves) so that a blind node does not contain the
/// management code (<c>BlindNodeManager</c>, <c>BlindPreflight</c>).
/// </summary>
public static class BlindNodeManagementEndpoints
{
    /// <summary>Mapped on a full node: the PC side of "Blind nodes" (plan 4.2, 5.2, 9).</summary>
    public static void MapBlindNodeManagementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/blind-nodes").WithTags("Blind").RequireInternalKey().RequireSuperadmin();

        group.MapGet("/", async (BlindNodeManager manager) => Results.Ok(await manager.ListAsync()));

        // Which blind nodes need attention (BMB-77): the list the desktop shell turns into notifications, through the node front's
        // /node/alarms. Kinds, times and protocol numbers only, and names only while the vault is unlocked.
        group.MapGet("/alarms", async (BlindNodeManager manager) => Results.Ok(await manager.GetAlarmsAsync()));

        group.MapPost("/", async (AddBlindNodeRequest req, BlindNodeManager manager, BlindInviteService invites, CancellationToken ct) =>
        {
            try
            {
                // One way of adding a node at a time: this node has redeemed an invitation (bmb blind join) that the owner has not answered yet.
                if (await invites.ServerInvitationIsPendingAsync(req.Code))
                    return Results.Json(new ErrorResponse(
                        "This blind node has redeemed an invitation that is waiting for your answer. Confirm or refuse the invitation instead of using its pair code."), statusCode: 409);
                return Results.Ok(await manager.AddAsync(req.Code, ct));
            }
            catch (FormatException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
            catch (BlindPreflightFailedException ex)
            {
                return Results.Json(new { error = ex.Message, problems = ex.Problems, details = ex.Details }, statusCode: 409);
            }
            catch (BlindNodeUnreachableException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 502);
            }
            catch (PeerPausedException ex)
            {
                return Results.Json(new ErrorResponse(BlindNodeManager.PausedSeedSentence(ex)), statusCode: 409);
            }
            catch (BaselineNotReadyException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        })
            .SingleFlight("blind-add", "Adding a blind node");

        group.MapPost("/{nodeId:guid}/reseed", async (Guid nodeId, BlindNodeManager manager, CancellationToken ct) =>
        {
            try
            {
                await manager.ReseedAsync(nodeId, ct);
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (BlindNodeUnreachableException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 502);
            }
            catch (PeerPausedException ex)
            {
                return Results.Json(new ErrorResponse(BlindNodeManager.PausedSeedSentence(ex)), statusCode: 409);
            }
            catch (BaselineNotReadyException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        })
            .SingleFlight("blind-reseed:{nodeId}", "A reseed of this blind node");
    }
}

public sealed record AddBlindNodeRequest(string Code);
