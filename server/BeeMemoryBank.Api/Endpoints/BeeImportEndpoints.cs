using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Api.Endpoints;

public static class BeeImportEndpoints
{
    public static void MapBeeImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/import/bee", async (
            IFormFile file,
            [FromForm] string destinationPath,
            HttpContext ctx, SessionService session, BeeImportService importService) =>
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
                var report = await importService.ImportAsync(stream, destinationPath, ctx.RequestAborted);
                return Results.Ok(report);
            }
            catch (InvalidOperationException ex)
            {
                // Missing/unparseable .bmb-manifest.json - a genuine "wrong kind of ZIP" user error.
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
