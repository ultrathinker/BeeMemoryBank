using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Activity ─────────────────────────────────────────────────────────────

    public async Task<ActivityResponseDto?> GetActivityAsync(int limit = 50, int offset = 0) =>
        await http.GetFromJsonAsync<ActivityResponseDto>(
            $"/api/activity?limit={limit}&offset={offset}", JsonOpts);

    public async Task<ActivityResponseDto?> GetActivityByArticleAsync(Guid articleId, int limit = 50) =>
        await http.GetFromJsonAsync<ActivityResponseDto>(
            $"/api/activity?articleId={articleId}&limit={limit}", JsonOpts);

    // ─── Comments ─────────────────────────────────────────────────────────────

    public async Task<List<CommentDto>?> GetCommentsAsync(Guid articleId) =>
        await http.GetFromJsonAsync<List<CommentDto>>(
            $"/api/comments?articleId={articleId}", JsonOpts);

    // ─── Article Versions ─────────────────────────────────────────────────────────

    public async Task<List<ArticleVersionDto>?> GetArticleVersionsAsync(Guid articleId)
    {
        var resp = await http.GetAsync($"/api/articles/{articleId}/versions");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ArticleVersionDto>>(JsonOpts);
    }

    // ─── Sync Status ──────────────────────────────────────────────────────────

    public async Task<bool> HasPeerNewerProtocolAsync()
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonDocument>("/api/sync/status", JsonOpts);
            if (doc != null && doc.RootElement.TryGetProperty("peerNewerProtocol", out var prop))
            {
                return prop.GetBoolean();
            }
        }
        catch { }
        return false;
    }

    public async Task<JsonNode?> GetAsync(string path)
    {
        try
        {
            var resp = await http.GetAsync("/api/" + path);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JsonNode.Parse(body);
        }
        catch { return null; }
    }

    public async Task<List<MediaDto>?> ListMediaAsync(Guid articleId)
    {
        var resp = await http.GetAsync($"/api/articles/{articleId}/media");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<MediaDto>>(JsonOpts);
    }

    public async Task<MediaDownloadResult?> DownloadMediaAsync(Guid id)
    {
        var resp = await http.GetAsync($"/api/media/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        var data = await resp.Content.ReadAsByteArrayAsync();
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"{id}";
        return new MediaDownloadResult { Data = data, ContentType = contentType, FileName = fileName };
    }

    public async Task<JsonElement?> ImportObsidianAsync(IFormFile file)
    {
        using var content = new MultipartFormDataContent();
        using var fileStream = file.OpenReadStream();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/zip");
        content.Add(streamContent, "file", file.FileName);

        var resp = await http.PostAsync("/api/import/obsidian", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    throw new InvalidOperationException(e.GetString() ?? "Import failed");
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException("Import failed"); }
        }
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    public async Task<JsonElement?> ImportBeeAsync(IFormFile file, string destinationPath)
    {
        using var content = new MultipartFormDataContent();
        using var fileStream = file.OpenReadStream();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/zip");
        content.Add(streamContent, "file", file.FileName);
        content.Add(new StringContent(destinationPath), "destinationPath");

        var resp = await http.PostAsync("/api/import/bee", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    throw new InvalidOperationException(e.GetString() ?? "Import failed");
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException("Import failed"); }
        }
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }
}
