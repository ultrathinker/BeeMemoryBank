# Architecture Overview

## What is BeeMemoryBank

BeeMemoryBank is a personal knowledge base with end-to-end encryption that runs on multiple devices simultaneously and synchronizes data between them automatically.

**Problem:** Notes, documentation, and technical decisions are scattered across dozens of places — Notion, Google Docs, local files, chat apps. Nothing is searchable, nothing is protected, and everything depends on third-party servers.

**Solution:** A self-hosted knowledge base that:
- **Encrypts everything** — AES-256-GCM per article; even if the database file leaks, article texts are unreadable without the master password
- **Synchronizes** — multiple nodes (home server, VPS, laptop) exchange events, work autonomously, and merge without conflicts
- **Integrates with AI** — a built-in MCP server lets AI agents (Claude Code, etc.) read, write, and search articles as if it were a regular knowledge base
- **Works everywhere** — Web UI in the browser, CLI in the terminal, mobile app on Android
- **Fully under your control** — self-hosted, no SaaS dependency, data lives only on your servers

**Target audience:** Developers and technical teams who want a single place for all their information — from project documentation to personal notes — with encryption and no cloud lock-in. Supports multi-user access with role-based authentication (superadmin, unlocker, user).

## Technology Stack

| Category | Technology | Rationale |
|---|---|---|
| Language | C# / .NET 10.0 | Unified stack with other projects |
| API | ASP.NET Core Minimal APIs | Lightweight, no controllers, sufficient for REST + MCP |
| Web UI | Razor Pages | Server-side rendering, simple deployment |
| Database | SQLite | Embedded, zero-config, `VACUUM INTO` for snapshots |
| ORM | Dapper | Explicit SQL, full control, micro-ORM |
| Body encryption | AES-256-GCM | Authenticated encryption — both confidentiality and integrity |
| Event signatures | Ed25519 (NSec v25.4) | Fast signatures, small keys (32 bytes), tamper-proof sync |
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
├── BeeMemoryBank.Api/       — REST API (15 endpoint groups) + MCP server
│   ├── Endpoints/           — 16 files: Article, Sync, Agent, Join, Folder, Deploy, User...
│   ├── McpTools/            — 6 tool groups: Search (2 tools), Read (5), Write (7), Session (2), Upload (1), Audit (1) — 20 tools total
│   ├── Middleware/           — AgentAuthMiddleware (bearer → auto-unlock)
│   ├── Services/            — SyncTokenStore, SnapshotService, HttpActorProvider
│   └── Models/              — DTOs for endpoints
│
├── BeeMemoryBank.Web/       — Razor Pages Web UI (stateless proxy to API)
│   ├── Pages/               — Login, Tree, Folder, Article/View, Article/Edit, Admin, Search, Activity, Users, Lock
│   └── Services/ApiClient   — HTTP client to API (folder download, deploy)
│
├── BeeMemoryBank.Cli/       — CLI (bmb)
│   ├── Commands/            — init, join, status, unlock, article, snapshot
│   └── CliActorProvider     — IActorProvider for CLI (actor_type = "cli")
│
libs/
├── BeeMemoryBank.Core/      — Models, services, interfaces (no external dependencies)
│   ├── Models/              — Article, Comment, Agent, Folder, FolderInfo, Media, NodeIdentity, AuditLog,
│   │                          ArticleVersion, FolderRestriction...
│   ├── Services/            — ArticleService, SessionService, SearchService, TreeService, FolderService,
│   │                          MediaService, InitializationService, KeyManagementService, EmbeddingProjectionService, UserService,
│   │                          FolderAccessService, InvisibleModeService
│   ├── Interfaces/          — 23 interfaces (incl. IMediaRepository, IFolderRepository, IArticleVersionRepository, IFolderRestrictionRepository, IActorProvider, IEmbeddingGenerator, ISyncTrigger, ISyncPushPositionRepository)
│   └── Embeddings/          — HashBasedEmbeddingGenerator, ProjectionMatrix
│
├── BeeMemoryBank.Crypto/    — Cryptographic primitives (~450 LOC)
│   └── AesGcmHelper, MasterKeyManager, DekManager, Ed25519Signer, KeyDerivation, ArticleEncryptor,
│       MediaEncryptor, AgentKeyHelper, SecureRandom, CryptoConstants
│
├── BeeMemoryBank.Storage/   — SQLite + Dapper
│   ├── Sqlite/              — 19 repositories (incl. MediaRepository, FolderRepository, FolderBootstrapper, ArticleVersionRepository, FolderRestrictionRepository, SyncPushPositionRepository) + MigrationRunner
│   └── Migrations/          — 001..019 SQL files (19 migrations)
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

## Key Architectural Decisions

| Decision | Rationale |
|---|---|
| SQLite, not PostgreSQL | Embedded, no separate server, `VACUUM INTO` for atomic snapshots |
| Per-article DEK | Isolation: compromising one DEK does not expose the rest |
| Lamport clocks, not HLC | Sufficient for causal ordering; HLC adds complexity without benefit for this use case |
| Pull-sync with push-on-save | Pull-based works behind NAT. Push-on-save (SyncTrigger) signals immediate sync after every write, reducing delay from 60s to near-instant for public nodes |
| Metadata in plaintext | Search and navigation without decryption. Trade-off: a database leak reveals structure but not content |
| Per-media DEK | Images use the same envelope encryption as articles — per-file random DEK, wrapped by Master DEK. Encrypted .enc files stored on disk (not in SQLite) to avoid bloating the database |
| Batched content search | Full-text body search decrypts articles in batches of 50, not all at once — controls memory usage at scale |
| Two processes (API + Web) | Web is a stateless proxy, can be replaced or removed. API is the sole data owner |
| Sentinel value | AES-GCM("BeeMemoryBank", masterDEK) — verifies DEK compatibility before synchronization |
| Event actor tracking | Every event is tagged with actor_type (web/agent/cli) for auditing |
| tbl_folder instead of tree_path parsing | Folders as first-class entities with CRUD, synchronization, and Lamport timestamps |
| Multi-user with key slots | Each user's password wraps the Master DEK as a separate key slot. Adding a user doesn't re-encrypt articles |
| Role-based access (superadmin/unlocker/user) | Superadmins manage users and unlock; unlockers can only unlock; users can only log in when already unlocked |

## New Models & Services

### Models

| Model | Fields | Purpose |
|---|---|---|
| **ArticleVersion** | `Id`, `ArticleId`, `VersionNumber`, `Title`, `Tags`, `TreePath`, `Ciphertext`, `IV`, `EncryptedDek`, `DekIV`, `UpdatedBy`, `CreatedAt` | Encrypted version history entry — each save creates a new version with its own DEK and encrypted body |
| **FolderRestriction** | `Id`, `UserId`, `AgentId`, `FolderId`, `CreatedAt` | Per-folder ACL entry — restricts a specific user or agent from accessing a folder and its subtree |

### Services

| Service | Purpose |
|---|---|
| **FolderAccessService** | Enforces per-folder access restrictions for users and agents. Resolves restricted paths from `tbl_folder_restrictions`, provides `IsPathRestricted()` and `FilterArticles()`/`FilterFolders()` helpers used by all MCP tools and API endpoints |
| **InvisibleModeService** | Controls whether this node is visible to sync partners. When invisible, the node does not advertise itself during sync handshakes, useful for maintenance or testing without affecting the sync network |
