using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Auth headers (X-Internal-Key, X-User-Role) are added automatically
    // by InternalKeyHandler registered as a DelegatingHandler on the HttpClient.

    // ─── Init ────────────────────────────────────────────────────────────────

    public async Task<bool?> GetInitStatusAsync()
    {
        try
        {
            var resp = await http.GetFromJsonAsync<InitStatusDto>("/api/init/status", JsonOpts);
            return resp?.Initialized;
        }
        catch
        {
            return null; // API unreachable — unknown state
        }
    }

    public async Task<(bool Ok, string? Error)> InitStandaloneAsync(string adminUsername, string displayName, string password)
    {
        var resp = await http.PostAsync("/api/init/standalone",
            Body(new { adminUsername, displayName, password }));
        if (resp.IsSuccessStatusCode)
            return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(body);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Initialization failed";
        }
        catch { error = "Initialization failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> InitJoinAsync(string adminUsername, string displayName, string remoteUrl, string password)
    {
        var resp = await http.PostAsync("/api/init/join",
            Body(new { adminUsername, displayName, remoteUrl, password }));
        if (resp.IsSuccessStatusCode)
            return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(body);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Join failed";
        }
        catch { error = "Join failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> ResetNodeAsync(string masterPassword)
    {
        var resp = await http.PostAsJsonAsync("/api/init/reset",
            new { masterPassword }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, err);
    }

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
            return new LoginResult(false, error, isLocked, null, null, null, null, null);
        }
        var result = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts);
        return new LoginResult(true, null, false, result!.Username, result.DisplayName, result.Role, result.UserId.ToString(), result.MigratedSyntheticUsername);
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

    public async Task<TreeChildrenDto?> GetChildrenAsync(string path = "/")
    {
        // Use raw GetAsync so ACL-denied (404 from API) returns null instead of throwing —
        // the caller's contract is "null → 404 for the user". Previously GetFromJsonAsync<T>
        // threw HttpRequestException on non-success, which bubbled up as a 500 to the browser
        // when a user tried to list a folder they don't have access to.
        var resp = await http.GetAsync($"/api/tree/children?path={Uri.EscapeDataString(path)}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<TreeChildrenDto>(JsonOpts);
    }

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
        string title, string treePath, string content)
    {
        var (article, _, _) = await CreateArticleWithErrorAsync(title, treePath, content);
        return article;
    }

    public async Task<(ArticleDto? Article, int Status, string? Error)> CreateArticleWithErrorAsync(
        string title, string treePath, string content)
    {
        var resp = await http.PostAsync("/api/articles",
            Body(new { title, treePath, content }));
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, await ReadErrorAsync(resp));
        var dto = await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
        return (dto, (int)resp.StatusCode, null);
    }

    public async Task<ArticleDto?> UpdateArticleAsync(
        Guid id, string? title, string? treePath, string? content)
    {
        var (article, _, _) = await UpdateArticleWithErrorAsync(id, title, treePath, content);
        return article;
    }

    public async Task<(ArticleDto? Article, int Status, string? Error)> UpdateArticleWithErrorAsync(
        Guid id, string? title, string? treePath, string? content)
    {
        var resp = await http.PutAsync($"/api/articles/{id}",
            Body(new { title, treePath, content }));
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, await ReadErrorAsync(resp));
        var dto = await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
        return (dto, (int)resp.StatusCode, null);
    }

    public async Task<(bool ok, int status, string? error)> DeleteArticleAsync(Guid id)
    {
        var resp = await http.DeleteAsync($"/api/articles/{id}");
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> MoveArticleAsync(Guid id, string newPath)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/move", Body(new { newPath }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    // ─── Folders ──────────────────────────────────────────────────────────────

    public async Task<(bool ok, int status, string? error)> CreateFolderAsync(string path)
    {
        var resp = await http.PostAsync("/api/folders", Body(new { path }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> RenameFolderAsync(string path, string newPath)
    {
        var req = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"/api/folders?path={Uri.EscapeDataString(path)}")
        {
            Content = Body(new { newPath })
        };
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> DeleteFolderAsync(string path)
    {
        var resp = await http.DeleteAsync($"/api/folders?path={Uri.EscapeDataString(path)}");
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> MoveFolderAsync(string path, string newParentPath)
    {
        var resp = await http.PostAsync(
            $"/api/folders/move?path={Uri.EscapeDataString(path)}",
            Body(new { newParentPath }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Body, int Status)> PostRawAsync(string path, string json)
    {
        var resp = await http.PostAsync("/api/" + path, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.IsSuccessStatusCode, body, (int)resp.StatusCode);
    }

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

    // ─── Concept Tags ─────────────────────────────────────────────────────────

    public async Task<List<ConceptTagDto>?> GetAllConceptTagsAsync(string? q = null, int limit = 500)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(q)) qs.Add($"q={Uri.EscapeDataString(q)}");
        qs.Add($"limit={limit}");
        var url = "/api/concept-tags?" + string.Join("&", qs);
        return await http.GetFromJsonAsync<List<ConceptTagDto>>(url, JsonOpts);
    }

    public async Task<List<ConceptGraphEdgeDto>?> GetConceptGraphAsync()
    {
        return await http.GetFromJsonAsync<List<ConceptGraphEdgeDto>>("/api/concept-tags/graph", JsonOpts);
    }

    public async Task<System.Text.Json.Nodes.JsonNode?> GetConceptGraphNeighborsAsync(string tag)
    {
        return await http.GetFromJsonAsync<System.Text.Json.Nodes.JsonNode>(
            $"/api/concept-tags/graph/neighbors?tag={Uri.EscapeDataString(tag)}", JsonOpts);
    }

    public async Task<JsonElement?> GetConceptGraphHomeAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>("/api/concept-tags/graph/home", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<JsonElement?> GetConceptGraphSearchAsync(string q, int depth, int maxNodes)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>(
                $"/api/concept-tags/graph/search?q={Uri.EscapeDataString(q)}&depth={depth}&maxNodes={maxNodes}", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<List<string>?> GetArticleConceptTagsAsync(Guid articleId)
    {
        try
        {
            var resp = await http.GetFromJsonAsync<JsonNode>($"/api/articles/{articleId}/concept-tags", JsonOpts);
            return resp?["conceptTags"]?.Deserialize<List<string>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<bool> SetArticleConceptTagsAsync(Guid articleId, List<string> conceptTags)
    {
        var resp = await http.PutAsync($"/api/articles/{articleId}/concept-tags",
            Body(new { conceptTags }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<(bool ok, int status, string? error)> RenameConceptTagAsync(string name, string newName)
    {
        var resp = await http.PutAsync($"/api/concept-tags/{Uri.EscapeDataString(name)}",
            Body(new { newName }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        var err = await TryReadErrorAsync(resp);
        return (false, (int)resp.StatusCode, err ?? "Rename failed");
    }

    public async Task<(bool ok, int status, string? error)> MergeConceptTagsAsync(string source, string target)
    {
        var resp = await http.PostAsync("/api/concept-tags/merge",
            Body(new { source, target }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        var err = await TryReadErrorAsync(resp);
        return (false, (int)resp.StatusCode, err ?? "Merge failed");
    }

    public async Task<(bool ok, int status, string? error)> DeleteConceptTagAsync(string name)
    {
        var resp = await http.DeleteAsync($"/api/concept-tags/{Uri.EscapeDataString(name)}");
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        var err = await TryReadErrorAsync(resp);
        return (false, (int)resp.StatusCode, err ?? "Delete failed");
    }

    private async Task<string?> TryReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            return err?["error"]?.GetValue<string>();
        }
        catch { return null; }
    }

    public async Task<List<RelatedArticleDto>?> GetRelatedArticlesAsync(Guid articleId)
    {
        return await http.GetFromJsonAsync<List<RelatedArticleDto>>($"/api/articles/{articleId}/related", JsonOpts);
    }

    public async Task<JsonElement?> GetArticlesByConceptTagAsync(string name)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>(
                $"/api/concept-tags/{Uri.EscapeDataString(name)}/articles", JsonOpts);
        }
        catch { return null; }
    }

    // ─── Snapshots ────────────────────────────────────────────────────────────

    public async Task<JsonElement?> GetConceptTagEdgeStatsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>("/api/admin/concept-tag-edge/stats", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<JsonElement?> RebuildConceptTagEdgesAsync()
    {
        try
        {
            var resp = await http.PostAsync("/api/admin/concept-tag-edge/rebuild", null);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<List<SnapshotDto>?> GetSnapshotsAsync() =>
        await http.GetFromJsonAsync<List<SnapshotDto>>("/api/snapshots", JsonOpts);

    public async Task<SnapshotDto?> CreateSnapshotAsync()
    {
        var resp = await http.PostAsync("/api/snapshots", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotDto>(JsonOpts);
    }

    public async Task<SnapshotUploadDto?> UploadSnapshotAsync(IFormFile file)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(file.OpenReadStream());
        content.Add(streamContent, "file", file.FileName);
        var resp = await http.PostAsync("/api/snapshots/upload", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotUploadDto>(JsonOpts);
    }

    public async Task<(bool ok, string? eventId, string? error)> InitiateNetworkRestoreAsync(Guid snapshotFileId)
    {
        var resp = await http.PostAsJsonAsync("/api/snapshots/restore-network", new {
            SnapshotFileId = snapshotFileId,
            Mode = "NetworkWide",
            ForeignMasterPassword = (string?)null
        }, JsonOpts);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (true, body.GetProperty("eventId").GetString(), null);
    }

    public async Task<RestoreProgressDto?> GetRestoreProgressAsync()
    {
        var resp = await http.GetAsync("/api/snapshots/restore/progress");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RestoreProgressDto>(JsonOpts);
    }

    public async Task<bool> ContinueRestoreWithoutBackupAsync(Guid eventId, string masterPassword)
    {
        var resp = await http.PostAsJsonAsync("/api/snapshots/restore/continue-without-backup",
            new { EventId = eventId, MasterPassword = masterPassword }, JsonOpts);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> CancelRestoreAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/snapshots/restore/cancel?eventId={Uri.EscapeDataString(eventId)}", null);
        return resp.IsSuccessStatusCode;
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
        string fileName, string masterPassword, bool createBackupFirst = true, bool standaloneMode = false)
    {
        var resp = await http.PostAsync("/api/snapshots/restore",
            Body(new { fileName, masterPassword, createBackupFirst, standaloneMode }));
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

    // ─── DEK Rotation ─────────────────────────────────────────────────────────

    public async Task<DekRotationProgressDto?> GetDekRotationProgressAsync()
    {
        var resp = await http.GetAsync("/api/dek-rotation/progress");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DekRotationProgressDto>(JsonOpts);
    }

    public async Task<(bool Ok, string? CommitEventId, string? Error)> ProposeDekRotationAsync(string masterPassword)
    {
        var resp = await http.PostAsync("/api/dek-rotation/propose",
            Body(new { masterPassword }));
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var commitId = body.TryGetProperty("commitEventId", out var cid) ? cid.GetString() : null;
            return (true, commitId, null);
        }
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation propose failed";
        }
        catch { error = "DEK rotation propose failed"; }
        return (false, null, error);
    }

    public async Task<(bool Ok, string? Error)> AcceptDekRotationAsync(string commitEventId, string masterPassword)
    {
        var resp = await http.PostAsync("/api/dek-rotation/accept",
            Body(new { commitEventId, masterPassword }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation accept failed";
        }
        catch { error = "DEK rotation accept failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> CancelDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/cancel/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation cancel failed";
        }
        catch { error = "DEK rotation cancel failed"; }
        return (false, error);
    }

    public async Task<List<PeerPendingDekRotationDto>?> GetPeerPendingDekRotationsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<List<PeerPendingDekRotationDto>>("/api/dek-rotation/peer-pending", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> PeerAcceptDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/peer-accept/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Peer accept failed";
        }
        catch { error = "Peer accept failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> PeerRejectDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/peer-reject/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Peer reject failed";
        }
        catch { error = "Peer reject failed"; }
        return (false, error);
    }

    // ─── Activity ─────────────────────────────────────────────────────────────

    public async Task<CompactionPreviewDto?> GetCompactionPreviewAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<CompactionPreviewDto>("/api/admin/compact/preview", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, CompactionResultDto? Result)> CompactAsync(long? explicitCp = null, string reason = "manual")
    {
        var resp = await http.PostAsJsonAsync("/api/admin/compact",
            new { explicitCp, reason }, JsonOpts);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<CompactionResultDto>(JsonOpts);
            return (true, null, result);
        }
        var err = await resp.Content.ReadAsStringAsync();
        return (false, err, null);
    }

    public async Task<List<SnapshotCheckpointDto>?> GetSnapshotCheckpointsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<List<SnapshotCheckpointDto>>("/api/admin/compact/checkpoints", JsonOpts);
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

    public async Task<(bool ok, string? error)> ChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/address",
            Body(new { newApiAddress, password }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<(bool ok, string? error)> SetAutoAcceptRestoreAsync(Guid nodeId, bool autoAccept)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/auto-accept-restore",
            Body(new { autoAccept }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<(bool ok, string? error)> SetAutoAcceptDekRotationAsync(Guid nodeId, bool autoAccept)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/auto-accept-dek-rotation",
            Body(new { autoAccept }));
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

    public async Task<(UserDto? User, string? Error, int StatusCode)> CreateUserAsync(string username, string displayName, string password, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = Body(new { username, displayName, password, role })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode)
        {
            var user = await resp.Content.ReadFromJsonAsync<UserDto>(JsonOpts);
            return (user, null, (int)resp.StatusCode);
        }
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (null, err?.Error ?? "Failed to create user", (int)resp.StatusCode);
        }
        catch { return (null, "Failed to create user", (int)resp.StatusCode); }
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> UpdateUserAsync(int id, string displayName, string? role)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}")
        {
            Content = Body(new { displayName, role })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to update user", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to update user", (int)resp.StatusCode); }
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> DeleteUserAsync(int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{id}");
        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to delete user", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to delete user", (int)resp.StatusCode); }
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

    public async Task<(bool Ok, string? Error, int StatusCode)> ChangeUserPasswordAsync(int id, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/users/{id}/change-password")
        {
            Content = Body(new { newPassword })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to change password", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to change password", (int)resp.StatusCode); }
    }

    // ─── Folder Restrictions ────────────────────────────────────────────────

    public async Task<List<AclEntryDto>?> GetUserRestrictionsAsync(int userId)
    {
        try
        {
            var resp = await http.GetAsync($"/api/restrictions/user/{userId}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<AclEntryDto>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<AclEntryDto?> AddUserRestrictionAsync(int userId, Guid folderId, string effect)
    {
        try
        {
            var resp = await http.PostAsync($"/api/restrictions/user/{userId}", Body(new { folderId, effect }));
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<AclEntryDto>(JsonOpts);
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static StringContent Body(object obj) =>
        new(JsonSerializer.Serialize(obj, JsonOpts), Encoding.UTF8, "application/json");

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString();
        }
        catch { }
        return null;
    }
}
