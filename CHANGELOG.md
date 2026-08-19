# Changelog

All notable changes to BeeMemoryBank will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security Hardening (Pre-GitHub Audit, 6 waves + mobile)

#### Crypto / Key Management
- **Encrypted node identity (v=1):** `tbl_node_identity.ed25519_private_key` now stored AES-256-GCM-wrapped under master DEK. Fresh nodes start at v=1; legacy v=0 nodes auto-upgrade on next unlock via `UpgradePrivateKeyToV1Async`. Mobile `NodeSetupService.JoinAsync` also creates v=1 identities.
- **HKDF-derived agent keys (v=1):** New agents use HKDF-SHA256 with per-agent random salt instead of plain SHA256. `kdf_version` column in `tbl_agent` dispatches; legacy v=0 agents still authenticate. Stops cross-agent precomputation if database is exfiltrated.
- **Argon2 defaults:** 64 MiB / parallelism=4 / iterations=3 (revert from earlier reduced settings — original passwords keep verifying).

#### Sync
- **Lamport clock saturation:** `Update(remoteTs)` clamps forward jumps to `MaxJump = 10_000_000` and uses saturating add so a peer can't lock the local clock at `long.MaxValue`.
- **Tombstone LWW:** `INSERT … ON CONFLICT … WHERE excluded.lamport_ts > existing` instead of naive `INSERT OR REPLACE`. Stops out-of-order delete events from overwriting strictly-newer tombstones.
- **`EventApplyResult` enum:** `Applied` / `SilentlyDropped` / `Skipped`. Pull/push loops advance their cursor past `SilentlyDropped` events so the same poison event isn't re-fetched forever.
- **Hard-delete audit table:** `tbl_hard_delete_audit` with `lamport_ts` per entity. Survives event-log compaction; gates against late updates from peers that didn't see the hard-delete.
- **`WhitelistAddPayload.IsSuperadmin`:** Closes 3+ node cluster split-brain — without it the bit was lost in transit, receivers stored the new peer as non-superadmin, then rejected its `hard_delete` / `restore_network` events forever.
- **`WhitelistRepository.UpdateAsync`** now writes `is_superadmin` (was missing — UI updates silently reset the bit).
- **Authorization gate:** `whitelist_*`, `hard_delete`, `restore_network` events from non-superadmin peers raise `UnauthorizedAccessException` on the receiver.
- **TreePath canonicalisation:** New `TreePathCanonicalizer` rejects `..` / `.` / control chars at write paths (FolderService, ArticleService, ObsidianImportService) and at sync apply (`EventApplier.IsTreePathPayloadValid`). Cosmetic non-canonical input (`//`, trailing `/`) passes through for compat with pre-canonicalisation peers.

#### Auth / Multi-tenancy
- **Legacy agent ACL fail-closed:** `CallerScopeMiddleware` now returns deny-all when an agent has `OwnerUserId == 0` (pre-migration-004). Previously empty ACL meant "see everything".
- **`/api/articles/{id}/content` ACL gate:** Endpoint pre-fetches metadata via scope-aware `GetMetadataAsync` and runs an explicit `IsAccessDenied` check. Previously a User-role caller knowing a GUID could pull plaintext for any article.
- **Snapshot endpoint role gates:** LIST / CREATE / DOWNLOAD now require `X-User-Role==Superadmin` (restore/upload/delete already did). Stops User-role disk DoS via repeated `VACUUM INTO` and exfil of encrypted DB blobs.
- **Init password complexity:** Both standalone init and JOIN paths now run `UserService.ValidatePassword` (8+ chars with upper/lower/digit) — previously the JOIN path accepted 6-char passwords with no complexity.

