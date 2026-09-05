using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
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

    public async Task<SearchResponseDto?> SearchAsync(string query, bool content = false, int page = 1, int pageSize = 50) =>
        await http.GetFromJsonAsync<SearchResponseDto>(
            $"/api/search?q={Uri.EscapeDataString(query)}&content={content}&page={page}&pageSize={pageSize}", JsonOpts);

    /// <summary>
    /// WP-16: ranked article results from <c>/api/search/hybrid</c> (RRF-combined BM25 keyword +
    /// chunk-based semantic ranking). Returns null on any transport error or non-2xx (locked
    /// session, semantic search not yet initialized for this vault, API down) so callers can fall
    /// back to the older content-search path rather than surfacing a raw failure.
    /// </summary>
    public async Task<List<ArticleDto>?> SearchHybridArticlesAsync(string query, string mode = "hybrid", int topK = 20)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/api/search/hybrid", new { query, mode, topK });
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<ArticleDto>>(JsonOpts);
        }
        catch
        {
            return null;
        }
    }

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

    public async Task<JsonElement?> GetConceptGraphSearchAsync(string q, int depth, int maxNodes, string? treePath = null)
    {
        try
        {
            var url = "/api/concept-tags/graph/search?q=" + Uri.EscapeDataString(q)
                + "&depth=" + depth + "&maxNodes=" + maxNodes;
            if (!string.IsNullOrEmpty(treePath))
                url += "&treePath=" + Uri.EscapeDataString(treePath);
            return await http.GetFromJsonAsync<JsonElement>(url, JsonOpts);
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
}
