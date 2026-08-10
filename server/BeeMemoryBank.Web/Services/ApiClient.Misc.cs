using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    public async Task<HttpResponseMessage?> DownloadByTokenAsync(string token)
    {
        try
        {
            return await http.GetAsync($"/api/downloads/{token}", HttpCompletionOption.ResponseHeadersRead);
        }
        catch { return null; }
    }

    public async Task<HttpResponseMessage?> DownloadFolderZipAsync(string path)
    {
        try
        {
            return await http.GetAsync(
                $"/api/folders/download?path={Uri.EscapeDataString(path)}",
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch { return null; }
    }

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

    public async Task<CommentDto?> AddCommentAsync(Guid articleId, string text)
    {
        var resp = await http.PostAsync("/api/comments", Body(new { articleId, text }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CommentDto>(JsonOpts);
    }

    public async Task<bool> DeleteCommentAsync(int id)
    {
        var resp = await http.DeleteAsync($"/api/comments/{id}");
        return resp.IsSuccessStatusCode;
    }

    // ─── Article Versions ─────────────────────────────────────────────────────────

    public async Task<List<ArticleVersionDto>?> GetArticleVersionsAsync(Guid articleId)
    {
        var resp = await http.GetAsync($"/api/articles/{articleId}/versions");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ArticleVersionDto>>(JsonOpts);
    }

    public async Task<ArticleVersionContentDto?> GetArticleVersionContentAsync(Guid articleId, int versionNumber)
    {
        var resp = await http.GetAsync($"/api/articles/{articleId}/versions/{versionNumber}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ArticleVersionContentDto>(JsonOpts);
    }

    // ─── Sync Status ──────────────────────────────────────────────────────────

    public async Task<object?> GetSyncStatusAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<object>("/api/sync/status", JsonOpts);
        }
        catch { return null; }
    }

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

    public async Task<bool> GetInvisibleModeAsync()
    {
        try
        {
            var result = await GetAsync("sync/invisible");
            if (result != null && result["isInvisible"] != null)
                return result["isInvisible"]!.GetValue<bool>();
        }
        catch { }
        return false;
    }

    public async Task<bool> SetInvisibleModeAsync(bool isInvisible)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/invisible");
            req.Content = Body(isInvisible);
            var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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

    public async Task<(MediaDto? Media, int Status, string? Error)> UploadMediaAsync(IFormFile file, string? articleId, bool isAttachment = false)
    {
        using var content = new MultipartFormDataContent();
        using var fileStream = file.OpenReadStream();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(streamContent, "file", file.FileName);
        if (!string.IsNullOrEmpty(articleId))
            content.Add(new StringContent(articleId), "articleId");
        if (isAttachment)
            // Must be a form field, not a query-string param: the API endpoint has an IFormFile
            // parameter, which makes ASP.NET Core infer [FromForm] for every other simple-type
            // parameter (including this one) — a query string value would be silently ignored,
            // and the upload would always fall through to the stricter image-only path.
            content.Add(new StringContent("true"), "attachment");

        var resp = await http.PostAsync("/api/media", content);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var errBody = await resp.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    return (null, status, e.GetString() ?? "Upload failed");
            }
            catch { }
            return (null, status, "Upload failed");
        }
        return (await resp.Content.ReadFromJsonAsync<MediaDto>(JsonOpts), (int)resp.StatusCode, null);
    }

    public async Task<List<MediaDto>?> ListMediaAsync(Guid articleId)
    {
        var resp = await http.GetAsync($"/api/articles/{articleId}/media");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<MediaDto>>(JsonOpts);
    }

    public async Task<bool> DeleteMediaAsync(Guid id)
    {
        var resp = await http.DeleteAsync($"/api/media/{id}");
        return resp.IsSuccessStatusCode;
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

    public async Task<PagedList<HardDeleteListItem>?> HardDeleteListAsync(int page, int pageSize, string? filter, HardDeleteStatusFilter status)
    {
        var url = $"/api/hard-delete/list?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(filter)) url += $"&filter={Uri.EscapeDataString(filter)}";
        url += $"&status={status}";
        return await http.GetFromJsonAsync<PagedList<HardDeleteListItem>>(url, JsonOpts);
    }

    public async Task<HardDeletePreview?> HardDeletePreviewFolderAsync(string path)
    {
        var resp = await http.PostAsJsonAsync("/api/hard-delete/folder/preview", new { path }, JsonOpts);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<HardDeletePreview>(JsonOpts);
    }

    public async Task<HardDeleteResult?> HardDeleteArticleAsync(Guid id)
    {
        var resp = await http.PostAsync($"/api/hard-delete/article/{id}", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<HardDeleteResult>(JsonOpts);
    }

    public async Task<HardDeleteResult?> HardDeleteFolderAsync(string path)
    {
        var resp = await http.PostAsJsonAsync("/api/hard-delete/folder", new { path }, JsonOpts);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<HardDeleteResult>(JsonOpts);
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> RestoreArticleAsync(Guid id)
    {
        var resp = await http.PostAsync($"/api/hard-delete/restore/article/{id}", null);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement?>(JsonOpts);
        if (!resp.IsSuccessStatusCode)
        {
            string err = "Restore failed";
            if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object && body.Value.TryGetProperty("error", out var e))
                err = e.GetString() ?? err;
            return (false, err, null);
        }
        return (true, null, body);
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> RestoreFolderAsync(Guid id)
    {
        var resp = await http.PostAsync($"/api/hard-delete/restore/folder/{id}", null);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement?>(JsonOpts);
        if (!resp.IsSuccessStatusCode)
        {
            string err = "Restore failed";
            if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object && body.Value.TryGetProperty("error", out var e))
                err = e.GetString() ?? err;
            return (false, err, null);
        }
        return (true, null, body);
    }

    public async Task<PagedList<HardDeleteAuditEntry>?> HardDeleteAuditAsync(int page, int pageSize)
    {
        return await http.GetFromJsonAsync<PagedList<HardDeleteAuditEntry>>($"/api/hard-delete/audit?page={page}&pageSize={pageSize}", JsonOpts);
    }
}
