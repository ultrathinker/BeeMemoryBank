# MCP Server & AI Integration

## Overview

The MCP (Model Context Protocol) server is built into the API process at the `/mcp` endpoint. It allows AI agents (Claude Code, etc.) to read, write, and search articles through the standard MCP protocol.

## Transport

- Streamable HTTP (SSE) at `/mcp`
- Authentication: Bearer token in the `Authorization` HTTP header
- Auto-unlock: AgentAuthMiddleware decrypts the Master DEK from the bearer token on the first request — but only for an agent owned by a superadmin. An ordinary user's agent has no wrapped DEK at all (see docs/encryption.md's Agent section): it authenticates and works normally whenever the vault is already unlocked by someone else, but cannot unlock it itself. A tool call that needs decrypted content while the vault is locked fails with a clear "Bank is locked" error either way.

## 7 Tool Groups

### bee_search, bee_search_content (BeeSearchTools.cs)
Search by title, folder names, and optionally full article body content. Does not require unlock for basic search.
- `bee_search` — `keywords: string` — fast metadata search (title, folder names). Returns: `[{id, title, treePath}]`
- `bee_search_content` — `keywords: string`, `mode: string?` (`hybrid` default / `keyword` / `semantic`, invalid value is an error) — ranked search of article bodies (BM25 + chunk-embedding semantic similarity via RRF), plus title/folder matches merged in. Requires an unlocked session; degrades to title-only search (with a `notice`) when locked or when ranked/semantic search is unavailable on this node. Returns: `[{id, title, treePath}]`

### bee_list_articles, bee_get_article, bee_get_tree, bee_get_article_versions, bee_get_article_version, bee_get_article_diff, bee_get_image, bee_get_file (BeeReadTools.cs)
- `bee_list_articles` — list articles with optional path filter (`treePath: string?`) and `updatedAfter` filter. Returns: `[{id, title, treePath, status, createdAt, updatedAt}]`
- `bee_get_article` — metadata and optionally decrypted content (`id: Guid`, `content: bool`, default false). Returns: `{id, title, treePath, tags, relatedCount, relatedStrength, content?, files?, createdAt, updatedAt}`. `files` is present only when the article has attached files, also with `content=false`: `[{mediaId, fileName, contentType, sizeBytes, kind ("image" or "attachment"), createdAt}]`, metadata only
- `bee_get_tree` — folder tree with their articles (`path: string?`)
- `bee_get_article_versions` — list version history for an article, metadata only (`id: Guid`). Returns: `[{id, versionNumber, title, treePath, createdAt, updatedBy}]`
- `bee_get_article_version` — get decrypted content of a specific version (`id: Guid`, `versionNumber: int`). Requires unlocked session. Returns version metadata + decrypted `content`
- `bee_get_article_diff` — markdown-block diff between an article's current content and its state as of a given baseline timestamp (`id: Guid`, `baselineAt: DateTime`). Requires unlocked session.
- `bee_get_image` — get an image from an article (`id: Guid`, `maxSizeKb: int?`). Decrypts on the fly and resizes to fit within token limits. Returns image as an inline content block.
- `bee_get_file` — get a file attached to an article as the file itself (`id: Guid`, or `articleId: Guid` + `fileName: string`, never both forms; an error lists the ids when several files share the name). Images (PNG, JPEG, GIF, WEBP) come back unchanged as an inline image content block, every other file as an embedded resource (blob) with its file name and content type; nothing is converted or extracted. Same access rules as `bee_get_image`; files of a password-protected article (also for `bee_get_image`) and files above 10 MB are refused with an error. Requires unlocked session.

