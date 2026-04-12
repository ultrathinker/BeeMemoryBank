# :bee: BeeMemoryBank

> The only open-source personal knowledge management system with native MCP support for AI agents

![License](https://img.shields.io/badge/license-AGPL--3.0-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![MCP](https://img.shields.io/badge/MCP-native-orange)
![Encryption](https://img.shields.io/badge/E2E-AES--256--GCM-green)
![SQLite](https://img.shields.io/badge/DB-SQLite-lightgrey)

### Demo

<table>
<tr>
<td align="center"><img src="docs/screenshots/demo-create.gif" width="380"><br><sub>Create &amp; Organize — browse tree, edit article, create folder</sub></td>
<td align="center"><img src="docs/screenshots/demo-search.gif" width="380"><br><sub>Search — full-text search across encrypted content</sub></td>
</tr>
<tr>
<td align="center" colspan="2"><img src="docs/screenshots/demo-article.gif" width="380"><br><sub>Rich Markdown — rendered content, switch themes</sub></td>
</tr>
</table>

### Screenshots

<details>
<summary>Web UI — Themes</summary>

| Dark Classic | Dark Bee | Ocean |
|:---:|:---:|:---:|
| ![Dark Classic](docs/screenshots/tree-dark-classic.png) | ![Dark Bee](docs/screenshots/tree-dark-bee.png) | ![Ocean](docs/screenshots/tree-ocean.png) |
| ![Article](docs/screenshots/article-dark-classic.png) | ![Article](docs/screenshots/article-dark-bee.png) | ![Article](docs/screenshots/article-ocean.png) |

</details>

<details>
<summary>Web UI — Crimson Theme</summary>

| Tree View | Article | Search | Admin |
|:---:|:---:|:---:|:---:|
| ![Tree](docs/screenshots/tree-crimson.png) | ![Article](docs/screenshots/article-crimson.png) | ![Search](docs/screenshots/search-crimson.png) | ![Admin](docs/screenshots/admin-crimson.png) |

</details>

<details>
<summary>Mobile App (Android)</summary>

| Articles | Article View | Tree | Tags | Sync Status |
|:---:|:---:|:---:|:---:|:---:|
| ![Articles](docs/screenshots/mobile-articles.png) | ![Article](docs/screenshots/mobile-article.png) | ![Tree](docs/screenshots/mobile-tree.png) | ![Tags](docs/screenshots/mobile-tags.png) | ![Status](docs/screenshots/mobile-status.png) |

</details>

## What is BeeMemoryBank?

BeeMemoryBank is a **self-hosted personal knowledge base** with end-to-end encryption, multi-node synchronization, and a built-in MCP server that lets AI agents (Claude Code, Cursor, Windsurf, and others) read, write, and search your notes as naturally as any other tool. Your data stays on your servers, encrypted at rest with per-article keys, and syncs automatically across devices using event sourcing with Lamport clocks.

---

## :sparkles: Features

| | Feature | Details |
|---|---|---|
| :lock: | **E2E Encryption** | AES-256-GCM with per-article and per-image keys, Argon2id KDF (64 MB, 3 iterations), envelope encryption with 3-level key hierarchy |
| :arrows_counterclockwise: | **Multi-Node Sync** | Event sourcing, Lamport clocks, Ed25519-signed events, near-realtime push-on-save sync between public nodes, works behind NAT |
| :robot: | **Native MCP Server** | 18 tools across 6 categories, token-aware truncation, zero-context file upload, sync status UI |
| :framed_picture: | **Encrypted Images** | Drag & drop, paste, or upload images in the editor — encrypted with per-image keys, decrypted on the fly |
| :globe_with_meridians: | **Web UI** | Dark theme, Markdown editor (EasyMDE), folder tree, tag management, activity feed |
| :iphone: | **Android App** | .NET MAUI, biometric unlock, offline-first |
| :keyboard: | **CLI** | `bmb` command-line tool for init, join, unlock, article management, snapshots |
| :jigsaw: | **REST API** | 15 endpoint groups, OpenAPI support, agent bearer auth with auto-unlock |
| :busts_in_silhouette: | **Multi-User Auth** | Role-based access (superadmin, unlocker, user), per-user key slots, team-ready |
| :shield: | **Audit Log** | Every operation tracked with actor type (web/agent/cli), node identity, timestamps |
| :clock9: | **Version History** | Encrypted article version history, inline diff viewer, who-changed tracking, fullscreen dialog |
| :closed_lock_with_key: | **Folder Access Control** | Per-folder ACL for users and AI agents independently, prevents horizontal privilege escalation |
| :ghost: | **Invisible Mode** | Node can hide itself from sync partners while still pulling events |
| :satellite: | **Event Relay (Gossip)** | Nodes push all events, not just their own — faster convergence across the network |

---

## :robot: AI Agent Integration (MCP)

BeeMemoryBank implements the [Model Context Protocol](https://modelcontextprotocol.io/) natively, exposing your knowledge base as a set of tools that any MCP-compatible AI agent can use.

### Configuration

Add to your Claude Code settings (`~/.claude/settings.json`) or Cursor MCP config:

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

The bearer token is created in the Web UI under **Admin > Agents** and is shown once at creation time.

### Available Tools

| Category | Tools | Description |
|---|---|---|
| **Search** | `bee_search`, `bee_search_content` | Fast metadata search (title/tags) + opt-in full-text body search (decrypts in batches) |
| **Read** | `bee_list_articles`, `bee_get_article`, `bee_get_tree` | Browse folders, read article content (auto-decrypted) |
| **Write** | `bee_save_article`, `bee_update_article`, `bee_delete_article`, `bee_append_to_article`, `bee_prepend_to_article`, `bee_move_folder`, `bee_delete_folder` | Full CRUD with soft-delete and folder management |
| **Session** | `bee_set_max_tokens`, `bee_continue` | Control response size, paginate large responses |
| **Upload** | `bee_get_upload_script` | Get a Python script for zero-context file uploads (bypasses LLM context window) |
| **Audit** | `bee_get_log` | Query activity log with filters by article, event type, pagination |

### Example Workflow

```
You: "Save our meeting notes to BeeMemoryBank"

Agent calls: bee_save_article(
    title: "Team Sync 2026-04-08",
    treePath: "/Work/Meetings",
    content: "## Decisions\n- Ship v2.0 by Friday\n...",
    tags: ["meeting", "team"]
)

Agent calls: bee_get_tree(path: "/Work")
→ Shows the folder structure with all articles
```

### Zero-Context Upload

For large files already on disk, agents can use `bee_get_upload_script` to get a self-contained Python script that uploads files directly from disk to the server, **without reading them into the LLM context window**:

```bash
python bmb-upload.py --url https://your-server.example.com \
  --bearer bee_xxx create ./large-doc.md "Architecture Doc" /Work/Docs
```

---

## :rocket: Quick Start

### From Source

```bash
# 1. Clone and build (requires .NET 10 SDK)
git clone https://github.com/ultrathinker/BeeMemoryBank.git
cd BeeMemoryBank
dotnet publish server/BeeMemoryBank.Api/ -c Release -o publish/api
dotnet publish server/BeeMemoryBank.Web/ -c Release -o publish/web
dotnet publish server/BeeMemoryBank.Cli/ -c Release -o publish/cli

# 2. Initialize a new knowledge base
./publish/cli/bmb init --data ./data --name "MyNode" --password "your-master-password"

# 3. Start the API server
BMB_DATA_PATH=./data ASPNETCORE_URLS=http://localhost:5300 ./publish/api/BeeMemoryBank.Api

# 4. Start the Web UI (in another terminal)
BMB_API_URL=http://localhost:5300 ASPNETCORE_URLS=http://localhost:5301 ./publish/web/BeeMemoryBank.Web
```

Open `http://localhost:5301` in your browser and log in with your master password.

### Join an Existing Network

To add a second node (e.g., a VPS) to sync with your first:

```bash
./publish/cli/bmb join --remote https://first-node.example.com \
  --password "your-master-password" --name "VPS-Node" --data /var/lib/beememorybank
```

---

## :building_construction: Architecture

```
┌─────────────────────────────────────────────────────────┐
│                        Clients                          │
│   Web UI (Razor Pages)  │  CLI (bmb)  │  Mobile (MAUI)  │
└────────────┬─────────────────┬──────────────┬───────────┘
             │ HTTP            │ HTTP         │ HTTP
             ▼                 ▼              ▼
┌─────────────────────────────────────────────────────────┐
│              BeeMemoryBank.Api                          │
│   REST Endpoints (15 groups)  │  MCP Server (/mcp)      │
│   Agent Auth Middleware       │  Rate Limiting           │
└────────────┬────────────────────────────────────────────┘
             │
     ┌───────┼───────┬──────────┐
     ▼       ▼       ▼          ▼
  ┌──────┐ ┌──────┐ ┌────────┐ ┌──────┐
  │ Core │ │Crypto│ │Storage │ │ Sync │
  │      │ │      │ │(SQLite)│ │      │
  └──────┘ └──────┘ └────────┘ └──────┘
```

**Dependency flow:** `Core` <-- `Storage`, `Crypto` <-- `Sync` <-- `Api`. No circular dependencies. `Web` is a stateless HTTP proxy to `Api`.

### Encryption Layers

```
Master Password (in your head)
    │
    ▼  Argon2id (64 MB, 3 iter, 4 threads)
    │
KEK (Key Encryption Key)
    │
    ▼  AES-256-GCM unwrap
    │
Master DEK (one per network, lives only in RAM)
    │
    ▼  AES-256-GCM unwrap
    │
Per-Article/Media DEK (unique random key per article and per image)
    │
    ▼  AES-256-GCM
    │
Plaintext
```

**Why three levels?**
- **Password change** re-encrypts one Master DEK, not every article
- **Per-article/media DEK** isolates articles and images: compromising one key does not expose others
- **Agent tokens** store Master DEK encrypted with a derived key, providing another "entry point" without the password

---

## :bar_chart: Comparison

| Feature | BeeMemoryBank | Obsidian | SiYuan | Trilium | Standard Notes | Joplin |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| **E2E Encryption** | :white_check_mark: AES-256-GCM | :x: (plugin) | :x: | :x: | :white_check_mark: | :white_check_mark: |
| **Per-Article Keys** | :white_check_mark: | :x: | :x: | :x: | :x: | :x: |
| **Self-Hosted Sync** | :white_check_mark: Built-in | :x: (paid) | :white_check_mark: | :white_check_mark: | :white_check_mark: | :white_check_mark: |
| **Native MCP** | :white_check_mark: 18 tools | :x: | :x: | :x: | :x: | :x: |
| **AI Agent Ready** | :white_check_mark: | :x: (plugin) | :x: | :x: | :x: | :x: |
| **Mobile App** | :white_check_mark: Android | :white_check_mark: | :white_check_mark: | :x: (PWA) | :white_check_mark: | :white_check_mark: |
| **Offline-First** | :white_check_mark: | :white_check_mark: | :white_check_mark: | :white_check_mark: | :x: | :white_check_mark: |
| **Self-Hosted** | :white_check_mark: | N/A (local) | :white_check_mark: | :white_check_mark: | :white_check_mark: | :white_check_mark: |
| **Version History** | :white_check_mark: Encrypted | :x: | :x: | :x: | :x: | :x: |
| **License** | AGPL-3.0 | Proprietary | AGPL-3.0 | AGPL-3.0 | AGPL-3.0 | AGPL-3.0 |
| **Stack** | .NET 10, SQLite | Electron | Go, SQLite | Node.js | Node.js | Node.js |

---

## :world_map: Roadmap

- [x] E2E encryption with per-article keys
- [x] Multi-node sync with event sourcing and near-realtime push-on-save
- [x] Native MCP server (18 tools)
- [x] Web UI with Markdown editor
- [x] CLI tool (`bmb`)
- [x] Android app (.NET MAUI)
- [x] Agent bearer auth with auto-unlock
- [x] Activity audit log
- [x] Multi-user authentication with role-based access (superadmin, unlocker, user)
- [x] Docker Compose deployment
- [x] Full-text search (article body, encrypted content)
- [x] Encrypted image storage with per-image keys (drag & drop, paste, upload)
- [x] Article version history with encrypted storage and inline diff viewer
- [x] Folder-level access control with per-folder ACL
- [x] Invisible mode for node synchronization
- [ ] CI/CD pipeline (GitHub Actions)
- [ ] Import from Obsidian / Markdown folders
- [ ] Backlinks and graph view
- [ ] iOS app

---

## :handshake: Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on how to get started.

---

## :lock: Security

BeeMemoryBank uses a defense-in-depth approach:

- **AES-256-GCM** authenticated encryption for all article content
- **Argon2id** key derivation (OWASP recommended parameters)
- **Per-article and per-media DEKs** for cryptographic isolation
- **Ed25519** signatures on all sync events (tamper-proof)
- **Master DEK** lives only in RAM, wiped on process shutdown
- **Rate limiting** on authentication endpoints (brute-force protection)
- **Sentinel verification** ensures key compatibility across nodes
- **Folder-level access control** — per-folder ACL prevents horizontal privilege escalation between users and AI agents
- **Agent privilege escalation prevention** — agents cannot spoof elevated permissions via request headers
- **DOMPurify** sanitization for all Markdown-rendered content in the Web UI (XSS prevention)

For responsible disclosure, please see [SECURITY.md](SECURITY.md).

---

## :page_facing_up: License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).

For commercial licensing inquiries, please contact: **evgeny.borzenkov@gmail.com**

---

## :pray: Acknowledgments

Built with these excellent open-source projects:

- [.NET](https://dotnet.microsoft.com/) and [ASP.NET Core](https://github.com/dotnet/aspnetcore)
- [SQLite](https://sqlite.org/) via [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore)
- [Dapper](https://github.com/DapperLib/Dapper) micro-ORM
- [BouncyCastle](https://www.bouncycastle.org/) for Ed25519 signatures
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) for Argon2id
- [ModelContextProtocol SDK](https://github.com/modelcontextprotocol/csharp-sdk) for MCP server
- [EasyMDE](https://github.com/Ionaru/easy-markdown-editor) Markdown editor
- [Shoelace](https://shoelace.style/) web components
- [Tagify](https://github.com/yairEO/tagify) tag input
