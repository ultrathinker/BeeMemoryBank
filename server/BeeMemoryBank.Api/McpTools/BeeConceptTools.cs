using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeConceptTools(
    ConceptTagService conceptTagService,
    ArticleService articleService,
    IHttpContextAccessor httpContextAccessor,
    McpResponseManager responseManager,
    SessionService session)
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
        "Get a short list of articles related to the given article: those that share RARE tags with it " +
        "(a tag carried by many articles links nothing and is ignored) or that are close to it by meaning.\n" +
        "Returns JSON array sorted by strength DESC: [{ id, title, treePath, sharedTags, strength, reasons, possibleDuplicate }]. " +
        "'strength' is 0-100 (higher = more related). 'reasons' says why the article is listed, e.g. " +
        "[\"shared tag: rust\", \"similar meaning: 87%\"]; 'sharedTags' is the rare tag names both articles have " +
        "(at most 3, rarest first); 'possibleDuplicate' is true when the two are so similar they may be copies of one another. " +
        "Only articles the caller may read are listed. While the vault is locked the list is made of shared tags " +
        "alone (nearness by meaning is skipped, as in bee_search_content). Returns [] if nothing is related.")]
    public async Task<string> GetRelated(
        [Description("Article ID (GUID).")] Guid id,
        [Description("Max number of related articles to return. Range 1–20. Default: 7.")] int limit = 7)
    {
        var article = await articleService.GetMetadataAsync(id);
        if (article == null)
            return $"Error: article {id} not found";

        // The article vectors are derived from article bodies: while locked, tags only.
        var related = await conceptTagService.GetRelatedArticlesAsync(id, RelatedArticle.ClampLimit(limit), includeMeaning: session.IsUnlocked);
        var json = JsonSerializer.Serialize(related.Select(r => new
        {
            id = r.Id,
            title = r.Title,
            treePath = r.TreePath,
            sharedTags = r.SharedConcepts,
            strength = r.Strength,
            reasons = r.Reasons,
            possibleDuplicate = r.PossibleDuplicate
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
        [Description("Max results to return. Range 1–500. Default: 100.")] int limit = 100,
        [Description("Number of matching tags to skip before the returned page (for pagination). Default: 0.")] int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var concepts = await conceptTagService.ListAsync(filter, limit, offset);
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
        "Adding at least one new tag is an edit of the article like any other: it marks the article as " +
        "changed (updatedAt, version) and reaches other nodes. Adding nothing new changes nothing. " +
        "Refused in a read-only folder.\n" +
        "The returned count reflects only NEWLY added tags: duplicates in the input and tags " +
        "already on the article are not counted. If everything was already present, returns 0. " +
        "Tags new to the vocabulary get embeddings generated automatically for future semantic search.\n" +
        "Returns plain-text confirmation (not JSON), e.g. \"Added 2 tag(s) to article 'Foo'.\"")]
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
    public async Task<string> AddTags(
        [Description("Article ID (GUID).")] Guid id,
        [Description("List of tag names to add. Pass as a JSON array of strings: [\"tag1\", \"tag2\"]. NOT a comma-separated string. Duplicates and tags already on the article are ignored silently.")] List<string> tags)
    {
        try
        {
            var article = await articleService.GetMetadataAsync(id);
            if (article == null)
                return $"Error: article {id} not found";

            // Through ArticleService, never a bare tag-table write: the service reads the tag set, merges and
            // writes it back under the article's write lock, as an article_update that carries the whole set,
            // and counts what was really new from that same read. A bare write stores the tag on this node
            // only, and the next article_update from any other node wipes it.
            var added = await articleService.AddTagsAsync(id, tags);
            return $"Added {added} tag(s) to article '{article.Title}'.";
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? $"Access denied: folder '{path}' is read-only for your user. Use bee_copy_to to copy the content into a writable folder."
                : "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException)
        {
            return $"Error: article {id} not found";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "bee_remove_tag")]
    [Description(
        "Remove a specific tag from an article. Does not affect other articles or the global vocabulary.\n" +
        "A real removal is an edit of the article like any other: it marks the article as changed " +
        "(updatedAt, version) and reaches other nodes. Refused in a read-only folder.\n" +
        "Idempotent: if the tag was not on the article, returns \"...was not on article '...' (no-op)\" " +
        "instead of \"Removed tag '...' from article '...'\" and changes nothing. Use this to distinguish " +
        "a real removal from a typo/no-op.\n" +
        "Returns plain-text confirmation (not JSON).")]
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
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
            // The service decides it from the same read it writes from, under the article's write lock.
            var removed = await articleService.RemoveTagAsync(id, tag);
            return removed
                ? $"Removed tag '{tag}' from article '{article.Title}'."
                : $"Tag '{tag}' was not on article '{article.Title}' (no-op).";
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteAclDenial.TryClassify(ex, out var kind, out var path);
            return kind == WriteAclDenialKind.ReadOnly
                ? $"Access denied: folder '{path}' is read-only for your user. Use bee_copy_to to copy the content into a writable folder."
                : "Access denied: article is in a restricted folder for this agent.";
        }
        catch (KeyNotFoundException)
        {
            return $"Error: article {id} not found";
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
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
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
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
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
    [BeeMemoryBank.Api.Helpers.RequiresUnlockedSession]
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
