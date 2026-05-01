using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public record PrepareDownloadRequest(string Kind, Guid? Id, string? Path, bool WithImages);

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/downloads").WithTags("Downloads");

        group.MapPost("/prepare", async (PrepareDownloadRequest req, DownloadTokenService tokenSvc, ZipExportService exportSvc, HttpContext ctx, CancellationToken ct) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            if (!sessionUnlocked(ctx))
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var (filePath, fileName) = req.Kind switch
                {
                    "article" when req.Id.HasValue => await exportSvc.ExportArticleAsync(req.Id.Value, req.WithImages, ct),
                    "folder" when !string.IsNullOrWhiteSpace(req.Path) => await exportSvc.ExportFolderAsync(req.Path, req.WithImages, ct),
                    "all" => await exportSvc.ExportAllAsync(req.WithImages, ct),
                    _ => throw new ArgumentException("Invalid request: specify kind=article|folder|all with appropriate id or path.")
                };

                var token = tokenSvc.Register(filePath, fileName);
                return Results.Ok(new { token, fileName });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 403);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 404);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
        });

        group.MapGet("{token}", (string token, DownloadTokenService tokenSvc) =>
        {
            var entry = tokenSvc.Take(token);
            if (entry == null)
                return Results.NotFound(new ErrorResponse("Download not found or expired."));

            if (!File.Exists(entry.FilePath))
                return Results.NotFound(new ErrorResponse("Download file missing."));

            var contentType = entry.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "text/markdown; charset=utf-8";

            var stream = new DeleteOnCloseStream(entry.FilePath);
            return Results.File(stream, contentType, entry.FileName);
        });
    }

    private static bool sessionUnlocked(HttpContext ctx)
    {
        var session = ctx.RequestServices.GetRequiredService<SessionService>();
        return session.IsUnlocked;
    }

    private sealed class DeleteOnCloseStream : FileStream
    {
        private readonly string _path;

        public DeleteOnCloseStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose)
        {
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
