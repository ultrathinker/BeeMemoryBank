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

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a vulnerability in BeeMemoryBank, please report it responsibly.

### How to Report

Send an email to **evgeny.borzenkov@gmail.com** with:

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