#### Web UI
- **Content-Security-Policy + security headers:** Added middleware emitting CSP (`default-src 'self'`, `frame-ancestors 'none'`), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`. CSP `connect-src` allows `data:` for Shoelace icon URIs; `style-src` allows `https://maxcdn.bootstrapcdn.com` for EasyMDE FontAwesome.
- **Auth cookie `SecurePolicy = Always`** (was `SameAsRequest`).
- **Folder picker autocomplete:** New reusable `bmbFolderPicker` (search input + Ajax `/api-proxy/folders/search` + dropdown with universal "/ (root)" option). Replaces the old radio-group "selected vs root" pattern across all create-folder / create-article dialogs and the move-folder dialog.
- **Compact path-selector breadcrumb on Article/Edit:** Path under the title is now clickable; opens a mini-dialog with the same picker. Hidden `treePath` input updates on Apply.
- **No-popup-for-new-article:** Sidebar and Folder-page "New Article" buttons jump straight to `/Article/Edit?treePath=…` — no popup. The user picks a different folder later via the breadcrumb.
- **Cancel button on every modal dialog** (17 dialogs across the UI).
- **`Folder.cshtml` onclick JS-string injection fix:** path interpolated directly into an `onclick` attribute could break out of the JS string via single quote in the folder name; switched to a JS variable reference.

#### Mobile (MAUI Android)
- **`FLAG_SECURE`** on `MainActivity`: blocks screenshots, screen recording, and recent-apps task-switcher previews of decrypted content.
- **Auto-lock on minimize:** `App.OnSleep` calls `_session.Lock()`; `OnResume` re-routes to `//unlock`. No more "snatch-and-run" full-vault access.
- **Intent-extra unlock bypass closed:** `bmb_init_password` / `bmb_unlock_password` extras in `MainActivity` are now wrapped in `#if DEBUG`. Release builds ignore them. (Maestro tests use the normal UI flow.)
- **Markdig `DisableHtml()`:** Article rendering pipeline now strips raw HTML at the markdown layer in addition to the WebView CSP `script-src 'none'`.
- **Mobile WebView CSP `img-src`** tightened from `*` to `data: blob:` (all images are inlined as data URIs).
- **Node identity v=1 on join:** Mobile `NodeSetupService.JoinAsync` wraps the Ed25519 private key with the master DEK before persist (server-side init was already correct).

#### MCP
- **`bee_get_log includeAdminEvents`:** Optional parameter that, when `true` AND the caller is superadmin, includes events without an `articleId` (whitelist_*, hard_delete, dek_rotation_*, restore_network, snapshot_checkpoint). Non-superadmin callers always get the article-only view.

#### Observability
- **Audit-log coverage:** Snapshot create/restore/upload/delete + user create/update/delete/admin-password-reset + agent create/delete now write to `tbl_audit_log`. Restore logs intent BEFORE applying so the row survives in the pre-restore state's last backup. DEK rotation already had coverage.

#### Tests
- **349/349 green** across Cli (16) + Core (106) + Crypto (22) + Storage (26) + Sync (44) + Integration (127) + Migrator (8). Added `TreePathCanonicalizerTests`, EventApplier admin-gate + payload-validation tests, MCP includeAdminEvents 3-state matrix.

### MCP Tool Fixes (External Client Compatibility & Access Control)

Filed by an external MCP client agent after real-world use against a populated vault; fixed as one batch.

