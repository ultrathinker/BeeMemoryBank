using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>The invitation text <c>bmb blind join</c> hands to the node. A bearer secret: it is read from the body and nowhere else, and never logged.</summary>
public sealed record BlindJoinRequest(string? Invitation)
{
    public override string ToString() => "BlindJoinRequest { Invitation = *** }";
}

/// <summary>
/// The local routes of <c>bmb blind join</c> (release 2.7), mapped on a blind node only: the CLI asks the running node to look at an invitation, to join with it, to say where the
/// join stands, and to give it up. Internal key and superadmin like the rest of <c>/api/blind/*</c>: the node's own operator. The node does the work (it holds its keys; the CLI
/// reads no key file) - see <see cref="BlindJoinService"/>. A superadmin's agent inherits its owner's rights, so the group is closed to agents as well
/// (<c>RequireNonAgent</c>, as the full node's invitation-confirmation group is): joining a full node is a decision of the person at this node's console.
/// </summary>
public static class BlindJoinEndpoints
{
    public static void MapBlindJoinEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/blind/join").WithTags("Blind").RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        group.MapPost("/preview", (BlindJoinRequest req, BlindJoinService join) => Guarded(() => Task.FromResult(join.Preview(req.Invitation))));
        group.MapPost("/", (BlindJoinRequest req, BlindJoinService join, HttpContext ctx) => Guarded(() => join.StartAsync(req.Invitation, ctx.RequestAborted)));
        group.MapGet("/", (BlindJoinService join, HttpContext ctx) => Guarded(() => join.StatusAsync(ctx.RequestAborted)));
        group.MapDelete("/", (BlindJoinService join, HttpContext ctx) => Guarded(() => join.CancelAsync(ctx.RequestAborted)));
    }

    private static async Task<IResult> Guarded<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (BlindJoinException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.Status);
        }
    }
}
