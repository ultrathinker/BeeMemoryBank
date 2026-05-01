using BeeMemoryBank.Api.Middleware;
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
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
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
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponse($"Import failed: {ex.Message}"), statusCode: 500);
            }
        }).DisableAntiforgery()
          .WithMetadata(new RequestSizeLimitAttribute(500L * 1024 * 1024))
          .WithTags("Import");
    }
}
