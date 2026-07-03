# Web Architecture Improvement Plan

> Status: **Locked for autonomous implementation.** All open questions (formerly §6)
> have been decided — see **§0 Decisions**. A coding agent will implement this with
> no further human input, so every workstream is concrete and unambiguous. Each is
> sized (S/M/L), risk-rated, and code-grounded. Where a workstream touches auth or the
> DB, an **Autonomous execution guardrail** bounds what the agent may do in one pass.

## 0. Decisions (locked — do not re-open)

These resolve the questions the draft left open. Rationale is short; the point is that
the implementer treats them as settled.

| # | Question | **Decision** | Why |
|---|----------|--------------|-----|
| O1 | W1: hand-written catch-all vs. YARP | **Hand-written catch-all + declarative table.** No new dependency. | `InternalKeyHandler` already injects identity headers; the route set is small and auditable; a single in-process hop doesn't justify YARP. |
| O2 | W1: deny-by-default route table | **Yes — unknown `/api-proxy` prefix returns 404, never a blind forward.** | Mirrors `CallerScopeMiddleware`'s deny-all. A forgotten prefix fails safe. |
| O3 | W3: security stamp vs. short cookie | **Both, sequenced: Option B (short non-sliding cookie) first as its own commit, then Option A (security stamp).** | B is a ~2-line, zero-risk ceiling that ships value immediately; A is the real per-event revocation. They compose. |
| O4 | W5c: fix doc vs. fix dependency | **Option A now (fix the doc).** Reassess Option B only as the *closing* commit of W1, and only if W1 actually removed the last `Core` reference. | The Web→Core reference is read-only DTO/enum reuse; deleting it prematurely creates churn for no safety gain. |
| O5 | W4: where anonymous API endpoints live | **In-group, with an explicit opt-out marker** the group filter honors (`SkipInternalKey` endpoint metadata). Do **not** relocate endpoints between groups. | Least churn, lowest risk of breaking route paths; the exception stays visible at the registration site. |
| O6 | W5b: EasyMDE spell-checker (hits `cdn.jsdelivr.net`) | **Disable the spell-checker entirely** (`spellChecker: false`), rather than self-hosting dictionaries. | It is already broken under the current CSP; disabling removes a third-party fetch and dead weight in one line. |

**Correction applied vs. the draft:** the internal-key check appears **117** times in
`server/BeeMemoryBank.Api/Endpoints/` (verified by grep), not "124". All other
file/line citations in this plan were re-verified against the current source and are
correct as written.

## 1. Verdict & how to read this plan

The architecture is **sound** and should **not** be rewritten. The split — Web as a
stateless HTTP proxy, API as the sole owner of crypto/session/ACL/data — is the
correct one. The CLI, the .NET MAUI mobile app, and the MCP server all consume the
API directly, which independently proves the API is the real product and the Web
layer is a convenience shell over it. The defense-in-depth posture is genuinely
good and must be preserved:

- Correct trust model — `X-User-Id` / `X-User-Role` are honored by the API only
  after `InternalKeyValidator.Validate(ctx)` passes (see `Api/Helpers/CallerIdentity.cs:32`).
- Deny-all default for anonymous/unknown callers (`Api/Middleware/CallerScopeMiddleware.cs:39-49`).
- Real double-checking — the Web layer gates on role, and the API re-checks
  superadmin on HardDelete / Users / Snapshots.
- Hardened cookie (`SameSite=Strict`, `Secure`, `HttpOnly`, `Web/Program.cs:55-62`),
  HSTS, fail-fast on a missing `BMB_INTERNAL_KEY` (`Api/Program.cs:32-38`),
  constant-time key comparison (`Api/Middleware/InternalKeyMiddleware.cs:25`).

**The cost of the current design is mechanical, not conceptual.** Every new feature
requires ~3–4 near-identical edits spread across two projects, and the only safety
net is runtime drift (a real incident where enum-as-int serialization broke the
login page is now papered over by `Api/Program.cs:152-159` adding a
`JsonStringEnumConverter`).