### bee_save_article, bee_update_article, bee_delete_article, bee_append_to_article, bee_prepend_to_article, bee_move_folder, bee_delete_folder, bee_rename_folder, bee_copy_to, bee_replace_in_article (BeeWriteTools.cs)
- `bee_save_article` — create (`title`, `treePath`, `content`, `tags?`). Auto-creates missing folders in `treePath`. Warns when content exceeds 5000 characters.
- `bee_update_article` — update (`id`, optionally `title`, `treePath`, `content`, `tags`). Omitted fields remain unchanged. Creates a version snapshot of the previous content on every save.
- `bee_delete_article` — soft delete (`id`, `confirm: bool`)
- `bee_delete_folder` — delete an empty folder (`path`, `confirm: bool`). Refuses if folder contains articles or subfolders.
- `bee_append_to_article` — append text to the end of an article without reading the full content. Saves tokens.
- `bee_prepend_to_article` — prepend text to the beginning of an article without reading the full content. Saves tokens.
- `bee_move_folder` — move a folder (`path`, `newParentPath`)
- `bee_rename_folder` — rename a folder in place (`path`, `newName`)
- `bee_copy_to` — copy an article (or a whole folder) to another location (`sourceId`, `targetPath`); returns the new id
- `bee_replace_in_article` — find/replace exact text within one article (`id`, `search`, `replace`) without resending the whole body

