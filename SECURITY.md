# Security Policy

## Encryption Architecture

BeeMemoryBank uses a **3-layer envelope encryption** model designed to protect article, version, and media content at rest and in transit. Titles, folder paths and names, concept tag names, timestamps, the FTS5 search index, sync event-log payloads, and audit-log details are stored in plaintext by design — they're metadata that folder ACLs, search, and sync all need as fast, plain lookups rather than per-row decrypt operations. This is a deliberate, documented trade-off, not an oversight: see [ADR-0005](docs/adr/0005-plaintext-metadata.md) for the exact table-by-table split and what it means for someone who obtains the raw database file.

### Encryption Layers

```
Password ──Argon2id──▶ KEK (Key Encryption Key)
                            │
                     AES-256-GCM wrap
                            │
                            ▼
                      Master DEK (Data Encryption Key)
                            │
                     AES-256-GCM wrap
                            │
                            ▼
                      Article DEK (per-article)
                            │
                     AES-256-GCM encrypt
                            │
                            ▼
                      Article Content (ciphertext)
```

### Cryptographic Primitives

| Component | Algorithm | Parameters |
|---|---|---|
| Symmetric encryption | AES-256-GCM | 256-bit key, 96-bit nonce, 128-bit tag |
| Key derivation (Password → KEK) | Argon2id | 64 MB memory, 3 iterations, 4 parallelism (OWASP) |
| Key derivation salt | CSPRNG | 256-bit (32 bytes) |
| Digital signatures (sync) | Ed25519 | 32-byte seed private key, 32-byte public key |
| All random values | `RandomNumberGenerator` | .NET CSPRNG |

### Per-Article DEK

Each article is encrypted with a unique Data Encryption Key (DEK). This means:

- Compromising one article's DEK does not affect other articles
- Articles can be re-encrypted individually without touching the master key
- Article DEKs are wrapped (encrypted) with the Master DEK and stored in the database

### Multi-Slot Key System

BeeMemoryBank uses a LUKS-style multi-slot key system stored in `tbl_key_slot`:

- Multiple passwords can wrap the same Master DEK
- Each key slot contains: Argon2id salt, wrapped Master DEK ciphertext, IV, and iteration parameters
- Passwords can be added or changed without re-encrypting any article content
- A sentinel value (`AES-256-GCM("BeeMemoryBank", masterDEK)`) is used to verify correct password entry

### Agent Key Encryption

API agent keys use a separate wrapping mechanism:

- Agent keys are SHA-256 hashed for database lookup
- A derived encryption key (`SHA256(apiKey + "bmb-encrypt")`) wraps the Master DEK for agent access
- This allows programmatic access without storing the user's password

### Data at Rest

- All article, version, and media content is stored encrypted in SQLite (or, for media bytes, as `.enc` files on disk)
- Titles, folder paths/names, tag names, and timestamps are stored in plaintext — see [ADR-0005](docs/adr/0005-plaintext-metadata.md)
- Keys are never persisted in plaintext
- The Master DEK exists only in memory during an active session
- Salt and wrapped key material are stored per-slot for offline brute-force resistance

### Online DEK Rotation

Replacing the Master DEK across an entire network without exporting/re-importing the vault. Reasons to rotate: suspected compromise of the current DEK, periodic key hygiene, or rotating off material that may have transited insecure paths.

**Initiator flow** (one superadmin, one node):

1. **Propose** — verify the master password against the current DEK; generate a fresh 32-byte DEK; wrap it with the current DEK; emit a signed `dek_rotation_proposed` sync event.
2. **Accept** — emit a signed `dek_rotation_commit` event referencing the proposed event; create a **pre-rotation snapshot** automatically (so the rotation is rollback-able to disk); verify the master password a second time before any destructive work; in a single SQLite transaction, walk every per-item DEK in `tbl_article_body` / `tbl_article_version` / `tbl_conflict_version` / `tbl_media`, unwrap with the old DEK and re-wrap with the new one; delete all `tbl_agent` rows (their API keys cannot be re-wrapped server-side); re-wrap the initiator's key slot; update the sentinel and the monotonic `dek_epoch`. Atomicity guarantees the database is never partially rotated. Then `SwapMasterDek` rolls the in-memory DEK over with a 2-second drain window for in-flight readers.

The HTTP `/accept` returns 202 immediately and the work runs in the background; the UI polls `/progress` for status.

**Peer-acceptance protocol.** After the initiator commits, the rotation event propagates through the existing signed sync stream. Each peer's `tbl_whitelist.auto_accept_dek_rotation` toggle controls behaviour:

