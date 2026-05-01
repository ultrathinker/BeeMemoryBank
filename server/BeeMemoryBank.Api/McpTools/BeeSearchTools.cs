using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeSearchTools(
    SearchService searchService,
    McpResponseManager responseManager)
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
        "Full-text search inside encrypted article BODIES (not just titles). SLOW — decrypts and scans all " +
        "articles in batches. Use only when bee_search by title didn't find what you need.\n" +
        "Requires an unlocked session. If the session is locked, silently degrades to title-only search " +
        "(returns fewer results — not an error).\n" +
        "Returns JSON with the same shape as bee_search: { folders: [{ path, name }], articles: [{ id, title, treePath }] }.")]
    public async Task<string> SearchContent(
        [Description("Search keywords to find in article body text, case-insensitive.")] string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return "Error: keywords must be provided";

        var results = await searchService.SearchWithContentAsync(keywords);

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
}
