using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
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

    public async Task<FolderPermissionsDto?> GetFolderPermissionsAsync(string path)
    {
        var resp = await http.GetAsync($"/api/access/folder-permissions?path={Uri.EscapeDataString(path)}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FolderPermissionsDto>(JsonOpts);
    }

    public async Task<string[]> GetReadOnlyPathsAsync()
    {
        var resp = await http.GetAsync("/api/access/readonly-paths");
        if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
        var doc = await resp.Content.ReadFromJsonAsync<ReadOnlyPathsDto>(JsonOpts);
        return doc?.Paths ?? Array.Empty<string>();
    }

    public async Task<(bool ok, int status, string? error)> CopyArticleAsync(Guid articleId, string targetFolderPath)
    {
        var resp = await http.PostAsync($"/api/articles/{articleId}/copy", Body(new { targetFolderPath }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        var err = await ReadErrorAsync(resp);
        return (false, (int)resp.StatusCode, err);
    }

    public async Task<(bool ok, int status, string? error)> CopyFolderAsync(Guid folderId, string targetParentPath)
    {
        var resp = await http.PostAsync($"/api/folders/{folderId}/copy", Body(new { targetParentPath }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        var err = await ReadErrorAsync(resp);
        return (false, (int)resp.StatusCode, err);
    }

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
        string title, string treePath, string content, string? passphrase = null, string? hint = null)
    {
        // passphrase != null → create the article ALREADY protected (body wrapped server-side before
        // the first save, so the plaintext never reaches the event log / sync).
        var resp = await http.PostAsync("/api/articles",
            Body(new { title, treePath, content, passphrase, hint }));
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, await ReadErrorAsync(resp));
        var dto = await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
        return (dto, (int)resp.StatusCode, null);
    }

    // Edit-load helper: returns whether the article is protected and, if it was unlocked in the last
    // ~60s by this user (server-side cache), the decrypted body so the editor opens without a re-prompt.
    public async Task<EditContentDto?> GetEditContentAsync(Guid id)
    {
        var resp = await http.GetAsync($"/api/articles/{id}/edit-content");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<EditContentDto>(JsonOpts);
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

    // ─── Protected ("second-layer") articles ───────────────────────────────────

    public async Task<(bool ok, int status, string? content, string? error)> UnlockArticleAsync(Guid id, string passphrase)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/unlock", Body(new { passphrase }));
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<ArticleContentDto>(JsonOpts);
            return (true, (int)resp.StatusCode, dto?.Content, null);
        }
        return (false, (int)resp.StatusCode, null, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> ProtectArticleAsync(Guid id, string passphrase, string? hint)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/protect", Body(new { passphrase, hint }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<(bool ok, int status, string? error)> UnprotectArticleAsync(Guid id, string passphrase)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/unprotect", Body(new { passphrase }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    public async Task<bool> RelockArticleAsync(Guid id)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/relock", Body(new { }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<(bool ok, int status, string? error)> ChangeArticlePassphraseAsync(Guid id, string oldPassphrase, string newPassphrase, string? hint)
    {
        var resp = await http.PostAsync($"/api/articles/{id}/change-passphrase", Body(new { oldPassphrase, newPassphrase, hint }));
        if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode, null);
        return (false, (int)resp.StatusCode, await ReadErrorAsync(resp));
    }

    // Edit of a protected article: PUT carries the passphrase so the server re-wraps the new body.
    public async Task<(ArticleDto? Article, int Status, string? Error)> UpdateProtectedArticleAsync(
        Guid id, string? title, string? treePath, string content, string passphrase)
    {
        var resp = await http.PutAsync($"/api/articles/{id}",
            Body(new { title, treePath, content, passphrase }));
        if (!resp.IsSuccessStatusCode)
            return (null, (int)resp.StatusCode, await ReadErrorAsync(resp));
        var dto = await resp.Content.ReadFromJsonAsync<ArticleDto>(JsonOpts);
        return (dto, (int)resp.StatusCode, null);
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
}