This document turns the review findings (C1–C5) into a phased, incremental roadmap.
**Nothing here is a rewrite.** Read each workstream (§3) as an independent,
mergeable chunk; §4 gives the suggested ordering.

---

## 2. Workstream summary

| ID | Title                                           | Effort | Risk  | Maps to |
|----|--------------------------------------------------|--------|-------|---------|
| W4 | Hoist the internal-key check into a route-group filter | S  | Low    | C4 |
| W3 | Session revocation (short cookie first, then security stamp) | M | Medium | C3 |
| W2 | Standardize proxy error propagation                    | S  | Low    | C2 |
| W1 | Collapse the proxy into a catch-all forwarder (staged) | L  | High   | C1 |
| W5a| Document why `/api-proxy/init/reset` is anonymous      | S  | None   | C5a |
| W5b| Self-host FontAwesome + tighten CSP                    | M  | Low    | C5b |
| W5c| Resolve the Web→Core dependency / doc contradiction    | S  | Low    | C5c |

---

## 3. Workstreams

### W4 — Hoist the internal-key check into a route-group filter  (C4) — *S / Low*

**Problem.** Every API handler starts with a copy-pasted
`if (!InternalKeyValidator.Validate(ctx)) return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);`
(**117** occurrences across the endpoint files — verified by grepping the `Endpoints/`
folder). A new endpoint that forgets this line is silently **fail-open**.

**Goal.** Make the internal-key gate **structural** — impossible to forget — by
applying it as an endpoint filter on the route group, while keeping per-endpoint
role/depth checks exactly where they add value.

**Approach.**

1. Each `Map{X}Endpoints(this WebApplication app)` extension already opens with
   `var group = app.MapGroup("/api/...").WithTags("...")` (e.g.
   `SnapshotEndpoints.cs:19`, `HardDeleteEndpoints.cs:14`). Add a shared
   extension, e.g. `group.RequireInternalKey()`, that registers an
   `IEndpointFilter` calling `InternalKeyValidator.Validate(ctx)` and returning
   a 403 `ErrorResponse` when it fails.
2. **Mark the intentional exceptions with an in-group opt-out marker (decision O5).**
   Define a marker type `SkipInternalKey` (empty class) and attach it via
   `.WithMetadata(new SkipInternalKey())` on the exempt endpoints. The
   `RequireInternalKey()` filter inspects `context.HttpContext.GetEndpoint()?
   .Metadata.GetMetadata<SkipInternalKey>()` and **skips** validation when present.
   Do **not** relocate these endpoints into a separate group — keep them in place to
   avoid changing route paths. The exempt endpoints:
   - `GET /api/snapshots/restore/progress` — intentionally reachable from the
     locked login screen (`SnapshotEndpoints.cs:337`, comment above it).
   - `GET /api/snapshots/restore/{eventId}/file` — uses Bearer/sync
     challenge-response auth, not the internal key (`SnapshotEndpoints.cs:286`).
   - `GET /health`, `GET /api/version` — anonymous (`Api/Program.cs:358-371`),
     already registered on `app` directly (outside any `/api/...` group), so the
     group filter never touches them — no marker needed.
   - The `init/*` endpoints during first-run setup (`InitEndpoints.cs`) — mark those
     that must run before an internal key exists.
   - `POST /mcp` (`Api/Program.cs:401`) is **not** an `/api/...` group endpoint and is
     authenticated by `AgentAuthMiddleware` + `CallerScopeMiddleware` deny-all; the
     group filter must not be applied to it. Leave it alone.
3. After the filter is in place, the redundant per-handler
   `if (!InternalKeyValidator.Validate(ctx))` blocks can be deleted file-by-file.
   Keep the **role/superadmin** checks — they are the second layer of
   defense-in-depth and stay per-endpoint.
