using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>Body of <c>POST /api/blind-nodes/android</c>: the phone's code and the node it will call.</summary>
public sealed record PairBlindPhoneRequest(string? Code, Guid ListenerId);

/// <summary>
/// The PC's side of pairing an Android blind node (plan section 10), next to adding a server blind node.
/// Superadmin only, like the rest of "Blind nodes": it adds a peer to the network and seals a key under
/// the DEK.
/// </summary>
public static class BlindPhoneEndpoints
{
    public static void MapBlindPhonePairingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/blind-nodes/android").WithTags("Blind").RequireInternalKey().RequireSuperadmin();

        group.MapGet("/listeners", async (BlindPhonePairingService pairing) => Results.Ok(await pairing.ListenersAsync()));

        group.MapPost("/", async (PairBlindPhoneRequest req, BlindPhonePairingService pairing, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.Json(new ErrorResponse("Paste the code shown by the phone."), statusCode: 400);
            try
            {
                return Results.Ok(await pairing.PairAsync(req.Code, req.ListenerId, ct));
            }
            catch (FormatException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
            catch (BlindPreflightFailedException ex)
            {
                return Results.Json(new { error = ex.Message, problems = ex.Problems }, statusCode: 409);
            }
            catch (BlindPhonePairingException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });
    }
}
