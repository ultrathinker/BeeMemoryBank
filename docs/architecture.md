# Architecture Overview

## What is BeeMemoryBank

BeeMemoryBank is a knowledge base for individuals and teams, with end-to-end encryption, multi-user access control, and native AI agent integration. It runs on multiple devices simultaneously and synchronizes data between them automatically.

**Problem:** Notes, documentation, and technical decisions are scattered across dozens of places — Notion, Google Docs, local files, chat apps. Nothing is searchable, nothing is protected, and everything depends on third-party servers.

**Solution:** A self-hosted knowledge base that:
- **Encrypts everything** — AES-256-GCM per article; even if the database file leaks, article texts are unreadable without the master password
- **Synchronizes** — multiple nodes (home server, VPS, laptop) exchange events, work autonomously, and merge without conflicts
- **Integrates with AI** — a built-in MCP server lets AI agents (Claude Code, etc.) read, write, and search articles as if it were a regular knowledge base
- **Works everywhere** — Web UI in the browser, CLI in the terminal, mobile app on Android
- **Fully under your control** — self-hosted, no SaaS dependency, data lives only on your servers

**Target audience:** Individuals, small teams, and companies who want a single place for all their information — from project documentation to team knowledge base — with encryption and no cloud lock-in. Multi-user with role-based access control (superadmin, user), per-user folder restrictions, and per-user AI agent connections via MCP.

## Technology Stack

| Category | Technology | Rationale |
|---|---|---|
| Language | C# / .NET 10.0 | Unified stack with other projects |
| API | ASP.NET Core Minimal APIs | Lightweight, no controllers, sufficient for REST + MCP |
| Web UI | Razor Pages | Server-side rendering, simple deployment |
| Database | SQLite | Embedded, zero-config, `VACUUM INTO` for snapshots |
| ORM | Dapper | Explicit SQL, full control, micro-ORM |
| Body encryption | AES-256-GCM | Authenticated encryption — both confidentiality and integrity |
| Event signatures | Ed25519 (BouncyCastle) | Fast signatures, small keys (32 bytes), tamper-proof sync |
| KDF | Argon2id (Konscious) | PHC winner, memory-hard — resistant to GPU brute-force |
| MCP | ModelContextProtocol.AspNetCore v1.0.0 | Standard protocol for AI integration |
| CLI | System.CommandLine v2.0.0-beta4 | Typed commands with parsing |
| UI | Shoelace 2 + EasyMDE + markdown-it + Tagify | Web components + Markdown editor/renderer + tag input |
| Tests | xUnit + WebApplicationFactory | Unit + integration tests |

## Building and Running

```bash
# Build (requires .NET 10 SDK)
dotnet publish server/BeeMemoryBank.Api/ -c Release -o publish/api
dotnet publish server/BeeMemoryBank.Web/ -c Release -o publish/web
dotnet publish server/BeeMemoryBank.Cli/ -c Release -o publish/cli

# Option A: New network
bmb init --data /var/lib/beememorybank --name "MyNode" --password "..."

# Option B: Join an existing network
bmb join --remote https://bmb.example.com --password "..." --name "MyNode" --data /var/lib/beememorybank

# Run (two processes)
BMB_DATA_PATH=/var/lib/beememorybank ASPNETCORE_URLS=http://localhost:5300 ./BeeMemoryBank.Api
BMB_API_URL=http://localhost:5300 ASPNETCORE_URLS=http://localhost:5301 ./BeeMemoryBank.Web
```

## Module Structure