4. **Consolidate the role check helper — this is a readability refactor, NOT a
   security fix.** `SnapshotEndpoints.cs:28,39,57,82,135,158,208,374,401` reads
   `ctx.Request.Headers["X-User-Role"]` directly, but **each of those handlers already
   calls `InternalKeyValidator.Validate(ctx)` immediately above** (e.g. lines 23, 35,
   52) — so the role header is only ever trusted after the internal key validates.
   The current code is **correct and safe**; do not treat it as a hole to plug. The
   consolidation is optional: migrate to the `CallerIdentity.Extract(ctx).IsSuperadmin`
   pattern used by `HardDeleteEndpoints.cs:23-25` (the most robust form) for
   uniformity. If unsure, **leave these handlers as-is** — correctness must not
   regress for a style win.

**Risk.** Low. Purely additive at first (filter + opt-out list), then mechanical
deletion. The one subtlety: the filter must run on the group, and any endpoint
registered *outside* a group (e.g. directly on `app`) must keep its manual check or
be moved into a group. Audit `Api/Program.cs:373-401` — all `Map*Endpoints` calls
use groups, so coverage is already uniform.

**Verify.**
- `dotnet test tests/BeeMemoryBank.Integration.Tests/` — the API integration suite
  exercises real requests and would catch a regression in auth gating.
- Add one negative test per anonymous-allowlisted endpoint confirming a *normal*
  internal-key endpoint still 403s when the key is absent (proves the filter is
  active, not silently bypassed).
- Manual: confirm the locked `/Login` page still polls
  `/api-proxy/.../restore/progress` successfully (the opt-out path).

---

### W3 — Session revocation against the API  (C3) — *M / Medium*

**Problem.** Cookie claims (role, userId, displayName) are minted once at login
(`Web/Pages/Login.cshtml.cs:70-81`) with a **7-day sliding expiry**
(`Web/Program.cs:63-64`) and **never revalidated against the API**. A user who is
deleted, demoted, or has their password reset keeps their old role/access until the
cookie naturally expires. For a team vault where "revoke access" is a first-class
operation, this is the **top security gap**.

**Goal.** Bound the window during which a stale credential stays valid to minutes,
not days, **without** making every page load round-trip the API.

**Decision (O3): ship BOTH, in this order — B as commit 1, then A as commit 2.**
B is the immediate ceiling; A is per-event revocation. Do them as two independent
commits so B's value lands even if A is deferred.

**Commit 1 — Option B: short, non-sliding cookie lifetime.**

1. In `Web/Program.cs:63-64`, set `ExpireTimeSpan = TimeSpan.FromHours(8)` and
   `SlidingExpiration = false`.
2. Zero new endpoints/columns. This is the absolute ceiling on a stale cookie; it
   does not by itself revoke a still-fresh stolen cookie — that is what A adds.

**Commit 2 — Option A: per-user security stamp.**

> **Correctness note (must read):** users are **node-local** — `architecture.md`
> states users/agents/ACLs are created per node and are **not** propagated through the
> event stream. Therefore `security_stamp` is a **local** column: it must **NOT** be
> added to any sync event payload, `EventApplier`, or `tbl_event`. It is read/written
> only by the local API. Do not touch sync code.

1. **Migration.** Add `libs/BeeMemoryBank.Storage/Migrations/NNN_security_stamp.sql`
   (next sequential number). SQLite cannot default a column to a per-row random value,
   so add the column then backfill in the same file. Follow the repo migration rules
   in `CLAUDE.md` (no `;` inside `--` comments; the runner splits on `;`):
   ```sql
   ALTER TABLE tbl_user ADD COLUMN security_stamp TEXT NOT NULL DEFAULT '';
   UPDATE tbl_user SET security_stamp = lower(hex(randomblob(16))) WHERE security_stamp = '';
   ```
   Add a `SecurityStamp` property to `User` (`libs/BeeMemoryBank.Core/Models/User.cs`)
   and map it in the user repository (`libs/BeeMemoryBank.Storage/Sqlite/`).
