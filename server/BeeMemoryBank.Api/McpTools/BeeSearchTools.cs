using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeSearchTools(
    SearchService searchService,
    HybridSearchService hybridSearchService,
    McpResponseManager responseManager,
    SessionService session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerTool(Name = "bee_search")]
    [Description(
        "Search both articles (by title) AND folders (by name/path), case-insensitive. Fast metadata search.\n" +
        "Returns { folders: [...], articles: [...] } — folders are included by design so you can locate " +
        "a folder without knowing its full path. Use this first; if you need to match body text, use bee_search_content.")]
    public async Task<string> Search(
        [Description("Search keywords, case-insensitive. Matches against article titles and folder names/paths.")] string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return "Error: keywords must be provided";

        var results = await searchService.SearchAsync(keywords);

        var json = JsonSerializer.Serialize(new
        {
            folders = results.Folders.Select(f => new { path = f.Path, name = f.Name }),
            articles = results.Articles.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                treePath = a.TreePath
            })
        }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_search_content")]
    [Description(
        "Ranked search inside article body content, plus title/folder matches for the same keywords " +
        "(merged in, same as bee_search). mode: 'hybrid' (default — used when the parameter is omitted) " +
        "combines exact-term matching (BM25) with meaning-based similarity via reciprocal rank fusion; " +
        "'keyword' is exact-term matching only; 'semantic' is meaning-based only (needs embeddings to have " +
        "been generated on this node). Passing an unrecognized mode value is an error — it does not " +
        "silently fall back to hybrid the way omitting it does.\n" +
        "If the vault is locked, or ranked/semantic search is unavailable on this node, degrades to a plain " +
        "title/metadata-only search rather than failing outright — the 'notice' field explains when this " +
        "happened; a null 'notice' means the requested mode ran normally.\n" +
        "Returns JSON: { folders: [{ path, name }], articles: [{ id, title, treePath }], notice }.")]
    public async Task<string> SearchContent(
        [Description("Search keywords to find in article body text, case-insensitive.")] string keywords,
        [Description("Search mode: 'hybrid' (default), 'keyword', or 'semantic'. Omit for hybrid; an unrecognized value is an error.")]
        string? mode = null)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return "Error: keywords must be provided";

        var parsedMode = SearchMode.Hybrid;
        if (!string.IsNullOrEmpty(mode) && !Enum.TryParse(mode, ignoreCase: true, out parsedMode))
            return $"Error: invalid mode '{mode}'. Valid values: hybrid, keyword, semantic.";

        var titleMatches = await searchService.SearchAsync(keywords);

        string? notice = null;
        List<Article> ranked;
        if (!session.IsUnlocked)
        {
            ranked = [];
            notice = "Vault is locked — searched titles/metadata only (body search skipped).";
        }
        else
        {
            try
            {
                ranked = await hybridSearchService.SearchAsync(keywords, parsedMode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ModelUnavailableException)
            {
                // Mirrors HybridSearchService.HybridAsync's own internal degrade for a missing/unavailable
                // embedding pipeline -- but that internal catch only covers Hybrid mode (Semantic-only mode
                // has no keyword component to fall back to internally), so this outer catch is what keeps a
                // Semantic-mode call from failing outright when this node never generates embeddings.
                ranked = [];
                notice = parsedMode == SearchMode.Semantic
                    ? "Meaning-based search is unavailable on this node — searched titles/metadata only."
                    : "Ranked content search is unavailable — searched titles/metadata only.";
            }
        }

        var seenIds = new HashSet<Guid>(ranked.Select(a => a.Id));
        var merged = new List<Article>(ranked);
        foreach (var a in titleMatches.Articles)
        {
            if (seenIds.Add(a.Id)) merged.Add(a);
        }

        var json = JsonSerializer.Serialize(new
        {
            folders = titleMatches.Folders.Select(f => new { path = f.Path, name = f.Name }),
            articles = merged.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                treePath = a.TreePath
            }),
            notice
        }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }
}
