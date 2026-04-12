using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeReadTools(
    ArticleService articleService,
    IArticleVersionRepository versionRepo,
    BeeMemoryBank.Core.Interfaces.IFolderRepository folderRepo,
    FolderAccessService folderAccess,
    SessionService session,
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

    [McpServerTool(Name = "bee_list_articles")]
    [Description("List articles, optionally filtered by tree path.")]
    public async Task<string> ListArticles(
        [Description("Tree path filter. Omit to list all articles.")] string? treePath = null)
    {
        var articles = await articleService.ListAsync(treePath);

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        articles = FolderAccessService.FilterArticles(articles, restricted);

        var json = JsonSerializer.Serialize(articles.Select(a => new
        {
            id = a.Id,
            title = a.Title,
            treePath = a.TreePath,
            tags = a.Tags,
            status = a.Status,
            createdAt = a.CreatedAt,
            updatedAt = a.UpdatedAt
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article")]
    [Description(
        "Get article metadata and optionally its decrypted content.\n" +
        "Set content=true to include the article body.")]
    public async Task<string> GetArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Include decrypted article body. Default: false.")] bool content = false)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return $"Error: article {id} not found or access denied";

        if (content)
        {
            try
            {
                var plaintext = await articleService.GetContentAsync(id);
                var json = JsonSerializer.Serialize(new
                {
                    id = article.Id,
                    title = article.Title,
                    treePath = article.TreePath,
                    tags = article.Tags,
                    content = plaintext,
                    createdAt = article.CreatedAt,
                    updatedAt = article.UpdatedAt
                }, JsonOpts);
                return responseManager.ProcessResponse(json);
            }
            catch (InvalidOperationException ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        return responseManager.ProcessResponse(JsonSerializer.Serialize(new
        {
            id = article.Id,
            title = article.Title,
            treePath = article.TreePath,
            tags = article.Tags,
            createdAt = article.CreatedAt,
            updatedAt = article.UpdatedAt
        }, JsonOpts));
    }

    [McpServerTool(Name = "bee_get_tree")]
    [Description("Get folder/article tree structure. Returns paths with their articles, including empty folders.")]
    public async Task<string> GetTree(
        [Description("Path filter to show only a subtree. Omit to show everything.")] string? path = null)
    {
        var articles = await articleService.ListAsync(path);
        var folders = await folderRepo.GetAllActiveAsync();

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        articles = FolderAccessService.FilterArticles(articles, restricted);
        folders = FolderAccessService.FilterFolders(folders, restricted);

        var articlesByPath = articles
            .GroupBy(a => a.TreePath)
            .ToDictionary(g => g.Key, g => g.Select(a => new { id = a.Id, title = a.Title }).ToList());

        var allPaths = new HashSet<string>(folders.Select(f => f.Path));
        foreach (var a in articles)
            allPaths.Add(a.TreePath);

        var filteredPaths = path != null
            ? allPaths.Where(p => p == path || p.StartsWith(path.TrimEnd('/') + "/"))
            : allPaths;

        // Remove restricted paths that may have been added by articles in non-restricted sub-paths
        filteredPaths = filteredPaths.Where(p => !FolderAccessService.IsPathRestricted(restricted, p));

        var emptyList = new List<object>();
        var byPath = filteredPaths
            .OrderBy(p => p)
            .Select(p => new
            {
                path = p,
                articles = articlesByPath.TryGetValue(p, out var arts)
                    ? arts.Select(a => (object)a).ToList()
                    : emptyList
            });

        var json = JsonSerializer.Serialize(new { paths = byPath }, JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article_versions")]
    [Description("List version history for an article. Returns metadata only (no content).")]
    public async Task<string> GetArticleVersions(
        [Description("Article ID (GUID).")] Guid id)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return $"Error: article {id} not found or access denied";

        var versions = await versionRepo.GetByArticleIdAsync(id);
        var json = JsonSerializer.Serialize(versions.Select(v => new
        {
            id = v.Id,
            versionNumber = v.VersionNumber,
            title = v.Title,
            tags = v.Tags,
            treePath = v.TreePath,
            createdAt = v.CreatedAt,
            updatedBy = v.UpdatedBy
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_get_article_version")]
    [Description("Get decrypted content of a specific article version. Requires unlocked session.")]
    public async Task<string> GetArticleVersion(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Version number.")] int versionNumber)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var agentId = GetAgentId();
        var restricted = await folderAccess.GetRestrictedPathsAsync(null, agentId);
        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return $"Error: article {id} not found or access denied";

        var version = await versionRepo.GetAsync(id, versionNumber);
        if (version == null)
            return $"Error: version {versionNumber} not found for article {id}";

        if (!session.IsUnlocked)
            return "Error: session is locked. Unlock first.";

        var masterDek = session.GetMasterDek();
        try
        {
            var articleDek = DekManager.UnwrapDek(version.EncryptedDek, version.DekIV, masterDek);
            var content = ArticleEncryptor.Decrypt(version.Ciphertext, version.IV, articleDek);
            Array.Clear(articleDek);

            var json = JsonSerializer.Serialize(new
            {
                id = version.Id,
                versionNumber = version.VersionNumber,
                title = version.Title,
                tags = version.Tags,
                treePath = version.TreePath,
                content,
                createdAt = version.CreatedAt,
                updatedBy = version.UpdatedBy
            }, JsonOpts);
            return responseManager.ProcessResponse(json);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }
}
