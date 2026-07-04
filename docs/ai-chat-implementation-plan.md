# AI Chat — Implementation Plan

> Status: **Build plan, autonomous execution.** Synthesized from two analyses
> (`_kilo_analysis.md` and the Fable architecture analysis). All judgment calls are
> LOCKED below (§1) so implementation proceeds without human input. Phases are small,
> independently abortable, and each keeps the vault security model untouched.

## 0. Goal
A native "AI" chat inside BeeMemoryBank: the user talks to an LLM (via OpenRouter) about
their Bee data and can ask it to act on that data through tools — strictly within the
logged-in user's ACL. Chat history in a separate, non-syncing SQLite DB. Self-hosted; no
external chat service; keys held server-side, encrypted at rest.

## 1. LOCKED decisions
- **Split:** backend in **Api** (owns `chat.db`, session/DEK, OpenRouter egress, tools).
  **Web** = Razor page + JS + a dedicated **streaming SSE passthrough** proxy. No chat
  logic/secrets in Web.
- **Tools = option (b):** a curated `ChatToolDispatcher` in Api calling the SAME Core
  services the MCP tools use (`SearchService`, `ArticleService`, `FolderService`,
  `FolderAccessService`, `ConceptTagService`), running under the request's ambient
  `CallerScope`. **Do NOT** use an internal `/mcp` client (that runs under decoupled agent
  identity + truncation). **CRITICAL — the get-article-content tool MUST mirror the ACL
  gate in `server/BeeMemoryBank.Api/Endpoints/ArticleEndpoints.cs` (the `/{id}/content`
  handler, ~L52–77):** call `ArticleService.GetMetadataAsync(id)` first (scope-filtered —
  returns null if the caller's scope denies the path), then
  `FolderAccessService.IsAccessDenied(...)` on the metadata's `TreePath`, and ONLY then
  `GetContentAsync(id)`. `GetContentAsync` goes straight to the body repo with **no scope
  filter**, so calling it directly is a plaintext ACL bypass. Every write/delete tool must
  likewise call the same scope-checked service methods the REST endpoints use (never raw
  repos/SQL), and re-check `session.IsUnlocked` for any content read/write.
- **ACL:** enforced by reuse — the chat request arrives from Web with
  `X-Internal-Key`+`X-User-Id`/`X-User-Role`, `CallerScopeMiddleware` sets scope, Core
  repos filter. The AI can only see/do what the user can. This is also the prompt-injection
  backstop.
- **Frontend:** **build native** (Razor + Shoelace + global `marked`/`DOMPurify`). No OSS
  SPA (CSP/frame-ancestors/second-runtime hostile).
- **Chat DB:** separate `{dataPath}/chat.db`, own `ChatDbConnectionFactory` — a **distinct
  type** from `BeeMemoryBank.Storage.DbConnectionFactory` (which is DI-injected everywhere
  pointing at `beedb.sqlite`); the two must not collide in DI. Schema created by a small
  idempotent `ChatDbInitializer` (inline `CREATE TABLE IF NOT EXISTS`) run once at startup
  from a dedicated `using (var scope …)` block in `Api/Program.cs`, placed **after** the
  existing beedb migration/bootstrapper blocks. It must **NOT** use `MigrationRunner`, must
  **NOT** live under any `Storage/Migrations/*.sql` (that folder is glob-embedded and
  Ghost-Hunter-managed), and must **NOT** be registered by `AddStorage()`. Never touched by
  `EventLogger`/`EventApplier`/`SnapshotService`/`SnapshotRestoreService`/`SyncClient`.
  No cross-device chat continuity (accepted). Guard test — see §4.
- **Keys at rest:** encrypted under the **master DEK**, using the EXACT precedent in
  `libs/BeeMemoryBank.Core/Services/RemoteAccountService.cs` (`EncryptToken`/`DecryptToken`,
  ~L96–108): `ArticleEncryptor.Encrypt(plaintext, masterDek, aad)` → `(ciphertext, iv)`,
  where `masterDek = session.GetMasterDek()` and the DEK is wiped with `Array.Clear(masterDek)`
  in a `finally`. Use a distinct constant AAD (e.g. `"bmb-openrouter-key-v1"`). **Do NOT use
  `AgentKeyHelper`** — that derives a key *from* an agent key to *wrap* the master DEK (the
  opposite direction); it is not an encrypt-a-secret-under-the-DEK primitive. ⇒ keys are
  configurable & usable only while the vault is unlocked (acceptable). Never sent to browser
  after creation (store + show a short `key_prefix` only). Egress pinned to
  `https://openrouter.ai`; browser never calls OpenRouter (no `connect-src` change).
- **Keys scope:** **node-global** (configured in settings, shared on this node). Per-user
  keys deferred. **Key/model catalogue writes require superadmin** (role check like
  Snapshots/Users); listing *enabled* models for the per-conversation picker is available to
  any authenticated user.
- **Write policy:** AI writes ALLOWED but every write/destructive tool goes through a
  **human-in-the-loop confirm** SSE gate (UI: "AI wants to X — Allow/Deny"; runs only on
  Allow). Read-only first (P1).
- **Tool surface (curated, deny-by-default):** allowed reads: `bee_search`,
  `bee_list_articles`, `bee_get_tree`, `bee_get_article` (content only when unlocked),
  `bee_search_content` (allowed, slow — fine). Allowed writes (P3, confirm-gated):
  `bee_save_article`, `bee_update_article`, `bee_append_to_article`,
  `bee_replace_in_article`, `bee_delete_article` (its existing 2-step confirm kept).
  **NEVER exposed:** tag rename/merge/delete, folder delete, hard-delete, DEK rotation,
  snapshot, user/agent admin, **audit tools**.
- **Audit:** AI-driven writes flow through `ArticleService` → `IEventLogger` (so the
  *article change* is audited + syncs like a human edit; only the *transcript* doesn't).
  Tag caller `ViaAgentName="chat"` so `/Activity` distinguishes AI edits.
- **Privacy:** UI shows a clear "everything the AI reads is sent to OpenRouter" notice.
- **Cancellation:** forward client disconnect (`RequestAborted`) to abort the OpenRouter
  stream (stops token billing). New capability — implement in the streaming phase.
- **Mobile (MAUI), per-user rate limits, cost dashboards, local-LLM backend:** out of scope
  for this pass (note as future).

## 2. Phases (each: kilo implements → I build → kilo reviews → kilo fixes → next)

### Phase 0 — Backend scaffold (no UI)
- `Api/Services/ChatDbConnectionFactory.cs` (+ WAL) as a distinct type; `ChatDbInitializer`
  (idempotent `CREATE TABLE IF NOT EXISTS`, tables from §3) invoked once from a dedicated
  scope block in `Api/Program.cs` after the beedb migration blocks; repos
  (`ChatConversationRepository`, `ChatMessageRepository`, `ChatSettingsRepository` for
  keys+models). All registered in `Api/Program.cs` (NOT via `AddStorage`).
- `Api/Endpoints/ChatEndpoints.cs` skeleton, grouped `app.MapGroup("/api/chat")
  .RequireInternalKey()`. Settings **write** endpoints (key CRUD, model catalogue) also
  check `X-User-Role == superadmin` (like `SnapshotEndpoints`). Key create returns the full
  key once; thereafter only `key_prefix`. Encryption uses the `RemoteAccountService`
  precedent (§1). **Any endpoint that encrypts/decrypts a key MUST check `session.IsUnlocked`
  first and return `409` `{"error":"Vault is locked"}` when locked** (encrypt needs the
  master DEK).
- `OpenRouterClient` (non-streaming completion, single key) — minimal, egress pinned to
  `https://openrouter.ai`.
- Guard test (§4): `chat`/`ChatDb` absent from Sync + Snapshot code.
- **Accept:** project builds. With the vault unlocked (via the existing
  `POST /api/session/unlock` master-password endpoint — already `SkipInternalKey`), a curl
  carrying `X-Internal-Key` (from `{dataPath}/.internal-key`) + `X-User-Role: superadmin`
  configures a key, then a second curl gets a non-streaming completion. A curl against the
  key-config endpoint while the vault is **locked** returns `409 Vault is locked`. Guard test
  green. No UI.

### Phase 1 — Read-only native chat (non-streaming)
- `ChatToolDispatcher` with read-only tools only; tool-call loop server-side.
- `Web/Pages/AI.cshtml(.cs)` + `wwwroot/js/chat.js` (message list, composer, markdown via
  marked+DOMPurify), "AI" nav item in `_Layout.cshtml`; `/api-proxy/chat/*` JSON routes.
- Settings UI (in Admin or a Chat settings panel): add/list/disable keys, pick a text model.
- **Accept:** logged-in user asks about their vault, read-only, ACL-correct; locked-vault
  handled (metadata tools work, content tool says "unlock to read").

### Phase 2 — Streaming + history
- SSE end-to-end: Api endpoint returns `Content-Type: text/event-stream` and writes events
  incrementally with `await Response.Body.FlushAsync()` per event.
- **Web passthrough MUST be a dedicated, explicit hand-written route** (e.g.
  `POST /api-proxy/chat/{conversationId}/stream`). It **MUST NOT** be served by the W1
  catch-all `/api-proxy/{**path}` forwarder, which buffers the whole upstream body via
  `ReadAsStringAsync` (see `Web/Program.cs`) and would break streaming. **Do NOT use
  `Results.File`** (download/seek semantics). Instead: call the API with
  `HttpCompletionOption.ResponseHeadersRead` (reuse `ApiClient.SendForwardAsync`), set
  `ctx.Response.ContentType = "text/event-stream"` and `Headers["X-Accel-Buffering"]="no"`,
  then copy the upstream content stream to `ctx.Response.Body` in a loop with a flush after
  each chunk. Register this route **before** `app.Run()` (explicit routes win over the
  catch-all regardless, but keep it visibly separate). Forward `ctx.RequestAborted` into
  `SendForwardAsync` so a browser disconnect cancels the upstream call.
- Persist `chat_conversation`/`chat_message`; history sidebar; rename/delete conversation.
- **Accept:** streaming chat with persisted history; disconnect stops the upstream stream
  (verify the OpenRouter request is cancelled, not just the browser response).

### Phase 3 — Guarded write tools
- Add write tools to the dispatcher behind the confirm-gate SSE event + UI Allow/Deny.
- `ViaAgentName="chat"` on writes.
- Per-turn tool-call cap (e.g. 10) + per-session destructive-op cap.
- **Accept:** "create/append/edit a note" works behind an explicit approval; ACL/read-only
  folders rejected; audit shows AI actor.

### Phase 4 — Multi-key failover + model categories
- Ordered labelled keys; per-key circuit breaker (401→session-disable, 402/429→cooldown,
  5xx→retry-next); structured `event: error` when exhausted.
- Model catalogue (manual add; optional refresh from OpenRouter `/models`), category
  text|vision|image-gen; per-conversation model pick.
- **Accept:** a dead key auto-fails over; category-filtered model picker.

### Phase 5 — Vision + image generation
- Image upload (vision models only; MIME allow-list + size cap; server resize) stored in
  `chat_attachment` (NOT vault media); image-gen category; inline render (`data:`/`blob:`
  already CSP-allowed). Explicit ACL'd "save to Bee" as a normal media/article action.
- **Accept:** image understanding + generation in chat; attachments in chat.db only.

## 3. chat.db schema (idempotent CREATE IF NOT EXISTS)
- `chat_conversation(id, user_id, title, created_at, updated_at)`
- `chat_message(id, conversation_id, role, content_text, tool_calls_json, tool_call_id,
  model, tokens_in, tokens_out, created_at)`
- `chat_attachment(id, message_id, kind, mime, blob, created_at)`
- `chat_api_key(id, label, key_prefix, ciphertext, iv, enabled, priority,
  disabled_until, last_error, last_used_at, created_at)` — NO `salt`/`kdf_version`: the
  `ArticleEncryptor.Encrypt(secret, masterDek, aad)` path yields only `(ciphertext, iv)`
  (AES-256-GCM under the master DEK; AAD is a fixed constant). `key_prefix` is the short
  display fragment shown in the UI after creation.
- `chat_model(id, model_id, label, category, default_for_category, enabled)`

## 4. Guardrails for every phase
- Nothing commit/push/deploy — working tree only.
- kilo does NOT run `dotnet build`/`test`; the orchestrator compiles and feeds errors back.
- Keep `chat_*` out of Sync/Snapshot/Storage-migrations by construction. **Guard test:**
  assert the tokens `chat` / `ChatDb` / `chat.db` do not appear in `libs/BeeMemoryBank.Sync/`
  **nor** in `SnapshotService.cs` / `SnapshotRestoreService.cs` (a source-scan test), so
  chat data can never enter the event stream, a snapshot, or a join/restore. Also confirm
  no `chat_*` `.sql` file lands under `libs/BeeMemoryBank.Storage/Migrations/`.
- **Tool ACL:** the get-article-content tool mirrors the `ArticleEndpoints` `/{id}/content`
  ACL gate (metadata → `IsAccessDenied` → `GetContentAsync`); all tools call scope-checked
  service methods, never raw repos/SQL; content ops re-check `session.IsUnlocked`. (§1.)
- **Do not over-edit.** Implement only what a phase names. Do not rewrite existing handlers,
  the W1 catch-all, or the streaming/auth helpers — reuse them as-is. If a phase seems to
  need a change to existing code beyond adding the new chat surface, STOP and flag it rather
  than editing.
- Never log or return raw API keys; never send them to the browser; never add `openrouter.ai`
  to browser CSP.
- Reuse existing patterns: `RemoteAccountService`/`AgentKeyHelper` (key encryption),
  `DisposingStreamWrapper`/`SendForwardAsync` (streaming), `RequireInternalKey` (auth),
  `CallerScope` (ACL), `marked`+`DOMPurify` (render).

## 5. Open risks carried forward (documented, not blocking)
- All AI-read content leaves the node to OpenRouter (inherent; UI warns).
- Prompt injection bounded by ACL + confirm-gate + curated tools + data/instruction
  separation in the system prompt.
- No chat on other devices (no sync) — by design.

## 6. Revisions (this pass — corrections against the real code)
- **Key-encryption precedent fixed.** The `AgentKeyHelper` reference was wrong: it derives a
  key *from* an agent key to *wrap* the master DEK (`EncryptDek`/`EncryptDekV1`) — the
  opposite of encrypting a secret under the DEK. Locked to the `RemoteAccountService`
  precedent (`EncryptToken`/`DecryptToken` → `ArticleEncryptor.Encrypt(secret, masterDek,
  aad)` with `session.GetMasterDek()` + `Array.Clear`). Schema `chat_api_key` accordingly
  dropped `salt`/`kdf_version` (not produced by that path) and added `key_prefix`.
- **Phase 0 acceptance made realistic.** Encryption needs the master DEK, so the acceptance
  now unlocks the vault first (existing `POST /api/session/unlock`) and requires
  `X-User-Role: superadmin` for key config; added the locked-vault → `409` requirement.
- **Streaming disambiguated.** Explicitly forbade routing the SSE stream through the W1
  catch-all (it buffers via `ReadAsStringAsync`) and through `Results.File`; specified the
  dedicated `ResponseHeadersRead` + copy-with-flush passthrough.
- **ACL bypass pre-empted.** Added the mandatory content-tool ACL gate mirroring
  `ArticleEndpoints` `/{id}/content` (calling `GetContentAsync` directly bypasses folder
  ACLs). Corrected the Core service name `FolderRepository` → `FolderService`
  (+ `FolderAccessService`).
- **Chat-DB decoupling made concrete.** `ChatDbConnectionFactory` must be a distinct DI type
  from `DbConnectionFactory`; schema via a startup `ChatDbInitializer` (not `MigrationRunner`,
  not under `Storage/Migrations/`); strengthened the guard test to also cover `SnapshotService`
  / `SnapshotRestoreService`.
- **Anti-over-edit guardrail added** (§4) after the earlier incident where a handler was
  rewritten wholesale; kilo must add the chat surface only and flag—not edit—existing code.
- **Verified accurate, left as-is:** option (b) + CallerScope ACL reuse; `bee_delete_article`
  two-step `confirm=true` exists in `BeeWriteTools`; native-frontend choice; node-global keys.

_Source analyses: `_kilo_analysis.md` (kilo) + the Fable architecture analysis (in the
orchestration log). This plan LOCKS the decisions they surfaced._