2. **Bump the stamp on every identity-affecting change** (regenerate
   `lower(hex(randomblob(16)))` or a new GUID): password change, role change,
   `IsActive` flip, user deletion. Touch sites: `UserEndpoints.cs` (update,
   change-password, delete) and the self-service change-password flow. A user's own
   password change bumping their stamp will log out their *other* sessions — that is
   the desired behavior; keep the current session valid by re-issuing its cookie with
   the new stamp after a self-service change (or accept a re-login — note which you
   chose in the commit message).
3. **New API endpoint** `GET /api/users/me/stamp` returning `{ "stamp": "..." }` for
   the forwarded `X-User-Id`. It is in the `/api/users` group, so W4's
   `RequireInternalKey()` filter covers it — no manual key check needed once W4 has
   landed (W4 precedes W3 in the phasing).
4. **Web-side validation.** Embed the stamp as a claim at login
   (`Login.cshtml.cs:70-81`, add to the claims list). Register
   `CookieAuthenticationEvents.OnValidatePrincipal` on the cookie
   (`Web/Program.cs:49-65`) that:
   - Reads the stamp claim; if absent (old cookie from before this change), reject →
     forced re-login (safe, one-time).
   - Looks up the current stamp via a **Web-side `IMemoryCache`** keyed by user id,
     TTL **5 minutes**, populated from `GET /api/users/me/stamp`.
   - On mismatch → `context.RejectPrincipal()`.
   - **Fail-OPEN on API-unreachable:** if the stamp lookup throws or times out, do
     **not** reject the principal (an API hiccup must not log out the whole site);
     log a warning and let the request through. The 5-min cache already bounds
     exposure.
   - Static assets run before auth (`Web/Program.cs:148-151`), so this never fires on
     CSS/JS requests.

**Autonomous execution guardrail (W3).** Implement Commit 1 (Option B) fully. For
Commit 2 (Option A): implement it, but the fail-open behavior and the "absent claim →
reject" path are **mandatory** — an implementation that fails closed on API errors, or
that logs users out on every request, must be treated as a failed build. Ship A only
if the new integration test below is green; otherwise stop after Commit 1 and leave A
for a supervised follow-up.

**Risk.** B: none. A: medium (auth hot path + migration). Mitigations are baked into
the steps above: 5-min cache, fail-open, node-local stamp (no sync), mandatory test.

**Verify.**
- New integration test: user A logs in → superadmin demotes/deletes A → A's next
  request after the cache TTL is rejected/redirected to `/Login`.
- Confirm `OnValidatePrincipal` does not issue a round-trip on *every* static-asset
  request (static files run before auth in `Web/Program.cs:148-151`, so this is
  already naturally avoided).

---

### W2 — Standardize proxy error propagation  (C2) — *S / Low*

**Problem.** Roughly half the proxy routes collapse **any** API non-success into a
generic `Results.StatusCode(502)` (e.g. `Web/Program.cs:167,238,267,305,312,318,
387,417,429,443,505`), a few map null→404 (`:161,564,675`), and the rest carefully
propagate `(ok, status, error)` (`:186,198,248,298,346`). Consequence: an ACL `403`
from the API can surface to the browser as a `502` or `404` depending on which route
handled it, and the original `error` text is lost.

**Goal.** A single, predictable contract for the hand-written routes that survive
W1: upstream status + body pass through unchanged.

**Approach.**

1. Introduce one helper on `ApiClient`, e.g. `Task<ProxyResult> ForwardAsync(...)`
   returning `(int Status, ReadOnlyMemory<byte> Body, string? ContentType)` that
   reads the upstream `HttpResponseMessage` and preserves status + body verbatim.
   (The pieces already exist — `PostRawAsync` at `ApiClient.cs:401-406` and
   `ReadErrorAsync` at `:1263-1275` — just not unified.)
2. Migrate the surviving hand-written routes to it; emit
   `Results.Content(body, contentType, null, statusCode)`.
