using System.Text.Json;

namespace BeeMemoryBank.Api.Services;

public sealed partial class ChatToolDispatcher
{
    private static List<Models.ChatToolDefinition> BuildToolDefinitions()
    {
        return
        [
            Tool("bee_search", "Search both articles (by title) AND folders (by name/path), case-insensitive. Fast metadata search. Returns { folders, articles }. Use this first; use bee_search_content only for body-text matches.",
                P("keywords", "string", "Search keywords (article titles + folder names/paths).", required: true)),
            Tool("bee_list_articles", "List articles, optionally filtered by tree path. Returns [{ id, title, treePath, status, createdAt, updatedAt }]. Omit treePath to list everything.",
                P("treePath", "string", "Optional tree path filter, e.g. '/Work'. Omit to list all.")),
            Tool("bee_get_tree", "Get the folder/article tree including empty folders. Returns { paths: [{ path, isSystem, isRemote, articles: [{ id, title }] }] }. Optional 'path' scopes to one subtree.",
                P("path", "string", "Optional subtree filter, e.g. '/Work'. Omit for the whole tree.")),
            Tool("bee_get_article", "Get an article. Returns metadata (id, title, treePath, tags, relatedCount, relatedStrength, createdAt, updatedAt) and, when content=true (default), the decrypted body. Content is withheld when the vault is locked, the article is password-protected, or the caller lacks folder access — each reported as a structured field, not an error.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("content", "boolean", "Include decrypted body. Default true.")),
            Tool("bee_search_content", "Ranked search inside article BODIES, plus title/folder matches merged in (same as bee_search). mode: 'hybrid' (default) combines exact-term and meaning-based matching; 'keyword' is exact-term only; 'semantic' is meaning-based only (needs embeddings generated on this node). An unrecognized mode value is an error. Degrades to title-only search (with a 'notice' explaining why) when the vault is locked or ranked/semantic search is unavailable.",
                P("keywords", "string", "Keywords to find in article body text.", required: true),
                P("mode", "string", "Search mode: 'hybrid' (default), 'keyword', or 'semantic'. Omit for hybrid.")),
            // ── Phase 3 write tools (confirm-gated: the user is shown an Allow/Deny card before any of these run) ──
            Tool("bee_save_article", "Create a new article (title, treePath, content, optional tags). Folders in treePath are auto-created. The user will be asked to APPROVE this before it runs.",
                P("title", "string", "Article title.", required: true),
                P("treePath", "string", "Tree path, e.g. '/Work/Dev'. Must start with '/'.", required: true),
                P("content", "string", "Article body in Markdown.", required: true),
                P("tags", "array", "Optional tags as a JSON array of strings, e.g. [\"a\",\"b\"].", itemsType: "string")),
            Tool("bee_update_article", "Update an existing article's title, treePath, content, and/or tags. Only provided fields change; omitted fields are untouched. Replaces content fully when provided. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("title", "string", "New title (omit to keep)."),
                P("treePath", "string", "New tree path (omit to keep)."),
                P("content", "string", "New full body in Markdown (omit to keep)."),
                P("tags", "array", "New tag list as a JSON array of strings. Replaces ALL tags. Omit to keep; pass [] to clear.", itemsType: "string")),
            Tool("bee_append_to_article", "Append text to the end of an article (a blank line is inserted first). Cheaper than bee_update_article for adding a section/note. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("text", "string", "Text to append.", required: true)),
            Tool("bee_replace_in_article", "Case-sensitive substring find-and-replace within one article (not regex). Returns the occurrence count; 0 means no change. The user will be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("search", "string", "Exact text to find (case-sensitive).", required: true),
                P("replace", "string", "Replacement text.", required: true)),
            Tool("bee_delete_article", "Soft-delete an article (hidden from search/lists; restorable from the web UI). You MUST set confirm=true. The user will ALSO be asked to APPROVE this before it runs.",
                P("id", "string", "Article ID (GUID).", required: true, format: "uuid"),
                P("confirm", "boolean", "Must be true to delete. The user is separately asked to approve.")),
            // Image generation tool — declared alongside the bee_* tools so the text model can call
            // it from its normal tool loop. The actual egress (OpenRouter image-gen call + storing
            // the result as a chat attachment + emitting the inline image SSE event) is handled in
            // ChatEndpoints.RunToolLoopAsync, NOT here (the dispatcher has no OpenRouter/SSE access).
            // If no image-gen model is configured, the handler returns a graceful error tool result
            // so the model can tell the user in plain text.
            Tool("generate_image", "Generate an image from a text prompt. The generated image appears inline in the chat. Use this ONLY when the user explicitly asks to create, generate, or draw an image. If image generation is not configured, you will receive an error — tell the user in plain text.",
                P("prompt", "string", "A detailed description of the image to generate.", required: true)),
            // Inserts an attached/generated chat image into an article: uploads the blob to the article
            // media store and appends (or creates) the ![caption](/api/media/{id}) markdown reference.
            // The model must address the image by its attachmentId, surfaced via the attachment manifest
            // (after each image-bearing message) and the generate_image tool result (attachmentIds).
            Tool("bee_insert_image_into_article",
                "Insert a chat image (a user-uploaded attachment or a previously generated image) into an article's content. " +
                "Pass the attachmentId from the attachment manifest or from a generate_image result — copy the id EXACTLY, character for character, and never invent one. " +
                "Target EITHER an existing article (articleId — the image markdown is appended to its body) OR a new article " +
                "(title + treePath — the article is created with the image as its content). " +
                "The image is uploaded to the article media store and referenced as ![caption](/api/media/{id}) markdown. " +
                "The user will be asked to APPROVE this before it runs.",
                P("attachmentId", "string", "Chat attachment ID (GUID) of the image to insert. Copy it EXACTLY from the attachment manifest or a generate_image result.", required: true, format: "uuid"),
                P("articleId", "string", "Existing article ID (GUID). Omit when creating a new article.", format: "uuid"),
                P("title", "string", "Title for a NEW article. Required together with treePath when articleId is omitted."),
                P("treePath", "string", "Tree path for a NEW article, e.g. '/Work/Dev'. Must start with '/'."),
                P("caption", "string", "Optional image caption, used as the markdown alt text.")),
        ];
    }

    // Tiny JSON-Schema builder so we don't hand-write raw JSON strings.
    private static Models.ChatToolDefinition Tool(string name, string description, params (string pname, JsonElement schema, bool required)[] props)
    {
        var required = props.Where(p => p.required).Select(p => p.pname).ToArray();
        var properties = props.ToDictionary(p => p.pname, p => (object?)p.schema);
        var root = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Length > 0)
            root["required"] = required;
        return new Models.ChatToolDefinition
        {
            Function = new Models.ChatToolFunction { Name = name, Description = description, Parameters = ToElement(root) }
        };
    }

    private static (string pname, JsonElement schema, bool required) P(string name, string type, string desc, bool required = false, string? format = null, string? itemsType = null)
    {
        var o = new Dictionary<string, object?> { ["type"] = type, ["description"] = desc };
        if (format != null) o["format"] = format;
        // For array params (e.g. tags), declare the item type so the model emits the right shape.
        if (itemsType != null) o["items"] = new Dictionary<string, object?> { ["type"] = itemsType };
        return (name, ToElement(o), required);
    }

    private static JsonElement ToElement(object obj)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