- **Auto-accept = true** — the peer applies the rotation autonomously the moment the COMMIT event arrives. Strictly necessary checks (PROPOSED was delivered first, signature verifies against the originator's whitelisted public key, peer is not revoked) gate the auto-apply.
- **Auto-accept = false** — the rotation lands in `tbl_dek_rotation_state` as `Committing`; the Admin UI surfaces a banner with **Apply** / **Reject — leave network** buttons. Reject permanently disconnects this node from the rotated network (its DEK now diverges from peers').

**Lazy slot rewrap.** When a peer auto-applies a rotation, it deliberately preserves user key slots (only `recovery`-type slots are dropped); the initiator-side flow drops every other user slot. On the next login on the peer, `SessionService.UnlockAsync` detects a sentinel mismatch and walks the chain of `Applied` rotations in `tbl_dek_rotation_state` — at each step decrypting the next DEK with the previous one — until the candidate matches the current sentinel. The user's slot is then re-wrapped against the latest DEK using their existing KEK. Transparent: no password re-prompt, no admin intervention.

**Sentinel mismatch does not block sync.** When two nodes' Master DEKs differ (a peer rotated, this node hasn't applied yet), the sync layer logs a warning and continues pulling events anyway — otherwise the COMMIT event that would bring the node back into sync could never be delivered (catch-22).

**Crash recovery.** A startup sweep marks any rotation row stuck in `Committing` from THIS node as `Failed`. Peer-originated `Committing` rows are left in place to be retried by a hook in the next successful unlock (`RetryPendingAutoAcceptsAsync`). `Proposed` rows older than 24h are auto-cancelled.

## Trust Model

Read this before you invite a second node onto your network. See also [docs/sync.md](docs/sync.md#trust-model)
for the same picture from the sync layer's side.

### Joining is authorised by the master password, and a joined node is fully trusted

`POST /api/join` takes the master password, tries it against every password-bearing key slot, and — if it opens a slot belonging to an active superadmin (or a legacy pre-user-table `password` slot) — returns the wrapped Master DEK and adds the caller to `tbl_whitelist`. There is no invite token, no per-node approval step, and no "content only" or "read only" kind of join.

The new peer is stored with `is_superadmin = 1`, and that bit travels to every other node inside the `whitelist_add` sync event so the whole mesh agrees on it. `EventApplier` consults exactly that bit before applying the cluster-state-modifying event types — `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `hard_delete` and `restore_network`. So every replica on the network can, by design:

- **Revoke any other peer.** A `whitelist_revoke` event it signs is applied by every node that receives it. A node never revokes *itself* on a remote event, so the revoked peer keeps its own copy of the vault — but every other node stops accepting its events, which is the same thing as being cut out.
- **Hard-delete content network-wide.** `hard_delete` is not a tombstone: on every peer it physically purges the article (or the whole folder subtree) from the database and deletes the matching `.enc` media files from disk. Nothing is left to restore from except a snapshot taken beforehand.
- **Initiate a network-wide restore.** A `restore_network` event replaces the vault on every peer with the initiator's snapshot. Peers with `tbl_whitelist.auto_accept_restore` set apply it unattended; the rest queue it for an admin, and rejecting it permanently disconnects them from the originator's timeline (wipe-and-rejoin to come back). Online DEK rotation propagates through the same peer-acceptance mechanism.

There is currently no way to demote a peer once it has joined: nothing in the API or the Admin UI clears `is_superadmin`. The only remedy for a peer you no longer trust is revoking it (`DELETE /api/whitelist/{nodeId}`), and each node enforces the flag from its own whitelist row, so the revocation has to reach the other nodes to take effect everywhere.

### The master password is the network, not the node

Every consequence above follows from one credential. Anyone who has the master password can stand up their own node, join, and from it revoke your peers, hard-delete your content everywhere, or restore the entire mesh to a snapshot of their choosing — none of which requires access to any machine you own. `/api/join` is also one of the few endpoints that has to be reachable through a reverse proxy (see [docs/deployment.md](docs/deployment.md)), so it is exposed on purpose.

Treat the master password as a root credential shared by every machine in the mesh, and note two things about changing it. Key slots are node-local — `tbl_key_slot` is neither synced nor included in a join snapshot — so a password change applies to the one node you made it on; every other node still accepts the old password at its own `/api/join`. And changing it does not evict a node that already holds the Master DEK, because the DEK itself is unchanged: that needs a revoke, plus a DEK rotation if the DEK may have leaked.

### "Lock" is advisory

`SessionService.IsUnlocked` is a single process-wide flag: one vault state shared by the web UI and every MCP agent on that node. There is no per-user or per-agent session. **Lock** wipes the Master DEK from memory, but two mechanisms put it back with no human involved:

- **A superadmin-owned agent key.** An agent created by a superadmin stores the Master DEK wrapped with its own API key (`Agent.CanAutoUnlock`); an ordinary user's agent stores no key material and cannot do this. `AgentAuthMiddleware` unwraps it and re-unlocks the process on the *next request that key makes* — with a live MCP client attached, that is usually seconds after the Lock. Two things stop it: revoking the agent (its key no longer resolves to a row at all) or deactivating its owner (the request is rejected with 401 before the unlock is even attempted).
- **OS auto-unlock (Windows, opt-in).** `OsAutoUnlockService` keeps a DPAPI-protected random secret in `<data>/os-auto-unlock.dat` that wraps the Master DEK in an `os_auto_unlock` key slot. It is attempted once, at API startup — it does *not* re-unlock after a Lock inside a running process — so its practical effect is that restarting a locked node unlocks it again. DPAPI's `CurrentUser` scope is an OS-user boundary, not a per-application one.

Lock is therefore a real operation with a short half-life unless you also remove what can undo it: revoke or delete the superadmin-owned agent keys, and disable OS auto-unlock if a restart must stay locked.

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a vulnerability in BeeMemoryBank, please report it responsibly.

### How to Report

Send an email to **universeissilent42@gmail.com** with:

1. A description of the vulnerability
2. Steps to reproduce (if applicable)
3. The potential impact
4. Any suggested mitigations

### Response Timeline

- **Acknowledgment:** Within 48 hours
- **Initial assessment:** Within 5 business days
- **Status updates:** Every 7 days until resolution

### Responsible Disclosure Policy

- Do not publicly disclose the vulnerability until a fix has been released
- We will credit researchers who report vulnerabilities (unless you prefer to remain anonymous)
- We ask that you:
  - Avoid accessing or modifying other users' data
  - Do not degrade service availability
  - Provide reasonable time for us to address the issue before any public disclosure

### Supported Versions

| Version | Supported |
|---|---|
| Latest release | Yes |
| Development branch | Best effort |

## Folder-Level Access Control

BeeMemoryBank implements per-folder access control lists (ACLs) that restrict which users and AI agents can read, write, or manage articles within specific folders.

- **Per-folder ACL** — each folder has an independent access list for users and agents
- **Horizontal privilege escalation prevention** — users cannot access folders they are not explicitly granted permission to, even if they manipulate API requests
- **Agent isolation** — AI agents are scoped to their assigned folders and cannot traverse the full tree
- **Server-side enforcement** — ACL checks are performed at the API layer before any data is returned or modified, regardless of client-side UI

## Agent Privilege Escalation Prevention

AI agents authenticated via bearer tokens cannot elevate their privileges through request header manipulation or parameter injection.

- **Role scoping enforced server-side** — agent roles are resolved from the authenticated token, not from request headers
- **No header spoofing** — the API ignores any role or permission headers sent by clients, relying solely on the authenticated session
- **Folder-scoped operations** — agents can only operate on folders explicitly granted to them
- **Audit trail** — all agent operations are logged with the agent identity for post-hoc review

## XSS Prevention

All user-generated content rendered in the Web UI is sanitized to prevent cross-site scripting (XSS) attacks.

- **DOMPurify** — client-side sanitization applied to all Markdown-rendered HTML content
- **Server-side validation** — input validation rejects obviously malicious payloads before storage
- **Defense in depth** — even if an attacker stores malicious script tags, DOMPurify strips them before rendering

## Constant-Time Key Comparison

Internal key validation uses constant-time comparison to prevent timing side-channel attacks.

- **FixedTimeEquals** — used for all internal API key comparisons, preventing attackers from inferring key values through response timing analysis
- **Applied to** — agent bearer tokens, internal Web↔API shared secret (`BMB_INTERNAL_KEY`)

## Web↔API Internal Authentication

The Web server communicates with the API server over a trusted internal network. Admin endpoints (user management, lock/unlock) require a shared secret key (`BMB_INTERNAL_KEY`) to prevent spoofing.

- Set `BMB_INTERNAL_KEY` to the same random value on both the API and Web systemd services
- Generate with: `openssl rand -hex 32`
- If unset, admin endpoints only accept requests from `127.0.0.1` / `::1` (safe only if API is not reachable from outside)

The Web server also requires cookie authentication with the `superadmin` role for all admin pages and proxy routes — the internal key is an additional layer, not a replacement.

## Security Best Practices for Deployments

- Use HTTPS in production (reverse proxy with TLS termination)
- Set strong Argon2id parameters (defaults follow OWASP recommendations)
- Keep your Master DEK recovery mechanism secure
- Regularly update dependencies
- Use LUKS or equivalent full-disk encryption on the host machine
- Set `BMB_INTERNAL_KEY` in production (see above)