```
server/
├── BeeMemoryBank.Api/       — REST API (24 endpoint groups) + MCP server
│   ├── Endpoints/           — 24 files: Activity, Admin, Agent, Article, Comment, ConceptTag, DekRotation, Download, Folder, HardDelete, Init, Join, Key, Media, ObsidianImport, Restriction, Search, Session, Snapshot, Sync, Tree, User, Version, Whitelist
│   ├── McpTools/            — 7 tool groups: Search (3 tools), Read (7), Write (10), Session (3), Upload (2), Audit (2), Concept (9) — 36 tools total
│   ├── Middleware/           — AgentAuthMiddleware (bearer → auto-unlock)
│   ├── Services/            — SyncTokenStore, SnapshotService, HttpActorProvider, DekRotationService, LazySlotRewrapService
│   └── Models/              — DTOs for endpoints
│
├── BeeMemoryBank.Web/       — Razor Pages Web UI (stateless proxy to API)
│   ├── Pages/               — Login, Tree, Folder, Article/View, Article/Edit, Admin, Search, Activity, Users, Lock
│   └── Services/ApiClient   — HTTP client to API (folder download, invisible mode)
│
├── BeeMemoryBank.Cli/       — CLI (bmb)
│   ├── Commands/            — init, join, status, unlock, article, snapshot, agent
│   └── CliActorProvider     — IActorProvider for CLI (actor_type = "cli")
│
libs/
├── BeeMemoryBank.Core/      — Models, services, interfaces (no external dependencies)
│   ├── Models/              — Article, Comment, Agent, Folder, FolderInfo, Media, NodeIdentity, AuditLog,
│   │                          ArticleVersion, FolderAclEntry...
│   │                          (models in BeeMemoryBank.Core/Models/)
│   ├── Interfaces/          — 29 interfaces (incl. IMediaRepository, IFolderRepository, IArticleVersionRepository, IFolderAclRepository, IActorProvider, IEmbeddingGenerator, ISyncTrigger, ISyncPushPositionRepository, IDekRotationApplier, IDekRotationStateRepository, ILazySlotRewrapService, IAuditLogRepository)
│   └── Embeddings/          — HashBasedEmbeddingGenerator, ProjectionMatrix
│
├── BeeMemoryBank.Crypto/    — Cryptographic primitives (~450 LOC)
│   └── AesGcmHelper, MasterKeyManager, DekManager, Ed25519Signer, KeyDerivation, ArticleEncryptor,
│       MediaEncryptor, AgentKeyHelper, SecureRandom, CryptoConstants
│
├── BeeMemoryBank.Storage/   — SQLite + Dapper
│   ├── Sqlite/              — 22 repositories (incl. MediaRepository, FolderRepository, FolderBootstrapper, ArticleVersionRepository, FolderAclRepository, SyncPushPositionRepository, DekRotationStateRepository, AuditLogRepository) + MigrationRunner
│   └── Migrations/          — `001_initial_schema.sql` (consolidated, 23 CREATE TABLE)
│
└── BeeMemoryBank.Sync/      — Distributed synchronization
    └── SyncScheduler, SyncTrigger, SyncClient, EventLogger, EventApplier, LamportClock,
        ConflictResolver, CleanupService, PendingEmbeddingProcessor, EventPayloads, EventSignature

tests/
├── BeeMemoryBank.Core.Tests/       — 7 files (Article, Session, KeyManagement, TreeSearch, Initialization, Embedding)
├── BeeMemoryBank.Crypto.Tests/     — 5 files (ArticleEncryptor, DekManager, Ed25519, KeyDerivation, MasterKey)
├── BeeMemoryBank.Storage.Tests/    — 2 files (Migrations, Schema)
├── BeeMemoryBank.Sync.Tests/       — 5 files (ConflictResolver, EventApplier, EventLogger, LamportClock, Fixture)
├── BeeMemoryBank.Cli.Tests/        — 1 file
├── BeeMemoryBank.Integration.Tests/— 5 files (API, MCP, TwoNodeSync, Whitelist, WebApplicationFactory)
└── BeeMemoryBank.Migrator.Tests/   — 1 file

tools/
└── BeeMemoryBank.Migrator/         — CLI for migrating from external formats (bmb-migrate)

mobile/
└── BeeMemoryBank.Mobile/           — .NET MAUI Android client
```

## Module Dependencies (unidirectional)

```
Core ← Storage
Core ← Crypto
Core, Storage, Crypto ← Sync
Core, Storage, Crypto, Sync ← Api
Core, Storage, Sync ← Cli
(Web has no dependencies on other modules — HTTP calls to Api only)
```

No circular dependencies. Core is the kernel with no external dependencies.

## Node Topology

Nodes are physical copies of the data across different locations and devices — not separate login sites for different people.

**Primary node (public site)** is where the system lives: regular users and agents are created here, and this is where people log in day-to-day. There is typically one primary node per network, hosted on a server or VPS that is always online.

**Replica nodes** (mobile phone, tablet, personal desktop, backup server) are superadmin-only, accessed via the master password. They exist purely to duplicate data in additional physical locations for availability and offline access. Regular users are never created on replica nodes and do not log in there.

**Sharing with another person** means creating another user account on the primary site node — not handing them a node of their own. Every team member connects to the same primary node; every device syncs a full copy of the data.

**Users, agents, and folder ACL restrictions are node-local.** They are created on the node where they belong and are not propagated through the event stream. A user created on the primary node does not exist on a replica, and vice versa.

This is a deliberate design choice. It rules out partial-sync topologies (a worker node syncing only `/Public`) and cross-node user provisioning. For small teams and families, the gain in simplicity outweighs the flexibility lost.

## Key Architectural Decisions