3. **Most of C2 is eliminated for free by W1's catch-all forwarder**, which passes
   status/body through untouched. W2 only governs the handful of routes that stay
   hand-written (downloads, related-articles paging, the combined article+content
   fetch) — see W1's "routes that stay hand-written" list.

**Risk.** Low. Behaviour change is "the browser now sees the real 403/409/error
message instead of a 502" — strictly an improvement. Front-end JS that special-cased
502 may need a quick check.

**Verify.** For each migrated route: drive it to an API failure (e.g. ACL-denied
folder) and assert the browser receives the upstream status code and message, not a
502.

---

### W1 — Collapse the proxy into a catch-all forwarder  (C1) — *L / High*

> This is the biggest and riskiest item. The plan below is **incremental by design**
> — the catch-all and the hand-written routes coexist during migration, so any
> migrated group can be reverted independently.
>
> **Autonomous execution guardrail (W1) — READ FIRST.** In the autonomous pass, do
> **only** the following and then STOP for human review:
> 1. Build the infrastructure: `ProxyRouteTable.cs` + the catch-all forwarder +
>    deny-by-default (unknown prefix → 404).
> 2. Migrate **exactly one pilot group: `concept-tags`** (GET-only, role: none,
>    non-streaming — the lowest-risk group).
> 3. Run `dotnet test tests/BeeMemoryBank.Integration.Tests/` and a manual UI smoke.
> 4. **Do NOT** migrate any `superadmin`-gated group (users, snapshots, hard-delete,
>    restrictions, sync), any streaming/upload route, or any PATCH route in this pass.
>    Those migrations are a supervised follow-up, one group per commit.
> This proves the mechanism on a safe group without touching auth-sensitive or
> streaming paths unattended. Decisions O1 (hand-written catch-all) and O2
> (deny-by-default) are locked — see §0.

**Problem.** `Web/Program.cs` is 849 lines of ~70 near-identical
`MapGet/MapPost/...` route registrations; `ApiClient.cs` is 1276 lines mirroring the
API one method per endpoint; DTO shapes live in **three** places (inline records in
`Program.cs:790-827`, `Web/Models/ApiModels.cs`, and `Api/Models/{Requests,Responses,
SyncModels}.cs`). Each feature costs 3–4 mechanical edits; drift is caught only at
runtime.

**Goal.** Delete ~80% of `Program.cs` and most of `ApiClient` by routing the
mechanical majority of `/api-proxy/*` requests through a single data-driven
forwarder, while keeping the genuinely-special routes hand-written.

**The key enabler (already in place).** `InternalKeyHandler`
(`Web/Services/InternalKeyHandler.cs`) is a `DelegatingHandler` on the `ApiClient`
`HttpClient` that **automatically** injects `X-Internal-Key`, `X-User-Role`,
`X-User-Id`, `X-User-DisplayName` on *every* outbound call. This means a raw
`HttpClient.SendAsync` in the forwarder inherits the full identity context for free
— the forwarder does not need to know about auth at all. The forwarder only needs to
decide **role gating** and **streaming**.

**Approach — incremental migration.**

**Step 1 — the declarative route table.** Define a small static table in a new
`Web/Services/ProxyRouteTable.cs`:

```
path-prefix (relative to /api-proxy) → { upstream path, required role?, streaming? }
```

Examples derived from the current code:
- `concept-tags` GET family → role: *none*, streaming: false
  (`Program.cs:309-374`)
- `snapshots` POST/DELETE → role: `superadmin`, streaming: false (`:390-400`)
- `users/*` → role: `superadmin` (`:573-611`)
- `hard-delete/*` → role: `superadmin` (`:682-722`)
- `folders/download` → streaming: true (`:508-518`)
- `downloads/{token}` → streaming: true (`:528-539`)
- `sync/status`, `sync/delivery-status` → role: `superadmin` (`:452-462`)

