# MCP Server & AI Integration

## Overview

The MCP (Model Context Protocol) server is built into the API process at the `/mcp` endpoint. It allows AI agents (Claude Code, etc.) to read, write, and search articles through the standard MCP protocol.

## Transport

- Streamable HTTP (SSE) at `/mcp`
- Authentication: Bearer token in the `Authorization` HTTP header
- Auto-unlock: AgentAuthMiddleware decrypts the Master DEK from the bearer token on the first request

## 7 Tool Groups

### bee_search, bee_search_content (BeeSearchTools.cs)
Search by title, folder names, and optionally full article body content. Does not require unlock for basic search.
- `bee_search` — `keywords: string` — fast metadata search (title, folder names). Returns: `[{id, title, treePath}]`
- `bee_search_content` — `keywords: string` — **SLOW** — decrypts and scans all article bodies in batches. Only use when `bee_search` didn't find what you need. Requires an unlocked session; if locked, falls back to title only. Returns: `[{id, title, treePath}]`

### bee_list_articles, bee_get_article, bee_get_tree, bee_get_article_versions, bee_get_article_version, bee_get_image (BeeReadTools.cs)
- `bee_list_articles` — list articles with optional path filter (`treePath: string?`). Returns: `[{id, title, treePath, status, createdAt, updatedAt}]`
- `bee_get_article` — metadata and optionally decrypted content (`id: Guid`, `content: bool`, default false). Returns: `{id, title, treePath, tags, relatedCount, relatedStrength, content?, createdAt, updatedAt}`
- `bee_get_tree` — folder tree with their articles (`path: string?`)
- `bee_get_article_versions` — list version history for an article, metadata only (`id: Guid`). Returns: `[{id, versionNumber, title, treePath, createdAt, updatedBy}]`
- `bee_get_article_version` — get decrypted content of a specific version (`id: Guid`, `versionNumber: int`). Requires unlocked session. Returns version metadata + decrypted `content`
- `bee_get_image` — get an image from an article (`id: Guid`, `maxSizeKb: int?`). Decrypts on the fly and resizes to fit within token limits. Returns image as an inline content block.

### bee_save_article, bee_update_article, bee_delete_article, bee_append_to_article, bee_prepend_to_article, bee_move_folder, bee_delete_folder (BeeWriteTools.cs)
- `bee_save_article` — create (`title`, `treePath`, `content`, `tags?`). Auto-creates missing folders in `treePath`. Warns when content exceeds 5000 characters.
- `bee_update_article` — update (`id`, optionally `title`, `treePath`, `content`, `tags`). Omitted fields remain unchanged. Creates a version snapshot of the previous content on every save.
- `bee_delete_article` — soft delete (`id`, `confirm: bool`)
- `bee_delete_folder` — delete an empty folder (`path`, `confirm: bool`). Refuses if folder contains articles or subfolders.
- `bee_append_to_article` — append text to the end of an article without reading the full content. Saves tokens.
- `bee_prepend_to_article` — prepend text to the beginning of an article without reading the full content. Saves tokens.
- `bee_move_folder` — move a folder (`path`, `newParentPath`)

### bee_set_max_tokens, bee_continue (BeeSessionTools.cs)
- `bee_set_max_tokens` — set token limit for MCP responses (min 1000, default 10000, max 20000)
- `bee_continue` — read the continuation of a truncated response (`guid`, `offset`). Responses are stored for 24 hours.

### bee_get_upload_script (BeeUploadTools.cs)
- Returns a self-contained Python script (~94 lines) for uploading files from disk to BeeMemoryBank **without** passing content through the LLM context. Supports `create` and `update` subcommands. Uses only stdlib (no pip install required).

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
- If a response exceeds the token limit → full content is saved to a temp file
- The first ~90% is returned + a warning with `guid` and `offset`
- The AI agent calls `bee_continue(guid, offset)` to read the next part
- Temp files are automatically deleted after 24 hours
- Token estimation: `ceil(UTF8ByteCount / 3.0)` — conservative estimate

## Files

```
server/BeeMemoryBank.Api/McpTools/
├── BeeSearchTools.cs    — bee_search, bee_search_content
├── BeeReadTools.cs      — bee_list_articles, bee_get_article, bee_get_tree,
│                          bee_get_article_versions, bee_get_article_version, bee_get_image
├── BeeWriteTools.cs     — bee_save_article, bee_update_article, bee_delete_article,
│                          bee_delete_folder, bee_append_to_article, bee_prepend_to_article,
│                          bee_move_folder
├── BeeSessionTools.cs   — bee_set_max_tokens, bee_continue
├── BeeUploadTools.cs    — bee_get_upload_script
├── BeeAuditTools.cs     — bee_get_log
├── BeeConceptTools.cs   — bee_get_related, bee_search_by_tag, bee_list_tags,
│                          bee_add_tags, bee_remove_tag, bee_rename_tag,
│                          bee_merge_tags, bee_delete_tag
├── McpResponseManager.cs — truncation, pagination, temp file management
└── TokenEstimator.cs    — token estimation for truncation
```

## 36 MCP Tools

| Group | Tools |
|---|---|
| **Search** (3) | `bee_search`, `bee_search_content`, `bee_search_by_tag` |
| **Read** (7) | `bee_list_articles`, `bee_get_article`, `bee_get_tree`, `bee_get_article_versions`, `bee_get_article_version`, `bee_get_image`, `bee_get_related` |
| **Write** (10) | `bee_save_article`, `bee_update_article`, `bee_delete_article`, `bee_delete_folder`, `bee_append_to_article`, `bee_prepend_to_article`, `bee_replace_in_article`, `bee_move_folder`, `bee_rename_folder`, _(and folder-level helpers)_ |
| **Tags** (9) | `bee_list_tags`, `bee_add_tags`, `bee_remove_tag`, `bee_rename_tag`, `bee_merge_tags`, `bee_delete_tag`, and concept-tag graph helpers |
| **Session** (3) | `bee_set_max_tokens`, `bee_continue`, and session helpers |
| **Upload** (2) | `bee_get_upload_script` and upload helpers |
| **Audit** (2) | `bee_get_log` and audit helpers |

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
