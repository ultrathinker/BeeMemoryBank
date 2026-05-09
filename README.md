<h1>
  <img src="docs/icon.png" width="40" align="left" alt="BeeMemoryBank">
  BeeMemoryBank
</h1>

> **Your AI agents' shared memory.** Self-hosted, end-to-end encrypted, syncs across every device you own — and your AI agent works with it natively from day one.

![License](https://img.shields.io/badge/license-AGPL--3.0-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![MCP](https://img.shields.io/badge/MCP-native-orange)
![Encryption](https://img.shields.io/badge/E2E-AES--256--GCM-green)
![SQLite](https://img.shields.io/badge/DB-SQLite-lightgrey)
![Tests](https://img.shields.io/badge/tests-349%2F349-brightgreen)
![Security audit](https://img.shields.io/badge/security_audit-7_waves_clean-brightgreen)

### Demo

<div align="center">
  <img src="docs/screenshots/create-docs-web.gif" width="780"><br>
  <sub>AI-generated documentation — Claude Code analyzes a project and writes structured docs directly into BeeMemoryBank</sub>
</div>

<table>
<tr>
<td align="center"><img src="docs/screenshots/demo-create.gif" width="380"><br><sub>Create &amp; Organize — browse tree, edit article, create folder</sub></td>
<td align="center"><img src="docs/screenshots/demo-search.gif" width="380"><br><sub>Search — full-text search across encrypted content</sub></td>
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
<summary>Knowledge Graph — links through tags, not pairs</summary>

One tag connects an article to every other article that shares it — by topic, project, technology, status, year, any dimension you pick. No manual `[[wiki-links]]` to maintain, no dead-end pairs. Add a tag — the graph rewires itself.

| Explore view | Full graph | Fullscreen focus |
|:---:|:---:|:---:|
| ![Explore](docs/screenshots/graph-explore.jpg) | ![Full](docs/screenshots/graph-full.jpg) | ![Fullscreen](docs/screenshots/graph-fullscreen.jpg) |
| Popular tags (blue) and recently active ones (orange); dashed nodes hide neighbors — double-click to expand. | All tags at once: clusters, orphans, and the long tail. | Click any tag to see its direct neighbors across the whole graph. |

</details>

<details>
<summary>Mobile App (Android)</summary>

| Articles | Article View | Tags | Sync Status |
|:---:|:---:|:---:|:---:|
| ![Articles](docs/screenshots/mobile-articles.png) | ![Article](docs/screenshots/mobile-article.png) | ![Tags](docs/screenshots/mobile-tags.png) | ![Status](docs/screenshots/mobile-status.png) |

</details>

## Why BeeMemoryBank?

You've been talking to AI agents for months. Every conversation produces useful artifacts — research notes, decisions, code reviews, project plans, meeting summaries. And every conversation **forgets it the moment the window closes**.

The fix isn't another notes app. It's a memory the agent can read **and write to** itself, that you actually control:

- **The agent saves and retrieves on its own.** No copy-paste. No "let me put that in Notion later." Native [MCP](https://modelcontextprotocol.io/) support — the agent treats your knowledge base like its own working memory. 36 tools across 7 categories. Token-aware truncation so a 50KB article doesn't blow your context window.
- **Your data, your server.** Self-hosted on your laptop, your VPS, your home NAS. End-to-end encrypted with **per-article keys** — the encryption happens on your device, the server holds ciphertext. No vendor lock-in, no telemetry, no "we updated our terms of service".
- **Syncs everywhere automatically.** Three nodes on three continents stay in sync via Ed25519-signed events with Lamport-clock conflict resolution. Push-on-save means your phone sees the article seconds after your laptop saves it. Works behind NAT.
- **Team-ready when you need it.** Per-folder ACLs, per-user key slots, per-agent isolation. Each teammate connects their own AI agent; the agent can only see folders the user can see.
- **Production-grade.** Survived 7 sequential security audit waves (crypto, sync, auth, input, admin, hygiene, mobile) — every finding either fixed or explicitly documented as accepted-risk. **349 tests pass on every build.** Online DEK rotation, snapshot/restore, hard-delete propagation, encrypted version history, audit log. Code is open under AGPL-3.0; nothing hidden.

If you've ever wished your AI assistant could remember the work it did with you yesterday — this is the answer.

---

## :sparkles: Features

| | Feature | Details |
|---|---|---|
| :robot: | **Native MCP for AI Agents** | 36 tools across 7 categories, **per-agent DEK isolation**, **token-aware truncation with `bee_continue` pagination**, **zero-context file uploads** (bypass the LLM context window), `append`/`prepend` operations for incremental edits without re-reading articles |
| :inbox_tray: | **Obsidian Vault Import** | One-click migration: upload an Obsidian vault as a ZIP — Markdown files become articles, folders map directly, Obsidian `![[image.png]]` embeds are rewritten to encrypted media |
| :dna: | **Emergent Semantic Graph** | Concept tags create automatic bidirectional links **through shared characteristics, not article-to-article pairs** — one tag connects a note to every article that shares it (topic, project, tech, status, year — any dimension you pick). No manual `[[wiki-links]]` to maintain, no dead-end pairs; add a tag and the graph rewires itself. D3.js force-directed graph with depth-controlled exploration; related articles ranked by shared-tag strength; semantic tag search via **ONNX all-MiniLM-L6-v2** (384-dim real ML embeddings, self-hosted) |
| :lock: | **E2E Encryption** | AES-256-GCM with per-article and per-image keys, Argon2id KDF (64 MB, 3 iterations), envelope encryption with 3-level key hierarchy |
| :rotating_light: | **Online DEK Rotation** | Rotate the master encryption key without exporting/re-importing your vault. Single-transaction re-wrap of all article keys, automatic pre-rotation snapshot, peer-acceptance protocol so multi-node networks roll over together (auto-accept toggle per peer). Lazy slot rewrap migrates each user's password slot transparently on next login |
| :floppy_disk: | **Snapshot & Restore** | One-click encrypted snapshots (full DB + media), upload to restore on any node, network-wide restore propagates via signed sync event with per-peer auto-accept toggle. Pre-rotation backups created automatically before destructive operations |
| :arrows_counterclockwise: | **Multi-Node Sync** | Event sourcing, Lamport clocks, Ed25519-signed events, near-realtime push-on-save sync between public nodes, works behind NAT |
| :framed_picture: | **Encrypted Images** | Drag & drop, paste, or upload images in the editor — encrypted with per-image keys, decrypted on the fly |
| :globe_with_meridians: | **Web UI** | Dark theme, Markdown editor (EasyMDE), folder tree, tag management, activity feed |
| :iphone: | **Mobile App** | .NET MAUI, biometric unlock, offline-first — **Android available now; iOS coming** |
| :keyboard: | **CLI** | `bmb` command-line tool for init, join, unlock, article management, snapshots |
| :jigsaw: | **REST API** | 21 endpoint groups, OpenAPI support, agent bearer auth with auto-unlock |
| :file_zip: | **Data Export** | Download folders or articles as ZIP archives with all attached images |
| :wastebasket: | **Hard Delete** | Superadmin-only permanent purge of articles/folders and their media, propagated to every synced node (no recovery) |
| :busts_in_silhouette: | **Multi-User Auth** | Role-based access (superadmin, user), per-user key slots, team-ready |
| :closed_lock_with_key: | **Folder Access Control** | Per-folder ACL for users and AI agents independently, prevents horizontal privilege escalation |
| :clock9: | **Version History** | Encrypted article version history, inline diff viewer, who-changed tracking, fullscreen dialog |
| :shield: | **Audit Log** | Every operation tracked with actor type (web/agent/cli), node identity, timestamps |
| :ghost: | **Invisible Mode** | Node can hide itself from sync partners while still pulling events |
| :satellite: | **Event Relay (Gossip)** | Nodes push all events, not just their own — faster convergence across the network |
| :paperclip: | **Orphan Media Linking** | Automatic image linking on save — fixes images uploaded before article creation |

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
| **Read** | `bee_list_articles`, `bee_get_article`, `bee_get_tree`, `bee_get_image` | Browse folders, read article content, and view embedded images (auto-decrypted) |
| **Write** | `bee_save_article`, `bee_update_article`, `bee_delete_article`, `bee_append_to_article`, `bee_prepend_to_article`, `bee_move_folder`, `bee_delete_folder` | Full CRUD with soft-delete and folder management |
| **Tags** | `bee_get_related`, `bee_search_by_tag`, `bee_list_tags`, `bee_add_tags`, `bee_remove_tag`, `bee_rename_tag`, `bee_merge_tags`, `bee_delete_tag` | Categorization via tags, semantic search, and global tag management |
| **Session** | `bee_set_max_tokens`, `bee_continue` | Control response size, paginate large responses |
| **Upload** | `bee_get_upload_script` | Get a Python script for zero-context file uploads (bypasses LLM context window) |
| **Audit** | `bee_get_log` | Query activity log with filters by article, event type, pagination |

### Example Workflows

**Save a meeting in seconds:**
```
You: "Save our meeting notes to bee"

Agent calls: bee_save_article(title: "Team Sync 2026-04-08", treePath: "/Work/Meetings", ...)
→ Saved, encrypted, synced to all your nodes.
```

**Generate project documentation autonomously:**
```
You: "Analyze my Galaxy Tetris project and write full documentation to bee
      under /Projects. Follow the documentation guidelines from /Instructions."

Agent reads:  bee_get_article("Documentation Guidelines")
Agent scans:  your codebase
Agent writes: bee_save_article(title: "Galaxy Tetris — Architecture", treePath: "/Projects/Galaxy Tetris")
              bee_save_article(title: "Galaxy Tetris — API Reference", ...)
              bee_save_article(title: "Galaxy Tetris — Setup Guide", ...)
→ 15 minutes later: complete, structured documentation — written once, never lost.
```

### Your Knowledge, Everywhere — Encrypted

Once saved, your articles don't sit on one server. Spin up a node on your laptop, phone, or a VPS on another continent — everything syncs automatically, encrypted end-to-end with your master key.

- **Public API URL?** Sync is near-instant — push-on-save, seconds after every write.
- **Phone in your pocket?** Background polling kicks in — every 5 seconds when active, up to 5–10 minutes in deep sleep.
- **Three nodes on three continents?** Sleep well. Your knowledge survives anything.

No cloud service has your keys. No provider can read your notes. The encryption happens on your device — always.

### Zero-Context Upload

Normally, asking an AI agent to upload a large file means the file gets read into the LLM context window — wasting thousands of tokens just to pass it through.

BeeMemoryBank solves this with `bee_get_upload_script`: the agent calls the tool, receives a self-contained Python script, saves it to disk, and runs it. The file goes **directly from disk to the server** — the LLM never sees the content.

```
You: "Upload ./architecture.pdf to /Work/Docs"

Agent calls: bee_get_upload_script()
→ Returns a ready-to-run Python script

Agent runs: python bmb-upload.py --url https://your-server.example.com --bearer bee_xxx create ./architecture.pdf "Architecture" /Work/Docs
→ File uploaded. 0 tokens spent on file content.
```

---

## :rocket: Quick Start

### Docker (recommended)

```bash
# 1. Clone the repository
git clone https://github.com/ultrathinker/BeeMemoryBank.git
cd BeeMemoryBank

# 2. Download the ONNX model for semantic search (87 MB, required)
mkdir -p data
curl -L -o data/model.onnx "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"

# 3. Build and start (API on :5300, Web UI on :5301)
docker compose up -d --build

# 4. Check health
curl -f http://localhost:5300/health
```

Open `http://localhost:5301` in your browser and log in with your master password.

Data is stored in `./data` on the host (including `model.onnx`). To customize ports, copy `.env.example` to `.env` and edit as needed.

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
./publish/cli/bmb join --remote https://first-node.example.com --password "your-master-password" --name "VPS-Node" --data /var/lib/beememorybank
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
│   REST Endpoints (21 groups)  │  MCP Server (/mcp)      │
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
| **Native MCP** | :white_check_mark: 36 tools | :x: | :x: | :x: | :x: | :x: |
| **AI Agent Ready** | :white_check_mark: | :x: (plugin) | :x: | :x: | :x: | :x: |
| **Auto-Backlinks** | :white_check_mark: via tag graph | :white_check_mark: manual `[[links]]` | :white_check_mark: manual | :white_check_mark: manual | :x: | :x: |
| **Mobile App** | :white_check_mark: Android | :white_check_mark: | :white_check_mark: | :x: (PWA) | :white_check_mark: | :white_check_mark: |
| **Offline-First** | :white_check_mark: | :white_check_mark: | :white_check_mark: | :white_check_mark: | :x: | :white_check_mark: |
| **Self-Hosted** | :white_check_mark: | N/A (local) | :white_check_mark: | :white_check_mark: | :white_check_mark: | :white_check_mark: |
| **Version History** | :white_check_mark: Encrypted | :x: | :x: | :x: | :x: | :x: |
| **License** | AGPL-3.0 | Proprietary | AGPL-3.0 | AGPL-3.0 | AGPL-3.0 | AGPL-3.0 |
| **Stack** | .NET 10, SQLite | Electron | Go, SQLite | Node.js | Node.js | Node.js |

---

## :no_entry: What This Is NOT

To set expectations and help you decide if BeeMemoryBank fits your workflow:

- **Not a Notion replacement** — no real-time collaboration, no databases/views, no block editor. Markdown-first by design.
- **Not an Obsidian-style Zettelkasten** — no manual `[[wiki links]]`. Article connections emerge from shared concept tags instead, which is better for AI agents but a different mental model if you're coming from Obsidian.
- **Not a multi-tenant SaaS platform** — team vault with a trusted superadmin, not hostile-tenant isolation. See [Security Model](#shield-security-model) below.
- **Not cross-platform on mobile yet** — Android only today; **iOS coming**.
- **Not an enterprise-backed product** — single maintainer, actively developed. Bus factor is real; plan accordingly if you depend on it for critical data.

If these are dealbreakers, Obsidian / Logseq / AnyType / Notion may suit you better. If they aren't — read on.

---

## :world_map: Roadmap

- [x] E2E encryption with per-article keys
- [x] Multi-node sync with event sourcing and near-realtime push-on-save
- [x] Native MCP server (36 tools)
- [x] Web UI with Markdown editor
- [x] CLI tool (`bmb`)
- [x] Android app (.NET MAUI)
- [x] Agent bearer auth with auto-unlock
- [x] Activity audit log
- [x] Multi-user authentication with role-based access (superadmin, user)
- [x] Docker Compose deployment
- [x] Full-text search (article body, encrypted content)
- [x] Encrypted image storage with per-image keys (drag & drop, paste, upload)
- [x] Article version history with encrypted storage and inline diff viewer
- [x] Folder-level access control with per-folder ACL
- [x] Invisible mode for node synchronization
- [x] Orphan Media Linking (automatic image linking on save)
- [x] Data Export (ZIP archives for articles and folders)
- [x] Obsidian vault import (ZIP upload with images)
- [x] Hard delete with cross-node propagation (Superadmin)
- [x] Emergent concept-tag knowledge graph (D3.js force-directed, automatic bidirectional connections, no manual wiki-links)
- [x] Semantic search powered by ONNX all-MiniLM-L6-v2 (384-dim real ML embeddings, self-hosted)
- [ ] CI/CD pipeline (GitHub Actions)
- [ ] iOS app (coming)

---

## :handshake: Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on how to get started.

---

## :lock: Security

BeeMemoryBank uses a defense-in-depth approach:

- **AES-256-GCM** authenticated encryption for all article content
- **Argon2id** key derivation (64 MiB / parallelism=4 / 3 iterations — OWASP-recommended)
- **Per-article and per-media DEKs** for cryptographic isolation
- **Encrypted node identity** — the Ed25519 private key used to sign sync events is wrapped under the Master DEK, so a stolen DB file alone cannot be used to impersonate the node
- **HKDF-derived agent keys** — per-agent random salt; stealing one key does not enable precomputation against any other agent
- **Ed25519** signatures on all sync events (tamper-proof, with replay-shield against pre-restore zombie events)
- **Master DEK** lives only in RAM, wiped on process shutdown
- **Online DEK rotation** — change the master key without exporting/re-importing your vault; auto-accept across the cluster
- **Rate limiting** on authentication endpoints (brute-force protection)
- **Sentinel verification** ensures key compatibility across nodes
- **Folder-level access control** — per-folder ACL prevents horizontal privilege escalation between users and AI agents (enforced at the **repository** layer, not just at endpoints)
- **`TreePathCanonicalizer`** rejects `../` / control-char paths at every write entry point and at sync apply (poison-event defence)
- **Mobile hardening** — `FLAG_SECURE` on the Android activity (no screenshots / recent-apps previews of decrypted content), auto-lock on background, debug-only intent extras stripped from Release builds
- **Web hardening** — Content-Security-Policy + `X-Frame-Options: DENY`, `Secure` auth cookie, DOMPurify-sanitised Markdown rendering
- **Comprehensive audit log** — DEK rotation, snapshot lifecycle, user CRUD, agent lifecycle, admin password resets all leave a tamper-evident trail

The codebase has passed **7 sequential security audit waves** (crypto / sync / auth & multi-tenancy / input surfaces / admin features / pre-publish hygiene / mobile) with all findings either fixed or explicitly documented as accepted-risk. Full audit + fix history is in the project's [CHANGELOG](CHANGELOG.md).

For responsible disclosure, please see [SECURITY.md](SECURITY.md).

---

## :shield: Security Model

BeeMemoryBank is a **team vault**, not a zero-trust multi-tenant platform. Understanding this distinction is critical before deploying it for a group.

### Trust Model

One BeeMemoryBank vault uses a single **Master Data Encryption Key (Master DEK)**, derived from the superadmin's master password via Argon2id. This key lives in the API process's memory while the vault is unlocked.

| Role | Cryptographic Access | What They Can Do |
|---|---|---|
| **Superadmin** | Owns the Master DEK (derived from their password) | Unlock/lock the vault, create users and agents, access everything |
| **Regular User** | No independent key slot — rides on the superadmin's unlocked session | Read/write within folder ACL boundaries set by the superadmin |
| **Agent** | Master DEK wrapped with its own API key | Inherits the folder ACL of its owning user |

Regular users are **ACL-restricted guests** on top of the superadmin's unlocked session. Their access is enforced by application-layer folder ACLs, not by cryptography. They do not have their own key slot.

### Node Topology

- **Primary node** — where users and agents are created, where people log in, and where ACLs are managed.
- **Replica nodes** (mobile device, tablet, personal laptop, backup server) — superadmin-only. They exist purely to duplicate data across physical locations. Regular users are never created on replica nodes.
- **Sharing** with another person means creating a user account on the primary node — not giving them a node of their own.

All inter-node sync is encrypted end-to-end with Ed25519-signed events. Replica nodes cannot join without superadmin access during setup.

### Threat Model — Who This Tool Is For

| Intended Use | Not Designed For |
|---|---|
| Individuals keeping a personal knowledge base across several devices | Corporate multi-tenant isolation between departments |
| Families and small teams (up to ~20 users) where the superadmin is trusted | Hostile multi-tenant scenarios where a regular user has SQLite access or RAM access on the server |

### What This Means in Practice

- Folder ACLs are enforced **only by the API process**. A user who bypasses the API (e.g., direct SQLite read, RAM dump) can read everything — ACL is app-layer, not cryptographic.
- Regular users **cannot decrypt data** without the superadmin having unlocked the node first.
- All sync traffic is end-to-end encrypted; replica nodes cannot join without superadmin credentials during setup.

---

## :page_facing_up: License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).

For commercial licensing inquiries, please contact: **universeissilent42@gmail.com**

---

## :pray: Acknowledgments

Built with these excellent open-source projects:

- [.NET](https://dotnet.microsoft.com/) and [ASP.NET Core](https://github.com/dotnet/aspnetcore)
- [SQLite](https://sqlite.org/) via [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore)
- [Dapper](https://github.com/DapperLib/Dapper) micro-ORM
- [BouncyCastle](https://www.bouncycastle.org/) for Ed25519 signatures
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) for Argon2id
- [ModelContextProtocol SDK](https://github.com/modelcontextprotocol/csharp-sdk) for MCP server
- [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) for local ONNX inference
- [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) by sentence-transformers (Apache 2.0) for semantic embeddings
- [EasyMDE](https://github.com/Ionaru/easy-markdown-editor) Markdown editor
- [Shoelace](https://shoelace.style/) web components
- [Tagify](https://github.com/yairEO/tagify) tag input
