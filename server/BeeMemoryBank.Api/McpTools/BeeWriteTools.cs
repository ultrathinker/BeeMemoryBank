using System.ComponentModel;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeWriteTools(
    ArticleService articleService,
    BeeMemoryBank.Core.Interfaces.IFolderRepository folderRepo,
    FolderService folderSvc,
    ConceptTagService conceptTagSvc,
    ILogger<BeeWriteTools> logger,
    McpResponseManager responseManager)
{
    private const int LargeContentThreshold = 5000;

    [McpServerTool(Name = "bee_save_article")]
    [Description(
        "Create a new article in the knowledge base.\n" +
        "Missing folders in treePath are created automatically — pass '/A/B/C' and A, B, C are created " +
        "as needed. Returns plain-text confirmation with the new article id (not JSON).\n" +
        "IMPORTANT: if content comes from an existing file on disk, DO NOT read it into context. " +
        "Call bee_get_upload_script to get a script that uploads directly from disk (zero context tokens).")]
    public async Task<string> SaveArticle(
        [Description("Article title — short and descriptive.")] string title,
        [Description("Tree path where the article belongs, e.g. '/Work/Dev' or '/Personal'. Missing folders are auto-created.")] string treePath,
        [Description("Article content in Markdown format. Stored encrypted.")] string content,
        [Description("Tags for categorization. Pass as a JSON array of strings: [\"tag1\", \"tag2\"]. NOT a comma-separated string. Optional, omit or pass null if not needed. Duplicates are ignored.")] List<string>? tags = null)
    {
        try
        {
            var mergedTags = tags ?? [];
            var article = await articleService.CreateAsync(title, treePath, [], content);
            if (mergedTags.Count > 0)
                await conceptTagSvc.SetForArticleAsync(article.Id, mergedTags);
            var result = $"Created article {article.Id}: {article.Title} in {article.TreePath}";
            if (content.Length > LargeContentThreshold)
                result += $"\n\n💡 Large content ({content.Length} chars). If this came from a file on disk, " +
                          "use bee_get_upload_script next time for zero-context uploads.";
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: target folder is restricted for this agent.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_update_article")]
    [Description(
        "Update an existing article. Content is fully replaced, not appended — read with bee_get_article, " +
        "modify, then save the full content. Only provided fields are updated; omitted fields are untouched.\n" +
        "Side effect: every call that changes content creates a new version snapshotting the PRIOR content " +
        "(see bee_get_article_versions). For cheaper edits without resending the whole body, use " +
        "bee_append_to_article / bee_prepend_to_article / bee_replace_in_article.\n" +
        "Returns plain-text confirmation (not JSON).")]
    public async Task<string> UpdateArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("New title. Omit (null) to keep current.")] string? title = null,
        [Description("New tree path. Missing folders are auto-created. Omit (null) to keep current.")] string? treePath = null,
        [Description("New full content in Markdown. Replaces the entire previous body. Omit (null) to keep current.")] string? content = null,
        [Description("New tag list. Replaces ALL current tags. Pass as a JSON array of strings: [\"tag1\", \"tag2\"]. NOT a comma-separated string. Omit (null) to keep current tags. Pass empty array [] to clear every tag.")] List<string>? tags = null)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        try
        {
            await articleService.UpdateAsync(id, title, treePath, null, content);
            if (tags != null)
            {
                await conceptTagSvc.SetForArticleAsync(id, tags);
            }
            return $"Updated article {id}";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
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
        try
        {
            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return $"Error: folder '{path}' not found";

            var folderName = path.TrimEnd('/').Split('/').Last();
            var newPath = newParentPath.TrimEnd('/') + "/" + folderName;
            await folderSvc.MoveAsync(folder.Id, newParentPath);
            return $"Moved folder '{path}' → '{newPath}'";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: source or destination folder is restricted for this agent.";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_rename_folder")]
    [Description(
        "Rename a folder. All articles and subfolders keep their relative structure.\n" +
        "Single atomic operation — fast even for folders with hundreds of articles.\n" +
        "Example: bee_rename_folder path=/Work/OldName newName=NewName → folder becomes /Work/NewName.")]
    public async Task<string> RenameFolder(
        [Description("Current full path of the folder to rename, e.g. /Work/OldName")] string path,
        [Description("New name for the folder (just the name, not a full path), e.g. NewName")] string newName)
    {
        try
        {
            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return $"Error: folder '{path}' not found";

            var parentPath = folder.ParentPath ?? "";
            var resolvedNewPath = parentPath.TrimEnd('/') + "/" + newName;

            await folderSvc.RenameAsync(folder.Id, newName);
            return $"Renamed folder '{path}' → '{resolvedNewPath}'";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: folder is restricted for this agent.";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_delete_article")]
    [Description(
        "Soft-delete an article. Two-step for safety: first call returns a warning, pass confirm=true to execute.\n" +
        "After soft-delete the article disappears from bee_list_articles, bee_get_article, bee_search etc. — " +
        "they all return \"not found\". Data is still on disk and can be restored from the Web UI; there is " +
        "currently NO MCP tool to list or restore soft-deleted articles, so use with care.\n" +
        "Returns plain-text confirmation.")]
    public async Task<string> DeleteArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Must be true to actually delete. Default false = dry-run that returns a warning.")] bool confirm = false)
    {
        if (!confirm)
            return $"Warning: This will soft-delete article {id}. Set confirm=true to proceed.";

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        try
        {
            await articleService.DeleteAsync(id);
            return $"Deleted article {id}";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException)
        {
            return $"Error: article {id} not found";
        }
    }

    [McpServerTool(Name = "bee_delete_folder")]
    [Description(
        "Soft-delete an empty folder. Refuses with a count of blockers if the folder still contains any " +
        "articles or subfolders — move/delete them first (use bee_get_tree to inspect contents).\n" +
        "Two-step: pass confirm=true to execute. Soft-deleted folders can be restored from the Web UI; " +
        "there is currently NO MCP tool to list or restore soft-deleted folders.\n" +
        "Returns plain-text confirmation.")]
    public async Task<string> DeleteFolder(
        [Description("Full path of the folder to delete, e.g. '/Work/OldProject'.")] string path,
        [Description("Must be true to actually delete. Default false = dry-run that returns a warning.")] bool confirm = false)
    {
        if (!confirm)
            return $"Warning: This will delete folder '{path}'. Set confirm=true to proceed.";

        try
        {
            var folder = await folderRepo.GetByPathAsync(path);
            if (folder == null)
                return $"Error: folder '{path}' not found";

            var children = await folderRepo.GetChildrenAsync(path);
            var articles = await articleService.ListAsync(path);
            if (children.Count > 0 || articles.Count > 0)
            {
                // Report both counts in one message so the caller sees the full picture,
                // not just the first blocker.
                var parts = new List<string>();
                if (children.Count > 0) parts.Add($"{children.Count} subfolder(s)");
                if (articles.Count > 0) parts.Add($"{articles.Count} article(s)");
                return $"Error: folder '{path}' is not empty: {string.Join(" and ", parts)}. Move or delete them first.";
            }

            await folderRepo.SoftDeleteAsync(folder.Id, DateTime.UtcNow);
            return $"Deleted folder '{path}'";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: folder is restricted for this agent.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_replace_in_article")]
    [Description(
        "Find and replace exact text in a single article, without needing to read its full body first. " +
        "Matching is case-sensitive and substring-based (NOT regex).\n" +
        "Always returns \"Replaced N occurrence(s) ...\" where N ≥ 0; parse by looking for the leading number. " +
        "When N=0 the article is NOT modified (no version created, no updatedAt change).\n" +
        "To undo: call again with search and replace swapped.\n" +
        "For bulk edits across many articles: use bee_search_content to find candidates, then call this for each.")]
    public async Task<string> ReplaceInArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Exact text to find (case-sensitive).")] string search,
        [Description("Text to replace each occurrence with.")] string replace)
    {
        if (string.IsNullOrEmpty(search))
            return "Error: search text cannot be empty.";
        if (search == replace)
            return "Error: search and replace texts are identical.";

        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        try
        {
            var content = await articleService.GetContentAsync(id);
            var count = CountOccurrences(content, search);
            // Unified response: "Replaced N occurrence(s)" for any N >= 0 so MCP clients parse one format.
            if (count == 0)
                return $"Replaced 0 occurrence(s) of \"{Truncate(search, 50)}\" in article {id} ({article.Title}).";

            var newContent = content.Replace(search, replace);
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return $"Replaced {count} occurrence(s) of \"{Truncate(search, 50)}\" → \"{Truncate(replace, 50)}\" in article {id} ({article.Title}).";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException) { return $"Error: article {id} not found"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    [McpServerTool(Name = "bee_append_to_article")]
    [Description(
        "Append text to the end of an article without reading its full content. Saves tokens — no need to " +
        "fetch and resend the whole body. Use for adding sections, log entries, notes. For editing existing " +
        "text use bee_replace_in_article or bee_update_article.\n" +
        "Side effect: creates a new version snapshotting the PRIOR content.\n" +
        "Returns plain-text confirmation with the new article size.")]
    public async Task<string> AppendToArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Text to append at the end. A blank line is inserted between existing content and this text.")] string text)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        try
        {
            var content = await articleService.GetContentAsync(id);
            var newContent = content + "\n\n" + text;
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return responseManager.ProcessResponse($"Appended to article {id}. New size: {newContent.Length} chars.");
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException) { return $"Error: article {id} not found"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "bee_prepend_to_article")]
    [Description(
        "Prepend text to the beginning of an article without reading its full content. Saves tokens — no need " +
        "to fetch and resend the whole body. Use for adding summaries, warnings, headers. For editing existing " +
        "text use bee_replace_in_article or bee_update_article.\n" +
        "Side effect: creates a new version snapshotting the PRIOR content.\n" +
        "Returns plain-text confirmation with the new article size.")]
    public async Task<string> PrependToArticle(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Text to insert at the beginning. A blank line is inserted between this text and the existing content.")] string text)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        try
        {
            var content = await articleService.GetContentAsync(id);
            var newContent = text + "\n\n" + content;
            await articleService.UpdateAsync(id, null, null, null, newContent);
            return responseManager.ProcessResponse($"Prepended to article {id}. New size: {newContent.Length} chars.");
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException) { return $"Error: article {id} not found"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }
}
