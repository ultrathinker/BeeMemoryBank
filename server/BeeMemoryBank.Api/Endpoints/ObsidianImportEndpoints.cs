using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Api.Endpoints;

public static class ObsidianImportEndpoints
{
    public static void MapObsidianImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/import/obsidian", async (
            IFormFile file,
            HttpContext ctx, SessionService session, ObsidianImportService importService) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (file == null || file.Length == 0)
                return Results.Json(new ErrorResponse("No file uploaded"), statusCode: 400);

            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new ErrorResponse("Please upload a .zip file"), statusCode: 400);

            try
            {
                using var stream = file.OpenReadStream();
                var report = await importService.ImportAsync(stream, ctx.RequestAborted);
                return Results.Ok(report);
            }
            catch (ZipExtractionLimitException ex)
            {
                // The upload is at fault, not the node - and the message names the offending file,
                // so it has to reach the operator rather than being flattened into a 500.
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponse($"Import failed: {ex.Message}"), statusCode: 500);
            }
        }).DisableAntiforgery()
          .WithMetadata(new RequestSizeLimitAttribute(500L * 1024 * 1024))
          .RequireInternalKey()
          .WithTags("Import");
    }
}