### bee_set_max_tokens, bee_continue (BeeSessionTools.cs)
- `bee_set_max_tokens` — set your own default token limit for MCP responses (min 1,000, default 10,000, max 100,000). A value outside that range is rejected with an error, never silently clamped. The limit is per-caller (keyed off the agent's bearer token), so raising it never affects other agents.
- `bee_continue` — read the continuation of a truncated response (`guid`, `offset`). Pass `ignoreLimit: true` to fetch all remaining content in one call instead of the next chunk, bypassing your own limit for that call only (still capped at the same hard 100,000-token ceiling). Responses are stored for 24 hours.

### bee_get_upload_script, bee_save_media, bee_delete_file (BeeUploadTools.cs)
- `bee_get_upload_script` — returns a self-contained Python script for uploading files from disk to BeeMemoryBank **without** passing content through the LLM context. Supports `create`, `update`, and `upload-media` subcommands. Uses only stdlib (no pip install required). `--client-name` is required: the name of the agent's main MCP client, reported as `clientInfo.name` (a gateway creates a new client for every new name, so never invent one). `--bearer` is optional (through a gateway the gateway supplies the credentials); `--tool-prefix` puts a gateway's server prefix in front of the tool names (e.g. `bee-memory-bank__`).
- `bee_save_media` — save a media file directly from a base64 payload (`fileName`, `contentBase64`, `articleId?`), for cases where the agent already has the content in-context (small files) rather than on disk. Capped at 20MB.
- `bee_delete_file` — delete a file of an article, an inline image or a file attachment (`id: Guid`, or `articleId: Guid` + `fileName: string`, never both forms; an error lists the ids when several files share the name; an unlinked upload is deleted by `id`). Two steps like `bee_delete_article`: without `confirm: true` it only describes the file and returns a warning. A soft delete, the same as the delete button of the web UI, and it reaches the other devices by sync. Refuses the files of a password-protected article and a folder that is read-only for the caller. When the article text still embeds the image (`![](/api/media/{id})`) the answer says the reference becomes a broken image.

### Tag tools (BeeConceptTools.cs)
- `bee_get_related` — find articles related to a given article via shared tags. Returns: `[{id, title, treePath, strength, sharedTags}]`
- `bee_search_by_tag` — find all articles carrying a specific tag. Returns: `[{id, title, treePath}]`
- `bee_list_tags` — list tags with optional substring or semantic filter. Returns: `[{name, articleCount}]`
- `bee_add_tags` — add tags to an article (additive). Returns updated tag list.
- `bee_remove_tag` — remove a specific tag from an article.
- `bee_rename_tag` — rename a tag globally (affects all articles).
- `bee_merge_tags` — merge source tag into target (source is deleted, all articles updated).
- `bee_delete_tag` — delete a tag globally (removes from all articles).

### bee_get_log (BeeAuditTools.cs)
- Query the activity log with filters: `articleId`, `eventType`, `limit` (1-200), `offset`. Resolves node names and article titles.
- By default returns only **article-tied** events (article_create/update/delete, comment_*, media_*).
- Pass `includeAdminEvents=true` to also see node-administration events (whitelist_*, hard_delete, dek_rotation_*, restore_network, snapshot_checkpoint). Honoured **only** when the caller resolves to superadmin via `CallerIdentity.Extract`; non-superadmin agents always receive the article-only view regardless of the flag, so a stolen agent token from a regular user cannot enumerate admin actions.

## Truncation (McpResponseManager)

Large responses are automatically truncated:
- If a response exceeds the caller's token limit → full content is saved to a temp file
- Plain-text responses: the first ~90% of budget is returned inline + a warning with `guid` and `offset`
- JSON responses: only a short preview is returned (truncating mid-structure would break parsing) + an envelope with `guid` and `offset: 0` — the caller must always continue from the start
- The AI agent calls `bee_continue(guid, offset)` to read the next part, or `bee_continue(guid, offset, ignoreLimit: true)` to get everything remaining in one call (capped at 100,000 tokens)
- Temp files are automatically deleted after 24 hours
- Token estimation: `ceil(UTF8ByteCount / 3.0)` — conservative estimate

## Files

```
server/BeeMemoryBank.Api/McpTools/
├── BeeSearchTools.cs    — bee_search, bee_search_content
├── BeeReadTools.cs      — bee_list_articles, bee_get_article, bee_get_tree,
│                          bee_get_article_versions, bee_get_article_version, bee_get_article_diff, bee_get_image,
│                          bee_get_file
├── BeeWriteTools.cs     — bee_save_article, bee_update_article, bee_delete_article,
│                          bee_delete_folder, bee_append_to_article, bee_prepend_to_article,
│                          bee_move_folder, bee_rename_folder, bee_copy_to, bee_replace_in_article
├── BeeSessionTools.cs   — bee_set_max_tokens, bee_continue
├── BeeUploadTools.cs    — bee_get_upload_script, bee_save_media, bee_delete_file
├── BeeAuditTools.cs     — bee_get_log
├── BeeConceptTools.cs   — bee_get_related, bee_search_by_tag, bee_list_tags,
│                          bee_add_tags, bee_remove_tag, bee_rename_tag,
│                          bee_merge_tags, bee_delete_tag
├── McpResponseManager.cs — truncation, pagination, temp file management
└── TokenEstimator.cs    — token estimation for truncation
```

## 33 MCP Tools

| Group | Tools |
|---|---|
| **Search** (2) | `bee_search`, `bee_search_content` |
| **Read** (8) | `bee_list_articles`, `bee_get_article`, `bee_get_tree`, `bee_get_image`, `bee_get_file`, `bee_get_article_version`, `bee_get_article_versions`, `bee_get_article_diff` |
| **Write** (10) | `bee_save_article`, `bee_update_article`, `bee_delete_article`, `bee_append_to_article`, `bee_prepend_to_article`, `bee_move_folder`, `bee_delete_folder`, `bee_copy_to`, `bee_rename_folder`, `bee_replace_in_article` |
| **Tags** (8) | `bee_get_related`, `bee_search_by_tag`, `bee_list_tags`, `bee_add_tags`, `bee_remove_tag`, `bee_rename_tag`, `bee_merge_tags`, `bee_delete_tag` |
| **Session** (2) | `bee_set_max_tokens`, `bee_continue` |
| **Upload** (3) | `bee_get_upload_script`, `bee_save_media`, `bee_delete_file` |
| **Audit** (1) | `bee_get_log` |

## Configuration Examples

### Claude Code

Add to your Claude Code MCP settings (`.claude/settings.json` or project-level):

```json
{
  "mcpServers": {
    "bee-memory-bank": {
      "type": "http",
      "url": "https://your-server.example.com/mcp",
      "headers": {
        "Authorization": "Bearer bee_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
      }
    }
  }
}
```

### Cursor

Add to your Cursor MCP configuration (`.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "bee-memory-bank": {
      "type": "http",
      "url": "https://your-server.example.com/mcp",
      "headers": {
        "Authorization": "Bearer bee_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
      }
    }
  }
}
```

The Bearer token is created on the Admin → Agents page. It is shown only once — copy it when creating the agent.
