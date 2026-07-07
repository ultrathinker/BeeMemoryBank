using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class FolderProxyEndpoints
{
    public static void MapFolderProxyEndpoints(this WebApplication app)
    {
        app.MapPost("/api-proxy/folders", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CreateFolderProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.CreateFolderAsync(req.Path);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization();

        app.MapMethods("/api-proxy/folders", ["PATCH"], async (HttpContext ctx, ApiClient api, string path) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<RenameFolderProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.RenameFolderAsync(path, req.NewPath);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/folders", async (ApiClient api, string path) =>
        {
            var (ok, status, error) = await api.DeleteFolderAsync(path);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/folders/move", async (HttpContext ctx, ApiClient api, string path) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<MoveFolderProxyRequest>();
            if (req == null) return Results.BadRequest();
            var (ok, status, error) = await api.MoveFolderAsync(path, req.NewParentPath);
            return ok ? Results.Ok() : Results.Json(new { error }, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/folders/search", async (ApiClient api, string? q, int limit = 12) =>
        {
            var result = await api.SearchFoldersAsync(q ?? "", limit);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/folders/download", async (ApiClient api, string path) =>
        {
            var result = await api.DownloadFolderZipAsync(path);
            if (result == null) return Results.StatusCode(502);
            if (!result.IsSuccessStatusCode)
                return Results.StatusCode((int)result.StatusCode);

            var folderName = path.TrimEnd('/').Split('/').LastOrDefault("folder");
            var stream = await result.Content.ReadAsStreamAsync();
            return Results.File(new DisposingStreamWrapper(stream, result), "application/zip", folderName + ".zip");
        }).RequireAuthorization();

        app.MapPost("/api-proxy/downloads/prepare", async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("downloads/prepare", json);
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/downloads/{token}", async (string token, ApiClient api) =>
        {
            var result = await api.DownloadByTokenAsync(token);
            if (result == null) return Results.StatusCode(502);
            if (!result.IsSuccessStatusCode) return Results.StatusCode((int)result.StatusCode);
            var stream = await result.Content.ReadAsStreamAsync();
            var contentType = result.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var fileName = result.Content.Headers.ContentDisposition?.FileNameStar
                ?? result.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? "download";
            return Results.File(new DisposingStreamWrapper(stream, result), contentType, fileName);
        }).RequireAuthorization();
    }
}