- **`bee_get_log` folder-event scope leak:** `folder_create`/`folder_rename`/`folder_delete` events (which carry no `articleId`) now appear in the default (non-admin) log view like `article_*` events — closing a gap where the tool reported soft-deleted articles as indistinguishable "not found" while giving agents no way to audit folder deletions at all. The new visibility is gated by the same `IsAccessDenied(EntityId)` folder-scope ACL check every other read path enforces (`EntityId` is the folder path for these event types) — a scope-restricted agent still cannot see folder events outside its access grant.
- **`bee_list_tags` semantic-search pagination:** New `offset` parameter, threaded through both the plain substring filter and the semantic (`~query`) embedding-similarity search path. The semantic path now scope-filters candidate tags *before* ranking/windowing instead of after, so `offset` advances relative to what the caller can actually see — previously a scope-restricted caller's page could permanently skip tags it was allowed to see but that ranked just below the global top-N.
- **`bee_save_article`/`bee_update_article` tags schema:** `tags` changed from a nullable array (`List<string>? tags = null`) to a non-nullable optional array (`List<string> tags = null!`). Some MCP clients (Gemini's function-calling translation) reject the *entire* tool list when a parameter's generated JSON Schema is a `["array","null"]` union with no `items` constraint — this brings `tags` in line with the already-correct `bee_add_tags` schema. Runtime null-handling (omit vs. clear-with-`[]`) is unchanged.
- **`bee_get_article` soft-delete distinction:** Soft-deleted articles now return `"Error: article {id} was deleted"` instead of the same generic "not found" a nonexistent id gets, so an agent mirroring a tree can tell the two cases apart. Still folder-scope gated — a caller denied access to the article's folder sees "not found" either way.
- **Timestamp UTC normalization (`DapperConfig`):** The Dapper `DateTime`/`DateTime?` type handlers now coerce `DateTimeKind.Unspecified` (legacy/imported rows stored without a zone marker) to `Utc` on read, so JSON responses always carry a `Z`/offset suffix. Previously an agent computing "now minus 24h" against an offset-less `updatedAt` from older data could silently get zero results instead of an error.
- **`bee_replace_in_article` parameter-alias tolerance:** Coding-agent MCP clients (Claude Code, opencode, etc.) reflexively reach for their own file-edit tool's parameter names — `old_string`/`new_string`, `oldString`/`newString`, plus an incidental `filePath` — instead of this tool's `search`/`replace`. `McpParameterValidationMiddleware` now transparently rewrites those aliases (and drops the incidental extra parameter) before the unknown-parameter check runs, in addition to a description hint naming the real parameters.
- **Tests:** Added `Acl_BeeGetLog_FolderDeleteEvents_DeniesSecretFolder` (the folder-event scope leak above had zero prior coverage — the ACL test fixture wires `FolderService` with a `NullEventLogger`, so no existing test ever persisted a real folder event; verified this test fails against the pre-fix behavior and passes against the fix). Full-suite run: all previously-green tests remain green; pre-existing failures in `SnapshotService`/CLI snapshot tests (`IOException: file in use`, Windows temp-file locking) reproduce identically on the pre-fix code and are unrelated to this change.

#### MCP follow-up (2026-08-19): missing-required-parameter validation

The alias tolerance above had a blind spot: a call that supplies *only* aliased/dropped names and never
supplies a genuinely required parameter (e.g. `bee_replace_in_article` called with `filePath`/`oldString`/
`newString` and no `id` at all — an agent that fully confused this tool with its own local file-edit tool)
passed the "no unknown parameter names" check cleanly after alias rewriting, then failed deep inside the
MCP SDK's own parameter binder with an opaque `"An error occurred invoking {tool}"` instead of a message
naming the missing parameter. Before the alias feature existed this was caught by accident (the extra wrong
names always tripped the old "Unknown parameter(s)" error, which happened to list `id` as a side effect).

- **`McpParameterValidationMiddleware`** now also checks for missing *required* parameters — symmetric to
  the existing unknown-parameter check, using the same `McpToolRegistry.ParamInfo.Required` flag — for
  every registered tool, not just `bee_replace_in_article`. The error now names the missing parameter
  directly: `"Error: Missing required parameter(s): 'id' for bee_replace_in_article. ..."` (unknown and
  missing problems are combined into one message when both occur).
- **Omitted/null `arguments`:** a `tools/call` request with no `arguments` property at all, or an explicit
  `"arguments": null`, used to skip parameter validation entirely (early-return). It's now treated as "no
  parameters supplied" for the missing-required check instead of silently reaching the SDK's own opaque
  failure. A malformed `arguments` (present but not an object, e.g. an array or string) still falls through
  to the SDK untouched.
- **Audited every `[McpServerTool]` method** across all 7 `server/BeeMemoryBank.Api/McpTools/*.cs` files
  (32 tools, 69 parameters) for a required parameter missing `[Description]` (which would make it invisible
  to `McpToolRegistry` and thus to this check too) — none found; independently re-verified.
- **Tests:** New `McpParameterValidationMiddlewareTests` (7 cases) — constructs the middleware directly
  (its dependencies are plain constructor parameters, no DI host needed) and covers: missing required
  parameter, the exact aliased-call-without-`id` regression, omitted/null `arguments`, unknown-parameter-only,
  a fully valid call, and a zero-parameter tool. No prior test coverage existed for this middleware at all.

#### MCP follow-up (2026-08-19): invalid GUID parameter values

A second, distinct blind spot reported against the same tool surface: every article/media `id` parameter
across ~15 MCP tool methods is typed as `Guid`/`Guid?` directly in the C# method signature. A value that
isn't a valid GUID — most commonly a tree path (e.g. `/Projects/_Sync/_README`) passed by an agent that only
knows the article by the path named in its own instructions, not its GUID — was never caught by the
name-based checks above: the SDK's own JSON argument binder throws while coercing the value into
`System.Guid`, *after* the middleware's `next(context)` call, producing the same opaque
`"An error occurred invoking {tool}"` failure the missing-parameter fix closed for names but not values.

- **`McpToolRegistry.ParamInfo`** gained an `IsGuid` flag (`Nullable.GetUnderlyingType(type) ?? type ==
  typeof(Guid)`), so the middleware can identify which parameters need value-shape validation without
  re-deriving CLR type info itself.
- **`McpParameterValidationMiddleware`** now validates every `IsGuid` parameter's provided value with
  `Guid.TryParse` before invocation. A non-GUID string, or any non-string JSON value (number/array/object),
  is reported as `"Invalid parameter value(s): 'id' must be a GUID, got \"/Projects/_Sync/_README\""`, with a
  trailing hint: *"GUID parameters take article/media IDs only, never tree paths. Resolve a path to its GUID
  first via bee_get_tree or bee_search, then pass that GUID here."* JSON `null` is accepted as "no value" for
  optional `Guid?` parameters (their default), but reported as invalid for a *required* Guid parameter — a
  non-nullable `Guid` can never legitimately bind from `null`, so explicit `null` there is unparseable like
  any other bad value, not silently equivalent to omitting the field entirely (missed by the first draft;
  caught in an Antigravity review pass and fixed before shipping). Oversized values (e.g. an entire article
  body mistakenly passed as `id`) are truncated to 80 chars in the error text and the log line.
- **Tests:** Two new cases (non-GUID string value, explicit `null` on a required GUID) plus one confirming
  `null` still passes through cleanly for an optional `Guid?` parameter; test tool registry extended to cover
  `BeeReadTools`/`BeeSearchTools`/`BeeSessionTools`/`BeeAuditTools`/`BeeConceptTools` in addition to the two
  already covered, for full parity with the tools actually registered in `Program.cs`.
- **Known follow-up, not fixed here:** the same class of bug likely affects other non-string MCP parameter
  types bound directly by the SDK (`List<string> tags`, `bool`, `int`/`int?`) if a client sends a
  differently-shaped JSON value (e.g. a comma-separated string instead of an array). Flagged by the
  Antigravity review pass; deliberately out of scope for this change pending a separate decision on whether
  to generalize `McpParameterValidationMiddleware` to arbitrary JSON-Schema-shape checks.

### Added

- **Multiple Storages & Data Isolation (Windows Desktop):**
  - **Stable Data Root:** Relocated all user database files, media, logs, and settings to a persistent per-user directory (`%LOCALAPPDATA%\BeeMemoryBankData`) outside the versioned Velopack installation path, ensuring data survives program updates, repairs, and uninstallation.
  - **Multiple Vaults:** Added desktop support for running multiple isolated vaults. Users can create, rename, and switch between vaults via the system tray menu. "Forgetting" a vault removes it from the UI profile list while safely preserving files on disk.
  - **Automatic Legacy Data Rescue:** Integrated a startup rescue utility (`LegacyDataRescue`) that detects and migrates data from the old `current\data` directory to the stable data root. Conflicting data is automatically quarantined into a `recovered-<date>` vault, and the app fails-closed with a diagnostic screen if a migration error occurs.
  - **E2E Update Verification:** Added `smoke-update.ps1` to test the full Velopack update and repair lifecycle on throwaway packages, ensuring data preservation across updates.
  - **Pre-Apply Safety Guards:** Added checks in the update service to block application updates if legacy database files are found in mutable folders.
  - **Process Log Redirection:** Redirected backend `bmbd` process logs (stdout/stderr) to `<DataRoot>\logs` with automatic rotation for easier troubleshooting.
- **Obsidian vault import:** Upload an Obsidian vault as a ZIP archive — Markdown files become articles, folder hierarchy is preserved, Obsidian `![[image.png]]` embeds are rewritten to encrypted media links. Per-article error isolation (one bad file does not abort the whole import), Windows-ZIP path normalization, oversized images auto-downscaled to 4096px before reject. Endpoint: `POST /api/import/obsidian`.
- **Hard delete (Superadmin only):** Permanent purge of an article or folder subtree and all attached media. Propagates to every synced node via the new `hard_delete` sync event. Subsequent `article_update` events for a purged entity are suppressed via the `tbl_event(event_type, entity_id)` gate index. New admin page `/HardDelete` with preview, filter, status chips, and paginated audit log; entry point lives in the Admin page (removed from the header to avoid accidental clicks).
- **Hard-delete audit log:** New `tbl_hard_delete_audit` records every hard delete (actor, source node, entity type/id/title, counts of rows removed) with pagination UI.
- **Migration `DROP COLUMN` idempotency:** `MigrationRunner` now treats `ALTER TABLE DROP COLUMN … no such column` as already-applied, making repeated runs on partially-migrated replicas safe.
- **Near-realtime sync (push-on-save):** SyncTrigger signals immediate sync after every save, reducing sync delay from 60 seconds to near-instant between public nodes
- **Push position tracking:** New `tbl_sync_push_position` table tracks what was sent to each remote node (vs pull position which tracks what was received)
- **Sync delivery status endpoint:** `GET /api/sync/delivery-status` returns per-node push progress (lastPushedSeq, totalLocalEvents, isSynced, lastContactAt)
- **Lightweight ping endpoint:** `GET /api/sync/ping?afterSequence=N` — returns 204 (no new events) or 200 with count; no auth required
- **Sync status UI in Web header:** Badge with pending node count, click to expand per-node delivery details
- **Post-save sync toast:** After saving an article, a toast shows per-node sync delivery circles (green=synced, yellow=pending); click to open detailed modal
- Encrypted image storage with per-image DEK (AES-256-GCM), same envelope encryption as articles
- Image upload in Web UI editor: drag & drop, clipboard paste, and toolbar button via EasyMDE
- Image display in article view with URL rewriting through Web proxy
- API endpoints: `POST /api/media` (upload), `GET /api/media/{id}` (download with on-the-fly decryption)
- Media files included in snapshots (TAR archive with SHA256 hashes, manifest v2)
- Snapshot restore now recovers media files alongside the database
- Sync events for media: `media_create` and `media_delete` with Base64-encoded ciphertext
- Automatic cleanup: soft-deleted media purged after 30 days, orphaned uploads after 24 hours
- Browser caching for media: `Cache-Control: private, max-age=31536000, immutable`
- Full-text search across encrypted article bodies with batched decryption (50 articles per batch)
- Separate MCP tool `bee_search_content` for body content search (slow, opt-in)
- Content search checkbox on Web search page (unchecked by default)
- Content search toggle on Mobile articles page with 1-second debounce
- Docker Compose deployment tested and verified
- Screenshots section in README (Web themes, Mobile app)
- **Concept tag sync events:** `concept_tag_rename`, `concept_tag_merge`, `concept_tag_delete` for syncing concept tag operations across nodes
- **MCP concept tag tools:** `bee_search_by_concept`, `bee_list_concept_tags`, `bee_add_concept_tags`, `bee_remove_concept_tag`, `bee_rename_concept_tag`, `bee_merge_concept_tags`, `bee_delete_concept_tag`

### Changed

- SyncScheduler now uses `SemaphoreSlim(1,1)` concurrent guard to prevent overlapping sync cycles
- SyncScheduler resilient to exceptions: catches and logs errors instead of crashing the background service
- SyncClient push now filters by local `node_id` — no longer sends remote-origin events back to their source (reduces wasted traffic)
- `DeliveryNodeStatus.TotalLocalEvents` changed from `int` to `long` to support large event logs
- `bee_search` MCP tool now performs fast metadata-only search (title/tags)
- Web search defaults to metadata-only; content search is opt-in via checkbox
- SearchService uses batched processing instead of loading all articles at once
- EventApplier now supports file system access for media sync (writes .enc files to disk)
- Snapshot manifest bumped to v2 when media files are present
- **BREAKING:** Removed keyword tags — `Article.Tags` property removed, articles now only have concept tags via `ConceptTagService`
- **BREAKING:** Migration `004_unify_tags.sql` copies existing keyword tags into concept tags (case-insensitive merge), renames `tbl_tag` → `tbl_tag_deprecated`, `tbl_article_tag` → `tbl_article_tag_deprecated`
- API: `Article.Tags` field in responses kept as empty `[]` for 1 release (mobile compat), will be removed next release; use `ConceptTags` instead
- API: `CreateArticleRequest` and `UpdateArticleRequest` now use `ConceptTags` parameter instead of `Tags`
- MCP: `bee_save_article(tags=...)` parameter deprecated but still works (merged into concept_tags with audit warning)
- ConceptTagService now emits sync events for rename/merge/delete operations

### Security

- **Delivery-status endpoint** protected by `InternalKeyValidator` (prevents node topology exposure)
- **Ping endpoint integer overflow fix:** removed `(int)` cast on `afterSequence` parameter (long values >2B were truncated)
- **XSS fix in sync toast:** node `displayName` now HTML-escaped in title attributes
- Admin endpoints (user management, lock) now require `BMB_INTERNAL_KEY` shared secret between Web and API, preventing role spoofing via HTTP headers
- Admin page and proxy routes restricted to `superadmin` role (cookie-based auth)
- `ApiClient` reads role from cookie claims per-request instead of a mutable singleton field
- Revoked sync nodes now correctly rejected by `GetByNodeIdAsync` (status filter added)

## [1.0.0] - 2026-04-08

### Added

- Initial release of BeeMemoryBank monorepo ([5258599])
- Multi-user authentication with role-based access: superadmin, unlocker, user ([0afc1d9])
- Bee delete folder MCP tool for removing empty folders ([737eee4])
- Dark Classic and Dark Bee themes ([4afd77b])
- Cache busting for site.css and site.js via `asp-append-version` ([43023a1])
- Folder creation: API endpoint, UI buttons on Home, Sidebar, and Folder pages ([8c32031])
- Mobile app: markdown rendering, security page, UI icons, app icon fix, peer management ([31908dd])
- Deploy button on Admin page with remote server setup instructions ([5c4b500], [5d7b6d7])
- Deploy mechanism via systemd oneshot service to survive API restart ([0da53ab])
- Maintenance page redirect during deployment ([9c160a7])
- Admin deploy section with description and disabled state ([fb061bd])
- Whitelist update sync event and Change Node URL feature ([ddc8f52])
- Security hardening, comment encryption, and sync status UI ([9685236])

### Changed

- Translated all Russian/Ukrainian text to English across the entire codebase ([ca690a2])

### Fixed

- Empty folders not showing in tree: TreeService now uses `tbl_folder` ([b0f99e9])
- Sidebar splitter lag; increased max width to 50% ([67ce3af])
- Sidebar UX: removed `+` from add folder, hidden refresh on hover, scrollbar padding ([30e8a5c], [70f90ca])
- URL validation: auto-prepend `https://` if scheme is missing ([b107eff])
- Deploy button: run via systemd oneshot service to survive API restart ([0da53ab])

[1.0.0]: https://github.com/ultrathinker/BeeMemoryBank/releases/tag/v1.0.0
