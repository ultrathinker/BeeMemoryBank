# MCP Server & AI Integration

## Overview

The MCP (Model Context Protocol) server is built into the API process at the `/mcp` endpoint. It allows AI agents (Claude Code, etc.) to read, write, and search articles through the standard MCP protocol.

## Transport

- Streamable HTTP (SSE) at `/mcp`
- Authentication: Bearer token in the `Authorization` HTTP header
- Auto-unlock: AgentAuthMiddleware decrypts the Master DEK from the bearer token on the first request

## 6 Tool Groups

### bee_search, bee_search_content (BeeSearchTools.cs)
Search by title, tags, and optionally full article body content. Does not require unlock for basic search.
- `bee_search` — `keywords: string` — fast metadata search (title, tags, folder names). Returns: `[{id, title, treePath, tags}]`
- `bee_search_content` — `keywords: string` — **SLOW** — decrypts and scans all article bodies in batches. Only use when `bee_search` didn't find what you need. Requires an unlocked session; if locked, falls back to title/tags only. Returns: `[{id, title, treePath, tags}]`

### bee_list_articles, bee_get_article, bee_get_tree, bee_get_article_versions, bee_get_article_version (BeeReadTools.cs)
- `bee_list_articles` — list articles with optional path filter (`treePath: string?`)
- `bee_get_article` — metadata and optionally decrypted content (`id: Guid`, `content: bool`, default false)
- `bee_get_tree` — folder tree with their articles (`path: string?`)
- `bee_get_article_versions` — list version history for an article, metadata only (`id: Guid`). Returns: `[{id, versionNumber, title, tags, treePath, createdAt, updatedBy}]`
- `bee_get_article_version` — get decrypted content of a specific version (`id: Guid`, `versionNumber: int`). Requires unlocked session. Returns version metadata + decrypted `content`

### bee_save_article, bee_update_article, bee_delete_article, bee_append_to_article, bee_prepend_to_article, bee_move_folder, bee_delete_folder (BeeWriteTools.cs)
- `bee_save_article` — create (`title`, `treePath`, `content`, `tags?`). Warns when content exceeds 5000 characters.
- `bee_update_article` — update (`id`, optionally `title`, `treePath`, `content`, `tags`). Omitted fields remain unchanged.
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

### bee_get_log (BeeAuditTools.cs)
- Query the activity log with filters: `articleId`, `eventType`, `limit` (1-200), `offset`. Resolves node names and article titles.

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
│                          bee_get_article_versions, bee_get_article_version
├── BeeWriteTools.cs     — bee_save_article, bee_update_article, bee_delete_article,
│                          bee_delete_folder, bee_append_to_article, bee_prepend_to_article,
│                          bee_move_folder
├── BeeSessionTools.cs   — bee_set_max_tokens, bee_continue
├── BeeUploadTools.cs    — bee_get_upload_script
├── BeeAuditTools.cs     — bee_get_log
├── McpResponseManager.cs — truncation, pagination, temp file management
└── TokenEstimator.cs    — token estimation for truncation
```

## 18 MCP Tools

| Group | Tools |
|---|---|
| **Search** | `bee_search`, `bee_search_content` |
| **Read** | `bee_list_articles`, `bee_get_article`, `bee_get_tree`, `bee_get_article_versions`, `bee_get_article_version` |
| **Write** | `bee_save_article`, `bee_update_article`, `bee_delete_article`, `bee_delete_folder`, `bee_append_to_article`, `bee_prepend_to_article`, `bee_move_folder` |
| **Session** | `bee_set_max_tokens`, `bee_continue` |
| **Upload** | `bee_get_upload_script` |
| **Audit** | `bee_get_log` |

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
