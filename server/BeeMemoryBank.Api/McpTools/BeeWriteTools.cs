using System.ComponentModel;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeWriteTools(
    ArticleService articleService,
    BeeMemoryBank.Core.Interfaces.IFolderRepository folderRepo,
    FolderService folderSvc,
    FolderAccessService folderAccess,
    IHttpContextAccessor httpContextAccessor,
    McpResponseManager responseManager)
{
    private const int LargeContentThreshold = 5000;

    private int? GetAgentId()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
            return agent.Id;
        return null;
    }

    private async Task<HashSet<string>> GetRestrictedPathsAsync()
    {
        var agentId = GetAgentId();
        return await folderAccess.GetRestrictedPathsAsync(null, agentId);
    }

    [McpServerTool(Name = "bee_save_article")]
    [Description(
        "Create a new article in the knowledge base.\n" +
        "IMPORTANT: If content comes from an existing file on disk, DO NOT read it into context.\n" +
        "Call bee_get_upload_script to get a script that uploads directly from disk (zero context tokens).")]
    public async Task<string> SaveArticle(
        [Description("Article title — short and descriptive.")] string title,
        [Description("Tree path where the article belongs, e.g. /Work/Dev or /Personal.")] string treePath,
        [Description("Article content in Markdown format.")] string content,
        [Description("Tags for categorization. Optional, omit if not needed.")] List<string>? tags = null)
    {
        var restricted = await GetRestrictedPathsAsync();
        if (FolderAccessService.IsPathRestricted(restricted, treePath))
            return "Access denied: target folder is restricted for this agent.";

        try
        {
            var tagList = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? [];
            var article = await articleService.CreateAsync(title, treePath, tagList, content);
            var result = $"Created article {article.Id}: {article.Title} in {article.TreePath}";
            if (content.Length > LargeContentThreshold)
                result += $"\n\n💡 Large content ({content.Length} chars). If this came from a file on disk, " +
                          "use bee_get_upload_script next time for zero-context uploads.";
            return result;
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_update_article")]
    [Description(
        "Update an existing article. Content is fully replaced, not appended.\n" +
        "Read the article with bee_get_article first, modify, then save the full content.\n" +
        "Only provided fields are updated. Omitted fields remain unchanged.")]
    public async Task<string> UpdateArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("New title. Omit to keep current.")] string? title = null,
        [Description("New tree path. Omit to keep current.")] string? treePath = null,
        [Description("New full content in Markdown. Replaces entire previous content. Omit to keep current.")] string? content = null,
        [Description("New tags. Replaces all current tags. Omit (null) to keep current. Pass empty array [] to clear all tags.")] List<string>? tags = null)
    {
        var restricted = await GetRestrictedPathsAsync();

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return "Access denied: article is in a restricted folder for this agent.";

        if (treePath != null && FolderAccessService.IsPathRestricted(restricted, treePath))
            return "Access denied: target folder is restricted for this agent.";

        try
        {
            var tagList = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            await articleService.UpdateAsync(id, title, treePath, tagList, content);
            return $"Updated article {id}";
        }
        catch (KeyNotFoundException)
        {
            return $"Error: article {id} not found";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_move_folder")]
    [Description(
        "Move a folder (and all its articles) to a new parent folder.\n" +
        "Single SQL operation — fast even for folders with hundreds of articles.\n" +
        "Example: bee_move_folder path=/Work/Docs newParentPath=/Archive → folder becomes /Archive/Docs.\n" +
        "The folder name stays the same, only the parent changes.")]
    public async Task<string> MoveFolder(
        [Description("Current full path of the folder to move, e.g. /Work/Docs")] string path,
        [Description("Full path of the destination parent folder, e.g. /Archive")] string newParentPath)
    {
        var restricted = await GetRestrictedPathsAsync();

        if (FolderAccessService.IsPathRestricted(restricted, path))
            return "Access denied: source folder is restricted for this agent.";

        try
        {
            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return $"Error: folder '{path}' not found";

            var folderName = path.TrimEnd('/').Split('/').Last();
            var resolvedNewPath = newParentPath.TrimEnd('/') + "/" + folderName;

            if (FolderAccessService.IsPathRestricted(restricted, newParentPath) ||
                FolderAccessService.IsPathRestricted(restricted, resolvedNewPath))
                return "Access denied: destination folder is restricted for this agent.";
            var newPath = newParentPath.TrimEnd('/') + "/" + folderName;
            await folderSvc.MoveAsync(folder.Id, newParentPath);
            return $"Moved folder '{path}' → '{newPath}'";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_delete_article")]
    [Description(
        "Soft-delete an article. Set confirm=true to execute.")]
    public async Task<string> DeleteArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Must be true to confirm deletion.")] bool confirm = false)
    {
        if (!confirm)
            return $"Warning: This will soft-delete article {id}. Set confirm=true to proceed.";

        var restricted = await GetRestrictedPathsAsync();

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return "Access denied: article is in a restricted folder for this agent.";

        try
        {
            await articleService.DeleteAsync(id);
            return $"Deleted article {id}";
        }
        catch (KeyNotFoundException)
        {
            return $"Error: article {id} not found";
        }
    }

    [McpServerTool(Name = "bee_delete_folder")]
    [Description(
        "Delete an empty folder. Refuses if folder contains articles or subfolders.\n" +
        "Set confirm=true to execute. Use bee_get_tree to check contents first.")]
    public async Task<string> DeleteFolder(
        [Description("Full path of the folder to delete, e.g. /Work/OldProject")] string path,
        [Description("Must be true to confirm deletion.")] bool confirm = false)
    {
        if (!confirm)
            return $"Warning: This will delete folder '{path}'. Set confirm=true to proceed.";

        var restricted = await GetRestrictedPathsAsync();

        if (FolderAccessService.IsPathRestricted(restricted, path))
            return "Access denied: folder is restricted for this agent.";

        try
        {
            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return $"Error: folder '{path}' not found";

            var children = await folderRepo.GetChildrenAsync(path);
            if (children.Count > 0)
                return $"Error: folder '{path}' has {children.Count} subfolder(s). Delete or move them first.";

            var articles = await articleService.ListAsync(path);
            if (articles.Count > 0)
                return $"Error: folder '{path}' has {articles.Count} article(s). Delete or move them first.";

            await folderRepo.SoftDeleteAsync(folder.Id, DateTime.UtcNow);
            return $"Deleted folder '{path}'";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_append_to_article")]
    [Description(
        "Append text to the end of an article without reading its full content.\n" +
        "Saves tokens — no need to fetch and resend the entire article.\n" +
        "Use for adding sections, log entries, notes. For editing existing text use bee_update_article.")]
    public async Task<string> AppendToArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Text to append at the end. Separated from existing content by a blank line.")] string text)
    {
        var restricted = await GetRestrictedPathsAsync();

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return "Access denied: article is in a restricted folder for this agent.";

        try
        {
            var content = await articleService.GetContentAsync(id);
            var newContent = content + "\n\n" + text;
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return responseManager.ProcessResponse($"Appended to article {id}. New size: {newContent.Length} chars.");
        }
        catch (KeyNotFoundException) { return $"Error: article {id} not found"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "bee_prepend_to_article")]
    [Description(
        "Prepend text to the beginning of an article without reading its full content.\n" +
        "Saves tokens — no need to fetch and resend the entire article.\n" +
        "Use for adding summaries, warnings, headers. For editing existing text use bee_update_article.")]
    public async Task<string> PrependToArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Text to insert at the beginning. Separated from existing content by a blank line.")] string text)
    {
        var restricted = await GetRestrictedPathsAsync();

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        if (FolderAccessService.IsPathRestricted(restricted, article.TreePath))
            return "Access denied: article is in a restricted folder for this agent.";

        try
        {
            var content = await articleService.GetContentAsync(id);
            var newContent = text + "\n\n" + content;
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return responseManager.ProcessResponse($"Prepended to article {id}. New size: {newContent.Length} chars.");
        }
        catch (KeyNotFoundException) { return $"Error: article {id} not found"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }
}