**Step 2 — the catch-all handler.** A single
`app.MapMethods("/api-proxy/{**path}", [GET,POST,PUT,DELETE,PATCH], forward)` that:
1. Looks up the longest matching prefix in the table.
2. **Role gate:** if the entry requires `superadmin`, check the cookie's role claim
   (`User.IsInRole`/`ClaimTypes.Role`); return 403 if absent. This preserves the
   current `.RequireAuthorization(policy => policy.RequireRole("superadmin"))`
   semantics (`Program.cs:347,356,394,577,...`) inside the handler, because a single
   catch-all registration cannot attach per-path `RequireAuthorization`.
3. Builds the upstream `HttpRequestMessage` with the same method, path
   (`/api/{path}`), query string, and request body (buffered for non-streaming,
   `ResponseHeadersRead` for streaming).
4. Identity headers are injected automatically by `InternalKeyHandler`.
5. **Error/status passthrough:** copies upstream `StatusCode`, content type, and
   body stream straight onto the response — this *is* the W2 fix, for free.

The catch-all is registered **last**; with ASP.NET Core endpoint routing, explicit
`MapGet/MapPost` templates are more specific and win over `{**path}`, so hand-written
routes keep working without any precedence hacks.

**Step 3 — migrate one group at a time.** For each group (concept-tags, snapshots,
users, restrictions, comments, activity, agents, …):
1. Add table entries.
2. Verify via the integration tests + manual UI smoke.
3. Delete the corresponding `Map*` blocks from `Program.cs` **and** the now-unused
   `ApiClient` methods.

Each group is an independent commit and independently revertible.

**Step 4 — routes that stay hand-written forever** (real logic, not mechanical
forwarding):
- `GET /api-proxy/article/{id}` — fetches article metadata **and** content in two
  calls and reshapes the response (`Program.cs:170-176`).
- `GET /api-proxy/article/{id}/related` — sorts + paginates server-side
  (`:549-559`).
- `GET /api-proxy/folders/download` — streams a zip, derives filename
  (`:508-518`, uses `DisposingStreamWrapper` at `:829-849`).
- `GET /api-proxy/downloads/{token}` and `POST /api-proxy/downloads/prepare` —
  streaming + content-disposition/`PostRawAsync` (`:520-539`).
- `POST /api-proxy/media/upload` and `GET /api-proxy/media/{id}` — multipart upload
  + cache headers (`:644-678`).
- `POST /api-proxy/import/obsidian` — form parsing + exception mapping (`:656-670`).
- `POST /api-proxy/init/reset` — the one anonymous route (see W5a).
- Any route that derives a *different* response shape than the API returns.

These can still call into a shrinking `ApiClient`, or call the forwarder's low-level
`SendAsync` directly.

**Auth / role gating preservation.** Role gating moves from compile-time
`RequireAuthorization` calls into the table-driven check inside the forwarder. To
prevent an accidental "forgot to gate this prefix" regression, make the table
**deny-by-default**: any prefix not present returns 404 (not a forward) — the same
principle as `CallerScopeMiddleware`'s deny-all. A new superadmin-only route must be
explicitly added to the table.

**Streaming preservation.** Streaming entries stream the body through via
`ResponseHeadersRead` + `Results.File(stream, ...)`, reusing the existing
`DisposingStreamWrapper` to ensure the upstream response is disposed when the
client stream ends.

**Rollback story.** Because catch-all and explicit routes coexist, rolling back a
migrated group = revert that one commit (which only deleted old routes + added table
entries). No big-bang revert is ever needed. Keep the full hand-written layer on a
branch until the last group is migrated and the suite is green.

**Risk.** High *in aggregate*, low *per step*. The dangers: (a) a prefix-match bug
gating the wrong role — mitigated by deny-by-default + per-group tests; (b)
streaming/large-upload edge cases — mitigated by keeping those routes hand-written;
(c) query/body forwarding subtleties (PATCH with body, `MapMethods` at `:472,629`) —
mitigated by forwarding method + body explicitly and testing PATCH routes early in
the migration.

**Verify.**
- `dotnet test tests/BeeMemoryBank.Integration.Tests/` after each group migration.
- A diff-of-diffs sanity check: for each migrated route, capture the old response
  (status + body) and the catch-all response and assert equality.
