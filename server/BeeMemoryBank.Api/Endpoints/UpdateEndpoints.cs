using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// /node/update/* — HTTP surface for the manual update choreography state machine
/// (UpdateService). Mirrors the auth/style conventions of DekRotationEndpoints /
/// SnapshotEndpoints: internal-key-gated group, superadmin-only on every mutating
/// endpoint, JSON error responses via the shared ErrorResponse record.
///
/// Scope note (task §6): the state machine CAN be driven step-by-step over HTTP, so this
/// file exposes the full set — status, check, download, apply, reset — not just status+check.
/// Real binary-swap / process restart is still out of scope (UpdateService.ApplyAsync only
/// simulates the apply + runs the pluggable health check); these endpoints let a future UI or
/// admin drive the proven state machine end-to-end once the Velopack integration lands.
/// </summary>
public static class UpdateEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapUpdateEndpoints(this WebApplication app)
    {
        // Driving the self-update state machine is superadmin-only for every route in the group.
        // RequireNonAgent for the same reason as /api/dek-rotation: the header compare this
        // replaces was never satisfiable by an agent request, and applying a software update on
        // its owner's behalf is not a capability an MCP key should gain.
        var group = app.MapGroup("/node/update").WithTags("NodeUpdate")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        // GET /node/update/status — current state machine snapshot. Safe to poll.
        // Superadmin-gated like the rest of the group: the payload surfaces internal
        // operational state (gates, version, error text) that non-admins have no need for.
        group.MapGet("/status", (UpdateService svc) =>
        {
            return Results.Ok(svc.GetProgress());
        });

        // POST /node/update/check — verify a supplied manifest signature and compare versions.
        // The caller passes the raw releases.json bytes plus its detached base64 Ed25519
        // signature; UpdateService verifies against the two embedded release keys before
        // deciding an update is available. Idempotent against repeated calls when already
        // up-to-date (returns to Idle).
        group.MapPost("/check", async (UpdateCheckRequest req, UpdateService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.ManifestJson))
                return Results.Json(new ErrorResponse("manifestJson is required"), statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.ManifestSignatureBase64))
                return Results.Json(new ErrorResponse("manifestSignatureBase64 is required"), statusCode: 400);

            try
            {
                var updateAvailable = await svc.CheckAsync(req.ManifestJson, req.ManifestSignatureBase64);
                var progress = svc.GetProgress();
                return Results.Ok(new { updateAvailable, progress });
            }
            catch (InvalidOperationException ex)
            {
                // Single-flight: another update op is already running.
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        // POST /node/update/download — download + SHA-256-verify the manifest's first artifact
        // via the configured IUpdateArtifactSource. The same signed manifest is re-posted so the
        // service can locate the artifact descriptor and its declared hash. Must be called from
        // the UpdateAvailable state produced by /check.
        group.MapPost("/download", async (UpdateCheckRequest req, UpdateService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.ManifestJson))
                return Results.Json(new ErrorResponse("manifestJson is required"), statusCode: 400);

            ReleasesManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ReleasesManifest>(req.ManifestJson, JsonOpts)
                    ?? throw new InvalidOperationException("Manifest deserialized to null.");
            }
            catch (JsonException ex)
            {
                return Results.Json(new ErrorResponse($"Manifest JSON is malformed: {ex.Message}"), statusCode: 400);
            }

            try
            {
                await svc.DownloadAsync(manifest);
                return Results.Ok(svc.GetProgress());
            }
            catch (InvalidOperationException ex)
            {
                // Wrong state (e.g. haven't /check'd yet) or a concurrent op.
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        // POST /node/update/apply — run the safety gates, take the pre-update backup, simulate
        // the apply, and run the post-apply health check. Must be called from ReadyToApply.
        // Long-ish running (health-check retries); the response carries the terminal progress
        // (Completed or Failed, with BlockedGates / ErrorMessage populated as appropriate).
        group.MapPost("/apply", async (UpdateService svc) =>
        {
            try
            {
                await svc.ApplyAsync();
                return Results.Ok(svc.GetProgress());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        // POST /node/update/reset — return the state machine to Idle. Lets an admin clear a
        // Failed state (e.g. after inspecting the pre-update backup) and start over.
        group.MapPost("/reset", async (UpdateService svc) =>
        {
            await svc.ResetAsync();
            return Results.Ok(svc.GetProgress());
        });
    }
}
