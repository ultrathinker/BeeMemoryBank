using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeConceptTools(
    ConceptTagService conceptTagService,
    ArticleService articleService,
    IHttpContextAccessor httpContextAccessor,
    McpResponseManager responseManager)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private CallerIdentity GetCaller()
    {
        var ctx = httpContextAccessor.HttpContext;
        return ctx != null ? CallerIdentity.Extract(ctx) : new CallerIdentity(null, null, null, false);
    }

    [McpServerTool(Name = "bee_get_related")]
    [Description(
        "Get articles related to the given article via shared tags.\n" +
        "Returns JSON array sorted by strength DESC: [{ id, title, treePath, sharedTags, strength }]. " +
        "'sharedTags' is the list of tag names both articles have in common; 'strength' is the count " +
        "of those shared tags (higher = more related). Returns [] if nothing is related.")]
    public async Task<string> GetRelated(
        [Description("Article ID (GUID).")] Guid id)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        var related = await conceptTagService.GetRelatedArticlesAsync(id);
        var json = JsonSerializer.Serialize(related.Select(r => new
        {
            id = r.Id,
            title = r.Title,
            treePath = r.TreePath,
            sharedTags = r.SharedConcepts,
            strength = r.Strength
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_search_by_tag")]
    [Description(
        "Find all articles that have the given tag. Exact tag-name match (case-insensitive).\n" +
        "Returns JSON array: [{ id, title, treePath }]. Returns [] if no article uses the tag " +
        "or the tag does not exist in the vocabulary. For fuzzy/semantic lookup of tag names " +
        "first, use bee_list_tags with filter '~query'.")]
    public async Task<string> SearchByTag(
        [Description("Exact tag name to search for (case-insensitive).")] string tag)
    {
        var articles = await conceptTagService.SearchByConceptAsync(tag);
        var json = JsonSerializer.Serialize(articles.Select(a => new
        {
            id = a.Id,
            title = a.Title,
            treePath = a.TreePath
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_list_tags")]
    [Description(
        "List tags from the global tag vocabulary. Use this to discover existing tags before adding new ones.\n" +
        "Filter modes:\n" +
        "  - omitted → top tags by article count\n" +
        "  - 'auto'  → substring match (matches 'automation', 'автономность', etc.), case-insensitive\n" +
        "  - '~resilience' → semantic/embedding search (finds similar meanings, not just substrings)\n" +
        "Returns JSON array: [{ name, articleCount }]. articleCount=0 means the tag has no live " +
        "articles — usually all tagged articles were soft-deleted.")]
    public async Task<string> ListTags(
        [Description("Optional filter. Plain text = case-insensitive substring match. '~word' = semantic search.")] string? filter = null,
        [Description("Max results to return. Range 1–500. Default: 100.")] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var concepts = await conceptTagService.ListAsync(filter, limit);
        var json = JsonSerializer.Serialize(concepts.Select(c => new
        {
            name = c.Name,
            articleCount = c.ArticleCount
        }), JsonOpts);
        return responseManager.ProcessResponse(json);
    }

    [McpServerTool(Name = "bee_add_tags")]
    [Description(
        "Add tags to an article. Additive — does not remove existing tags.\n" +
        "The returned count reflects only NEWLY added tags: duplicates in the input and tags " +
        "already on the article are not counted. If everything was already present, returns 0. " +
        "Tags new to the vocabulary get embeddings generated automatically for future semantic search.\n" +
        "Returns plain-text confirmation (not JSON), e.g. \"Added 2 tag(s) to article 'Foo'.\"")]
    public async Task<string> AddTags(
        [Description("Article ID (GUID).")] Guid id,
        [Description("List of tag names to add. Pass as a JSON array of strings: [\"tag1\", \"tag2\"]. NOT a comma-separated string. Duplicates and tags already on the article are ignored silently.")] List<string> tags)
    {
        try
        {
            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return $"Error: article {id} not found";

            // Counter must reflect actual additions, not input size:
            // dedupe the input (case-insensitive) and subtract what the article already has.
            var existing = await conceptTagService.GetByArticleIdAsync(id);
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var newCount = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(t => !existingSet.Contains(t));

            await conceptTagService.AddToArticleAsync(id, tags);
            return $"Added {newCount} tag(s) to article '{article.Title}'.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_remove_tag")]
    [Description(
        "Remove a specific tag from an article. Does not affect other articles or the global vocabulary.\n" +
        "Idempotent: if the tag was not on the article, returns \"...was not on article '...' (no-op)\" " +
        "instead of \"Removed tag '...' from article '...'\". Use this to distinguish a real removal " +
        "from a typo/no-op.\n" +
        "Returns plain-text confirmation (not JSON).")]
    public async Task<string> RemoveTag(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Tag name to remove (case-insensitive match).")] string tag)
    {
        try
        {
            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return $"Error: article {id} not found";

            // Honest response: report no-op distinctly so a typo in tag name is visible to the caller.
            var existing = await conceptTagService.GetByArticleIdAsync(id);
            var wasPresent = existing.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

            await conceptTagService.RemoveFromArticleAsync(id, tag);
            return wasPresent
                ? $"Removed tag '{tag}' from article '{article.Title}'."
                : $"Tag '{tag}' was not on article '{article.Title}' (no-op).";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied: article is in a restricted folder for this agent.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_rename_tag")]
    [Description(
        "Rename a tag globally. Affects every article using it. Returns error if the new name already exists " +
        "(use bee_merge_tags instead to consolidate). Requires superadmin — regular agents get an error.\n" +
        "Returns plain-text confirmation.")]
    public async Task<string> RenameTag(
        [Description("Current tag name.")] string name,
        [Description("New name for the tag. Must not already exist in the vocabulary.")] string newName)
    {
        if (!GetCaller().IsSuperadmin)
            return "Error: renaming tags is restricted to superadmin. This operation affects all users globally.";

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newName))
            return "Error: name and newName must not be empty.";

        try
        {
            await conceptTagService.RenameAsync(name, newName);
            return $"Renamed tag '{name}' to '{newName}'.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_merge_tags")]
    [Description(
        "Merge two tags: every article using 'source' is updated to use 'target' instead, then 'source' is " +
        "deleted from the vocabulary. Use this to consolidate duplicates or near-duplicates " +
        "(e.g. 'ml' + 'machine-learning' → 'machine-learning'). Requires superadmin.\n" +
        "Returns plain-text confirmation.")]
    public async Task<string> MergeTags(
        [Description("Source tag to merge (will be deleted from the vocabulary).")] string source,
        [Description("Target tag to merge into (will be kept).")] string target)
    {
        if (!GetCaller().IsSuperadmin)
            return "Error: merging tags is restricted to superadmin. This operation affects all users globally.";

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return "Error: source and target must not be empty.";

        try
        {
            await conceptTagService.MergeAsync(source, target);
            return $"Merged tag '{source}' into '{target}'. '{source}' has been deleted.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_delete_tag")]
    [Description(
        "Delete a tag from the global vocabulary. Removes it from every article that had it. " +
        "Requires superadmin. Cannot be undone — use bee_rename_tag or bee_merge_tags first if you want " +
        "to preserve article-tag associations.\n" +
        "Returns plain-text confirmation.")]
    public async Task<string> DeleteTag(
        [Description("Tag name to delete globally.")] string name)
    {
        if (!GetCaller().IsSuperadmin)
            return "Error: deleting tags is restricted to superadmin. This operation affects all users globally.";

        if (string.IsNullOrWhiteSpace(name))
            return "Error: name must not be empty.";

        try
        {
            await conceptTagService.DeleteAsync(name);
            return $"Deleted tag '{name}' from the global vocabulary.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

}