- Manual UI smoke of the streaming routes (folder download, media, downloads token).
- Confirm the `superadmin`-only routes 403 for a `user`-role cookie.

---

### W5a — Document why `/api-proxy/init/reset` is anonymous  (C5a) — *S / None*

**Problem.** `POST /api-proxy/init/reset` (`Program.cs:729-736`) is the only proxy
route without `.RequireAuthorization()`. It is gated by the master password on the
API side and is almost certainly intentional (lockout/forgotten-password recovery),
but a future reader can't tell intent from accident.

**Approach.** Add a comment block above the route explaining: anonymous by design,
API-side master-password gate is the real control, purpose is lockout recovery.
Optionally add a small constant/flag to make the exception grep-able.

**Verify.** Code review only.

---

### W5b — Self-host FontAwesome and tighten CSP  (C5b) — *M / Low*

**Problem.** The CSP (`Web/Program.cs:123-146`) allows `script-src 'unsafe-inline'`
and an **external** host, `https://maxcdn.bootstrapcdn.com`, in `style-src`/`font-src`
because EasyMDE injects a `<link>` for FontAwesome. An external CDN dependency
contradicts the self-hosted, no-third-party-servers promise of an E2E product.

(Pre-existing latent issue, verified in the bundled JS: `wwwroot/lib/easymde/easymde.min.js`
references `cdn.jsdelivr.net` — its spell-checker fetches dictionaries from that CDN,
which is not in the CSP, so spell-check already fails under the strict policy.
**Decision O6: disable the spell-checker entirely** — set `spellChecker: false` in the
EasyMDE init options wherever the editor is constructed (the Article edit page). One
line, removes a third-party fetch and dead weight; do not self-host dictionaries.)

**Goal.** Eliminate the external host from CSP; move toward nonce-based `script-src`
in a later step.

**Approach.**
1. Self-host the FontAwesome subset actually used by the EasyMDE toolbar (drop the
   `webfonts` + `css` into `wwwroot/lib/fontawesome/`), and point EasyMDE at it.
2. Remove `https://maxcdn.bootstrapcdn.com` from `style-src` and `font-src`
   (`Program.cs:132,134`).
2b. Set `spellChecker: false` in the EasyMDE init options (decision O6) — removes the
   `cdn.jsdelivr.net` dictionary fetch. Confirm `cdn.jsdelivr.net` is absent from any
   network request after this.
3. (Optional follow-up, not blocking) nonce-based `script-src`: generate a per-request
   nonce, emit it in the CSP header, and apply it to inline `<script>` blocks in the
   Razor pages (Article/View, Edit, Folder, Layout…). This removes `'unsafe-inline'`
   from `script-src`. `style-src 'unsafe-inline'` stays required by Shoelace.

**Risk.** Low. Visual regression risk only — verify the editor toolbar icons render.

**Verify.** Manual: open the article editor, confirm all toolbar icons render and no
CSP violations in the browser console. Automated: a Playwright/Maestro-style check
could assert no `maxcdn` requests are issued.

---

### W5c — Resolve the Web→Core dependency / doc contradiction  (C5c) — *S / Low*

**Problem.** `docs/architecture.md:117` states
*"Web has no dependencies on other modules — HTTP calls to Api only"*, but
`Web.csproj:11` references `BeeMemoryBank.Core`, and the Web layer reaches into
Core models — `HardDeleteStatusFilter` (`Web/Program.cs:1,684`, `ApiClient.cs:5,1196`,
`Lock.cshtml.cs:2`; type lives in `Core/Models/HardDeleteModels.cs`) and
`PagedList<>` (`ApiClient.cs:1196,1253`). One of the two (doc or dependency) is
wrong.

**Goal.** Make the documented architecture and the real build match.

**Decision (O4): take Option A now — fix the doc.** Reassess Option B only as the
closing commit of W1, and only if W1 actually removed the last `Core` reference.

