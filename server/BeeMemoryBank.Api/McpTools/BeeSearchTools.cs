using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeSearchTools(
    SearchService searchService,
    FolderAccessService folderAccess,
    IHttpContextAccessor httpContextAccessor,
    McpResponseManager responseManager)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private int? GetAgentId()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
            return agent.Id;
        return null;
    }

    [McpServerTool(Name = "bee_search")]
    [Description(
        "Search articles and folders by title/name and tags. Fast metadata search.\n" +
        "Use this first. If you can't find what you need, try bee_search_content.")]
    public async Task<string> Search(
        [Description("Search keywords, case-insensitive.")] string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return "Error: keywords must be provided";

        var results = await searchService.SearchAsync(keywords);

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        var filteredArticles = FolderAccessService.FilterArticles(results.Articles, restricted);
        var filteredFolders = FolderAccessService.FilterFolders(results.Folders, restricted);

        var json = JsonSerializer.Serialize(new
        {
            folders = filteredFolders.Select(f => new { path = f.Path, name = f.Name }),
            articles = filteredArticles.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                treePath = a.TreePath,
                tags = a.Tags
            })
        }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_search_content")]
    [Description(
        "Search inside encrypted article body content. SLOW — decrypts and scans all articles in batches.\n" +
        "Only use when bee_search didn't find what you need.\n" +
        "Requires an unlocked session. If locked, falls back to title/tags only.")]
    public async Task<string> SearchContent(
        [Description("Search keywords to find in article body text, case-insensitive.")] string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return "Error: keywords must be provided";

        var results = await searchService.SearchWithContentAsync(keywords);

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        var filteredArticles = FolderAccessService.FilterArticles(results.Articles, restricted);
        var filteredFolders = FolderAccessService.FilterFolders(results.Folders, restricted);

        var json = JsonSerializer.Serialize(new
        {
            folders = filteredFolders.Select(f => new { path = f.Path, name = f.Name }),
            articles = filteredArticles.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                treePath = a.TreePath,
                tags = a.Tags
            })
        }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }
}