| Decision | Rationale |
|---|---|
| SQLite, not PostgreSQL | Embedded, no separate server, `VACUUM INTO` for atomic snapshots |
| Per-article DEK | Isolation: compromising one DEK does not expose the rest |
| Lamport clocks, not HLC | Sufficient for causal ordering; HLC adds complexity without benefit for this use case |
| Pull-sync with push-on-save | Pull-based works behind NAT. Push-on-save (SyncTrigger) signals immediate sync after every write, reducing delay from 60s to near-instant for public nodes |
| Metadata in plaintext | Search and navigation without decryption. Trade-off: a database leak reveals structure but not content |
| Per-media DEK | Images use the same envelope encryption as articles — per-file random DEK, wrapped by Master DEK. Encrypted .enc files stored on disk (not in SQLite) to avoid bloating the database |
| Concept tags only | Articles have concept tags only (`tbl_concept_tag` + `tbl_article_concept_tag`). Legacy keyword tags (`tbl_tag` + `tbl_article_tag`) were removed in an earlier schema migration |
| Batched content search | Full-text body search decrypts articles in batches of 50, not all at once — controls memory usage at scale |
| Two processes (API + Web) | Web is a stateless proxy, can be replaced or removed. API is the sole data owner |
| Sentinel value | AES-GCM("BeeMemoryBank", masterDEK) — verifies DEK compatibility before synchronization |
| Event actor tracking | Every event is tagged with actor_type (web/agent/cli) for auditing; `via_agent_name` records which agent initiated the request (NULL for direct human actions) |
| Agent ownership | Every agent has `owner_user_id NOT NULL` (FK → tbl_user, ON DELETE RESTRICT). Folder restrictions are stored per-user only; agents inherit the owner's restrictions. MCP ACL calls use `owner_user_id`, not `agent_id`. |
| tbl_folder instead of tree_path parsing | Folders as first-class entities with CRUD, synchronization, and Lamport timestamps |
| Multi-user with key slots | Each user's password wraps the Master DEK as a separate key slot. Adding a user doesn't re-encrypt articles |
| Role-based access (superadmin/user) | Superadmins manage users, unlock the vault, and have full control; regular users can only log in when the vault is already unlocked by a superadmin or agent |
| Centralized Media ACL | Access to images (`/api/media/{id}`) automatically checked against the ACL of the article they are linked to |
| Orphan Media Auto-linking | On article save, any referenced images are automatically linked to the article in the database for proper ACL enforcement and sync |
| DEK rotation | Replace the Master DEK across all nodes. Destructive single-transaction re-wrap of all encrypted_dek columns. Peer-acceptance model (auto-accept per-whitelist toggle). Lazy slot rewrap for surviving key slots. State machine: Proposed → Committing → Applied / Cancelled / Failed / Rejected |

## New Models & Services

### Models

| Model | Fields | Purpose |
|---|---|---|
| **ArticleVersion** | `Id`, `ArticleId`, `VersionNumber`, `Title`, `TreePath`, `Ciphertext`, `IV`, `EncryptedDek`, `DekIV`, `UpdatedBy`, `CreatedAt` | Encrypted version history entry — each save creates a new version with its own DEK and encrypted body |
| **FolderAclEntry** | `Id`, `UserId`, `FolderId`, `Effect` (Allow/Deny), `CreatedAt` | Per-folder ACL entry — each row is self-describing (allow or deny). Mixing allow+deny rows for one user is allowed (deny wins) |

### Services

| Service | Purpose |
|---|---|
| **FolderAccessService** | Enforces per-folder access control for users and agents. Resolves deny/allow paths from `tbl_folder_acl_entry`, provides `IsAccessDenied()` and `FilterArticles()`/`FilterFolders()` helpers used by all MCP tools and API endpoints. Deny wins over allow. No rows = no restrictions |
| **TreePathCanonicalizer** (in `BeeMemoryBank.Core.Services`) | Single source of truth for folder-path normalisation. `Canonicalize` collapses `//`, strips trailing `/`, rejects `..` / `.` / control chars / NUL. Wired into `FolderService.NormalizePath`, `ArticleService.CreateAsync/UpdateAsync`, and the `EventApplier` payload validation gate. Cosmetic non-canonical input is allowed at sync apply (forward compat with pre-canonicalisation peers); strictly illegal segments are silently dropped |
| **InvisibleModeService** | Controls whether this node is visible to sync partners. When invisible, the node does not advertise itself during sync handshakes, useful for maintenance or testing without affecting the sync network |
| **HardDeleteService** (in `BeeMemoryBank.Sync`) | Permanently purges articles or folder subtrees (rows + media files on disk) inside a single SQLite transaction, then — after commit — writes a `hard_delete` sync event so the purge propagates to every subscriber. Post-commit event logging avoids the SQLite write-lock that event logging inside the outer transaction would cause |
| **ObsidianImportService** (in `BeeMemoryBank.Core`) | Parses an Obsidian vault ZIP stream: strips frontmatter, normalizes Windows backslash paths, skips `.obsidian/` config, rewrites `![[image.png]]` / `![alt](image.png)` embeds to encrypted media URLs, and creates one article per `.md` file. Per-article errors are isolated and surfaced in the import report |
| **DekRotationService** (in `BeeMemoryBank.Api`) | Orchestrates DEK rotation: Propose (generate new DEK, emit sync events) → Accept (pre-rotation snapshot, destructive re-wrap of all DEKs in a single transaction, update sentinel + epoch). Also handles auto-accept for peer rotations and retry of deferred auto-accepts after unlock. Implements `IDekRotationApplier` |
| **LazySlotRewrapService** (in `BeeMemoryBank.Api`) | Walks the chain of Applied DEK rotations to transparently re-wrap a user's key slot when their slot was wrapped with a previous DEK. Triggered automatically on unlock when sentinel mismatch is detected |