- **Option A (do this now):** Edit `docs/architecture.md`. In the dependency graph
  (lines 111-118) change the last line from
  `(Web has no dependencies on other modules — HTTP calls to Api only)` to:
  `Core ← Web (shared DTO/enums only; no business logic)`. This is honest about the
  current state; the dependency is read-only model reuse
  (`HardDeleteStatusFilter` in `Core/Models/HardDeleteModels.cs:16`, `PagedList<>`),
  not business logic.
- **Option B (do NOT do now — only revisit post-W1):** Having Web own its own wire
  DTOs/enums and dropping the `Core` reference is the cleaner end state, but doing it
  before W1 is churn for no safety gain. If, after the full W1 migration, no
  hand-written route references a `Core` type, drop the reference then as W1's final
  commit and flip the doc line to remove `Core ← Web`.

**Verify.** `dotnet build` of the Web project after the change; doc review.

---

## 4. Suggested ordering / phasing

Ordered for best risk/reward — small, high-value security wins first; the large
refactor staged last when everything around it is already cleaner.

**Phase 0 — Quick wins (low risk, do first).**
- **W4** (S, Low) — structural internal-key gate. Pure safety improvement; makes the
  API layer safer before touching anything else.
- **W5a** (S, None) — document the anonymous reset route.
- **W5c** (S, Low) — at minimum fix the doc (Option A) now; revisit dependency after W1.

**Phase 1 — Security hardening (medium).**
- **W3** (M, Medium) — session revocation. Ship Option B (short cookie) as an
  immediate stopgap, then Option A (security stamp) as the real fix. This is the top
  security gap and is independent of W1/W2.
- **W2** (S, Low) — standardize error propagation on the surviving hand-written
  routes. (Most of C2 is auto-fixed by W1; do the residual after W1, or before as a
  standalone cleanup of the obvious 502 cases.)

**Phase 2 — The big refactor (staged carefully).**
- **W1** (L, High) — catch-all forwarder. **Autonomous pass builds the infrastructure
  and migrates only the `concept-tags` pilot group, then STOPS** (see the W1 guardrail
  block). Remaining group migrations are a supervised follow-up, one group per commit
  with full integration tests after each. Keep special-case routes hand-written. Do
  this *after* W4 (the API is safer) and *after* W3 (auth is settled, so the
  forwarder's role-gating logic is built against the final auth model).

**Phase 3 — Cleanup (low risk).**
- **W5b** (M, Low) — self-host FontAwesome, drop the CDN from CSP; optional nonce
  CSP follow-up.
- Delete dead `ApiClient` methods once W1 has removed their last caller.

---

## 5. Out of scope — do NOT do

- **No rewrite.** The two-process proxy model stays. Web stays a stateless HTTP
  shell; the API stays the sole data owner.
- **No merging Web into the API** (or vice versa). The CLI, mobile, and MCP
  consumers rely on the API being independently usable; collapsing the Web shell
  would re-couple presentation to data ownership.
- **No removing the defense-in-depth.** The Web role gate **and** the API
  superadmin re-check both stay (W1 moves the Web gate into the route table; it does
  not remove it). The internal-key gate becomes structural (W4), not optional.
- **No YARP/big-bang replacement in W1.** The migration is incremental and
  revertible per route group; the catch-all is hand-written (decision O1, §0).
- **No new crypto, session, or sync semantics.** This plan is about the *plumbing*,
  not the trust model, which the review confirmed is correct.
- **No changes to the mobile app, CLI, or MCP wiring** as part of these
  workstreams — they consume the API directly and are unaffected by Web-layer
  refactors (W3's security stamp is API + Web only).

---

## 6. Decisions

All questions that were open in the draft are now **locked** — see **§0 Decisions**
at the top of this document (O1 catch-all vs. YARP; O2 deny-by-default; O3 short
cookie then security stamp; O4 fix the doc now; O5 in-group opt-out marker; O6 disable
spell-checker). This section is retained only as a pointer; there are no open
questions. The implementer must not re-open them.
