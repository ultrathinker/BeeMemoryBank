using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;

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

        group.MapPost("/", async (AddBlindNodeRequest req, BlindNodeManager manager, CancellationToken ct) =>
        {
            try
            {
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
        });

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
        });
    }
}

public sealed record AddBlindNodeRequest(string Code);
