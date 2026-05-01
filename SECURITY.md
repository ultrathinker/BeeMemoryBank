# Security Policy

## Encryption Architecture

BeeMemoryBank uses a **3-layer envelope encryption** model designed to protect user data at rest and in transit. No unencrypted content is ever stored on disk.

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

- All article content is stored encrypted in SQLite
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
