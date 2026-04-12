using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Auth headers (X-Internal-Key, X-User-Role) are added automatically
    // by InternalKeyHandler registered as a DelegatingHandler on the HttpClient.

    // ─── Session ──────────────────────────────────────────────────────────────

    public async Task<bool> UnlockAsync(string password)
    {
        var resp = await http.PostAsync("/api/session/unlock", Body(new { password }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        var resp = await http.PostAsync("/api/session/login", Body(new { username, password }));
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            string error;
            try
            {
                var errorDoc = JsonDocument.Parse(body);
                error = errorDoc.RootElement.GetProperty("error").GetString() ?? "Login failed";
            }
            catch
            {
                error = "Login failed";
            }
            var isLocked = resp.StatusCode == System.Net.HttpStatusCode.Forbidden && error.Contains("locked");
            return new LoginResult(false, error, isLocked, null, null, null, null);
        }
        var result = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts);
        return new LoginResult(true, null, false, result!.Username, result.DisplayName, result.Role, result.UserId.ToString());
    }

    public async Task LockAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/lock");

        await http.SendAsync(request);
    }

    public async Task<bool> IsUnlockedAsync()
    {
        var resp = await http.GetFromJsonAsync<SessionStatusDto>("/api/session/status", JsonOpts);
        return resp?.IsUnlocked ?? false;
    }

    // ─── Tree ─────────────────────────────────────────────────────────────────

    public async Task<TreeChildrenDto?> GetChildrenAsync(string path = "/") =>
        await http.GetFromJsonAsync<TreeChildrenDto>(
            $"/api/tree/children?path={Uri.EscapeDataString(path)}", JsonOpts);

    public async Task<Dictionary<string, List<string>>?> GetFullTreeAsync() =>
        await http.GetFromJsonAsync<Dictionary<string, List<string>>>("/api/tree", JsonOpts);

    // ─── Articles ─────────────────────────────────────────────────────────────

    public async Task<List<ArticleDto>?> ListArticlesAsync(string? treePath = null)
    {
        var url = treePath != null
            ? $"/api/articles?treePath={Uri.EscapeDataString(treePath)}"
            : "/api/articles";
        return await http.GetFromJsonAsync<List<ArticleDto>>(url, JsonOpts);
    }

    public async Task<ArticleDto?> GetArticleAsync(Guid id)
    {
        var resp = await http.GetAsync($"/api/articles/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
    }

    public async Task<ArticleContentDto?> GetArticleContentAsync(Guid id)
    {
        var resp = await http.GetAsync($"/api/articles/{id}/content");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ArticleContentDto>(JsonOpts);
    }

    public async Task<ArticleDto?> CreateArticleAsync(
        string title, string treePath, List<string> tags, string content)
    {
        var resp = await http.PostAsync("/api/articles",
            Body(new { title, treePath, tags, content }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
    }

    public async Task<ArticleDto?> UpdateArticleAsync(
        Guid id, string? title, string? treePath, List<string>? tags, string? content)
    {
        var resp = await http.PutAsync($"/api/articles/{id}",
            Body(new { title, treePath, tags, content }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
    }

    public async Task<bool> DeleteArticleAsync(Guid id)
    {
        var resp = await http.DeleteAsync($"/api/articles/{id}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MoveArticleAsync(Guid id, string newPath)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/move", Body(new { newPath }));
        return resp.IsSuccessStatusCode;
    }

    // ─── Folders ──────────────────────────────────────────────────────────────

    public async Task<bool> CreateFolderAsync(string path)
    {
        var resp = await http.PostAsync("/api/folders", Body(new { path }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RenameFolderAsync(string path, string newPath)
    {
        var req = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"/api/folders?path={Uri.EscapeDataString(path)}")
        {
            Content = Body(new { newPath })
        };
        var resp = await http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteFolderAsync(string path)
    {
        var resp = await http.DeleteAsync($"/api/folders?path={Uri.EscapeDataString(path)}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> MoveFolderAsync(string path, string newParentPath)
    {
        var resp = await http.PostAsync(
            $"/api/folders/move?path={Uri.EscapeDataString(path)}",
            Body(new { newParentPath }));
        return resp.IsSuccessStatusCode;
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

    public async Task<JsonElement?> SearchFoldersAsync(string query, int limit = 12)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>(
                $"/api/folders/search?q={Uri.EscapeDataString(query)}&limit={limit}", JsonOpts);
        }
        catch { return null; }
    }

    // ─── Search ───────────────────────────────────────────────────────────────

    public async Task<SearchResponseDto?> SearchAsync(string query, bool content = false) =>
        await http.GetFromJsonAsync<SearchResponseDto>(
            $"/api/search?q={Uri.EscapeDataString(query)}&content={content}", JsonOpts);

    // ─── Tags ─────────────────────────────────────────────────────────────────

    public async Task<List<TagDto>?> GetTagsAsync() =>
        await http.GetFromJsonAsync<List<TagDto>>("/api/tags", JsonOpts);

    // ─── Snapshots ────────────────────────────────────────────────────────────

    public async Task<List<SnapshotDto>?> GetSnapshotsAsync() =>
        await http.GetFromJsonAsync<List<SnapshotDto>>("/api/snapshots", JsonOpts);

    public async Task<SnapshotDto?> CreateSnapshotAsync()
    {
        var resp = await http.PostAsync("/api/snapshots", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotDto>(JsonOpts);
    }

    public async Task<bool> DeleteSnapshotAsync(string fileName)
    {
        var resp = await http.DeleteAsync(
            $"/api/snapshots/{Uri.EscapeDataString(fileName)}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<HttpResponseMessage?> DownloadSnapshotAsync(string fileName)
    {
        try
        {
            return await http.GetAsync(
                $"/api/snapshots/{Uri.EscapeDataString(fileName)}/download",
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch { return null; }
    }

    public async Task<(bool ok, string? error, string? backupFileName)> RestoreSnapshotAsync(
        string fileName, string masterPassword, bool createBackupFirst = true)
    {
        var resp = await http.PostAsync("/api/snapshots/restore",
            Body(new { fileName, masterPassword, createBackupFirst }));
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var backup = body.TryGetProperty("backupFileName", out var bp) ? bp.GetString() : null;
            return (true, null, backup);
        }
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Restore failed";
        }
        catch { error = "Restore failed"; }
        return (false, error, null);
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

    // ─── Keys ─────────────────────────────────────────────────────────────────

    public async Task<bool> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        var resp = await http.PostAsync("/api/keys/change-password",
            Body(new { oldPassword, newPassword }));
        return resp.IsSuccessStatusCode;
    }

    // ─── Whitelist (sync nodes) ───────────────────────────────────────────────

    public async Task<List<WhitelistEntryDto>?> GetWhitelistAsync() =>
        await http.GetFromJsonAsync<List<WhitelistEntryDto>>("/api/whitelist", JsonOpts);

    public async Task<bool> RevokeNodeAsync(Guid nodeId)
    {
        var resp = await http.DeleteAsync($"/api/whitelist/{nodeId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> AddWhitelistEntryAsync(
        Guid nodeId, string displayName, string ed25519PublicKeyB64, string? apiAddress)
    {
        var resp = await http.PostAsync("/api/whitelist",
            Body(new { nodeId, displayName, ed25519PublicKeyB64, apiAddress }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<(bool ok, string? error)> ChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/address",
            Body(new { newApiAddress, password }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<NodeIdentityDto?> GetIdentityAsync() =>
        await http.GetFromJsonAsync<NodeIdentityDto>("/api/sync/identity", JsonOpts);

    public async Task<Dictionary<Guid, DateTime>?> GetNodeSyncStatusAsync()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<SyncStatusEntry>>("/api/whitelist/sync-status", JsonOpts);
            return list?.ToDictionary(e => e.NodeId, e => e.UpdatedAt);
        }
        catch { return null; }
    }

    private record SyncStatusEntry(Guid NodeId, DateTime UpdatedAt);

    // ─── Agents ───────────────────────────────────────────────────────────────

    public async Task<List<AgentDto>?> GetAgentsAsync() =>
        await http.GetFromJsonAsync<List<AgentDto>>("/api/agents", JsonOpts);

    public async Task<AgentCreatedDto?> CreateAgentAsync(string name, string? description)
    {
        var resp = await http.PostAsync("/api/agents", Body(new { name, description }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AgentCreatedDto>(JsonOpts);
    }

    public async Task<bool> DeleteAgentAsync(int id)
    {
        var resp = await http.DeleteAsync($"/api/agents/{id}");
        return resp.IsSuccessStatusCode;
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    public async Task<List<UserDto>?> GetUsersAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");

        var resp = await http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<UserDto>>(JsonOpts);
    }

    public async Task<UserDto?> CreateUserAsync(string username, string displayName, string password, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = Body(new { username, displayName, password, role })
        };

        var resp = await http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<UserDto>(JsonOpts);
    }

    public async Task<bool> UpdateUserAsync(int id, string displayName, string? role)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}")
        {
            Content = Body(new { displayName, role })
        };

        var resp = await http.SendAsync(request);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{id}");

        var resp = await http.SendAsync(request);
        return resp.IsSuccessStatusCode;
    }

    public async Task<(bool Ok, string? Error)> ChangeOwnPasswordAsync(string oldPassword, string newPassword)
    {
        var resp = await http.PostAsync("/api/users/me/change-password",
            Body(new { oldPassword, newPassword }));
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to change password");
        }
        catch { return (false, "Failed to change password"); }
    }

    public async Task<bool> ChangeUserPasswordAsync(int id, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/users/{id}/change-password")
        {
            Content = Body(new { newPassword })
        };

        var resp = await http.SendAsync(request);
        return resp.IsSuccessStatusCode;
    }

    // ─── Folder Restrictions ────────────────────────────────────────────────

    public async Task<List<RestrictionInfoDto>?> GetUserRestrictionsAsync(int userId)
    {
        try
        {
            var resp = await http.GetAsync($"/api/restrictions/user/{userId}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<RestrictionInfoDto>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<RestrictionInfoDto?> AddUserRestrictionAsync(int userId, Guid folderId)
    {
        try
        {
            var resp = await http.PostAsync($"/api/restrictions/user/{userId}", Body(new { folderId }));
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<RestrictionInfoDto>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<bool> RemoveUserRestrictionAsync(int userId, Guid folderId)
    {
        try
        {
            var resp = await http.DeleteAsync($"/api/restrictions/user/{userId}/{folderId}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<RestrictionInfoDto>?> GetAgentRestrictionsAsync(int agentId)
    {
        try
        {
            var resp = await http.GetAsync($"/api/restrictions/agent/{agentId}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<RestrictionInfoDto>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<RestrictionInfoDto?> AddAgentRestrictionAsync(int agentId, Guid folderId)
    {
        try
        {
            var resp = await http.PostAsync($"/api/restrictions/agent/{agentId}", Body(new { folderId }));
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<RestrictionInfoDto>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<bool> RemoveAgentRestrictionAsync(int agentId, Guid folderId)
    {
        try
        {
            var resp = await http.DeleteAsync($"/api/restrictions/agent/{agentId}/{folderId}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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

    // ─── Deploy ───────────────────────────────────────────────────────────────

    public async Task<bool> IsDeployEnabledAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/deploy/enabled");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<(bool ok, string output)> DeployAsync(string password)
    {
        var resp = await http.PostAsync("/api/deploy", Body(new { password }));
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.IsSuccessStatusCode, body);
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

    public async Task<MediaDto?> UploadMediaAsync(IFormFile file, string? articleId)
    {
        using var content = new MultipartFormDataContent();
        using var fileStream = file.OpenReadStream();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(streamContent, "file", file.FileName);
        if (!string.IsNullOrEmpty(articleId))
            content.Add(new StringContent(articleId), "articleId");

        var resp = await http.PostAsync("/api/media", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<MediaDto>(JsonOpts);
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static StringContent Body(object obj) =>
        new(JsonSerializer.Serialize(obj, JsonOpts), Encoding.UTF8, "application/json");
}
