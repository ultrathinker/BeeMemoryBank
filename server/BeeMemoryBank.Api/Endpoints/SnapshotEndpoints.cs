using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class SnapshotEndpoints
{
    public static void MapSnapshotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/snapshots").WithTags("Snapshots");

        group.MapGet("/", (SnapshotService svc) =>
            Results.Ok(svc.List()));

        group.MapPost("/", async (SnapshotService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var info = await svc.CreateAsync();
            return Results.Created($"/api/snapshots/{info.FileName}", info);
        });

        group.MapGet("/{fileName}/download", (string fileName, SnapshotService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var filePath = svc.GetSnapshotPath(fileName);
                return Results.File(filePath, "application/gzip", Path.GetFileName(fileName));
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Snapshot {fileName} not found"));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ErrorResponse("Invalid snapshot file name"));
            }
        });

        group.MapPost("/restore", async (RestoreSnapshotRequest req, SnapshotService svc, SessionService session,
            MaintenanceModeService maintenance, HttpContext ctx, ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var unlockOk = await session.UnlockAsync(req.MasterPassword);
            if (!unlockOk)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

            string? backupFileName = null;
            try
            {
                maintenance.Enter("Restoring from snapshot...");
                session.Lock();

                if (req.CreateBackupFirst)
                {
                    logger.LogInformation("Creating backup snapshot before restore");
                    var backup = await svc.CreateAsync();
                    backupFileName = backup.FileName;
                    logger.LogInformation("Backup created: {FileName}", backupFileName);
                }

                logger.LogInformation("Starting restore from {FileName}", req.FileName);
                await svc.RestoreAsync(req.FileName);
                logger.LogInformation("Restore completed successfully");

                maintenance.Exit();
                return Results.Ok(new { success = true, backupFileName });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Restore failed");
                maintenance.Exit();
                return Results.Json(
                    new ErrorResponse($"Restore failed: {ex.Message}. {(backupFileName != null ? $"Backup available: {backupFileName}" : "No backup was created.")}"),
                    statusCode: 500);
            }
        });

        group.MapDelete("/{fileName}", (string fileName, SnapshotService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            return svc.Delete(fileName)
                ? Results.NoContent()
                : Results.NotFound(new ErrorResponse($"Snapshot {fileName} not found"));
        });
    }
}
