# Encryption & Key Management

## Overview

The entire encryption system solves one problem: article texts must be unreadable without the master password. At the same time: title-based search works without the password, synchronization between nodes works without the password, and changing the password does not require re-encrypting all articles.

This is achieved through **envelope encryption** — three levels of keys, where each level encrypts the next.

## Three Levels of Keys

```
Level 1: Master password (in the user's head)
    │
    ▼ Argon2id(password, salt, 64MB, 3 iter, 4 threads)
    │
Level 2: KEK → decrypts → Master DEK (one per entire node network)
    │
    ▼ AES-256-GCM unwrap
    │
Level 3: Master DEK → decrypts → Article DEK (unique per article)
    │
    ▼ AES-256-GCM unwrap
    │
    Article DEK → decrypts → article plaintext
```

**Why three levels instead of one?**
- Password change: re-encrypt a single Master DEK (one AES-GCM operation), articles are untouched
- Per-article DEK: compromising one article does not expose the rest
- Agents: a **superadmin's** agent stores the Master DEK encrypted with its API key — another "entry point" to the same DEK, no more privileged than that superadmin's own web login. An ordinary user's agent stores no such thing at all (see the Agent section below) — it is not an "entry point" to the vault, only to whatever content the vault already has decrypted for someone else.

## Database Storage

### Key Slots (`tbl_key_slot`)

Every entry point that can unlock the vault — each user, each recovery key — owns one row in `tbl_key_slot` containing the Master DEK wrapped with a key derived from that entry point's secret.

```sql
-- One slot per unlock pathway
slot_type:           "user"          -- per-user slot; tbl_user.key_slot_id → tbl_key_slot.slot_id
                     "recovery"      -- recovery key slot (issued separately)
                     "password"      -- legacy single-slot type, only on pre-A2 nodes
encrypted_master_dek: BLOB           -- AES-256-GCM(master_dek, kek), 48 bytes (32 + 16 tag)
iv:                  BLOB            -- 12 bytes (GCM nonce)
salt:                BLOB            -- 32 bytes (Argon2id salt, unique per slot)
argon_memory:        65536           -- 64 MB
argon_iterations:    3
argon_parallelism:   4
```

**Important:** The salt is randomly generated per slot. Even with the same password on two nodes, the KEK will differ. The Master DEK is the same across the network (transferred during `join`).

**Multi-user model:** every active user with login access has a `tbl_user.key_slot_id` pointing to their personal slot. This lets two superadmins unlock the same vault with different passwords. Adding a user creates a new `tbl_key_slot` row wrapping the same Master DEK with the new user's KEK.

**Legacy "password" slot:** before the password unification (Phase A2), every node had a single shared `slot_type='password'` row. New nodes initialize directly with `user`-type slots; existing nodes are migrated transparently on the first successful unlock by `LegacyPasswordSlotMigrationService`. See [Password Unification](#password-unification) below.

### Article Body (`tbl_article_body`)

```sql
article_id:    TEXT  -- FK → tbl_article
ciphertext:    BLOB  -- AES-256-GCM(plaintext, article_dek)
iv:            BLOB  -- 12 bytes
encrypted_dek: BLOB  -- AES-256-GCM(article_dek, master_dek)
dek_iv:        BLOB  -- 12 bytes
```

Each article has its own random DEK (32 bytes). On creation: `article_dek = SecureRandom(32)` → encrypt body → wrap DEK.

### Article Version History (`tbl_article_version`)

Each article version stores its own DEK, separate from the current article's DEK. This allows the full version history to remain decryptable even as the current article's DEK changes over time.

```sql
article_id:    TEXT  -- FK → tbl_article
version_number: INTEGER -- monotonically increasing
encrypted_body: BLOB  -- AES-256-GCM(plaintext, version_dek)
iv:            BLOB  -- 12 bytes
encrypted_dek: BLOB  -- AES-256-GCM(version_dek, master_dek)
dek_iv:        BLOB  -- 12 bytes
updated_by:    TEXT  -- actor who made this version
```

Versions are created automatically on every article update. The same envelope encryption pattern applies: master DEK → unwrap article version DEK → decrypt body. Source references: `libs/BeeMemoryBank.Core/Models/ArticleVersion.cs` and `libs/BeeMemoryBank.Sync/EventApplier.cs`.

### User Key Slots (`tbl_user`)

```sql
role:               TEXT  -- "superadmin", "user"
password_hash:      TEXT  -- Argon2id hash for login authentication
encrypted_dek:      BLOB  -- AES-256-GCM(master_dek, kek_from_user_password) — only for superadmin
dek_iv:             BLOB  -- 12 bytes
dek_salt:           BLOB  -- 32 bytes (Argon2id salt)
```

Each superadmin has their own key slot wrapping the Master DEK with their password. This allows multiple people to unlock the system independently. Users with the "user" role can only log in when the system is already unlocked — they have no key slot.

**Promotion is deferred, not instant.** Building a key slot requires the target user's *plaintext* password, which the admin doing the promoting does not have. So promoting an existing user to superadmin only changes their role: `key_slot_id` stays `NULL`, and the slot is created at the promoted user's next successful login (`UserService.ProvisionMissingKeySlotAsync`, called from `/api/session/login`), or earlier if an admin resets their password. Provisioning commits through a conditional `UPDATE … WHERE key_slot_id IS NULL AND role = 'superadmin' AND is_active = 1`, so two concurrent logins cannot each leave a slot behind — an orphaned slot would keep answering to that password forever, surviving every later rotation.

Because "another superadmin exists" no longer implies "another superadmin can unlock", demoting or deleting a user refuses to drop their slot unless some **other active superadmin still holds one** (`EnsureAnotherSuperadminHoldsAKeySlotAsync`). Counting rows in `tbl_key_slot` is not equivalent and was the earlier, weaker check: a `recovery` slot opens only with the recovery key, and an `os_auto_unlock` slot has no KDF parameters at all so `UnlockAsync` skips it — either one pads the count past the guard while leaving nobody able to unlock with a password. For the same reason, `KeyManagementService.RemoveSlotAsync` clears `tbl_user.key_slot_id` for the slot it deletes: a dangling id makes the user look provisioned and silently suppresses re-provisioning at their next login.

### Agent (`tbl_agent`)

```sql
key_prefix:     TEXT  -- "bee_a1b2c3d4" (first 12 characters for UI display)
key_hash:       TEXT  -- SHA256(full_api_key) — for database lookup
encrypted_dek:  BLOB  -- AES-256-GCM(master_dek, derived_key); NULL unless owner is a superadmin
dek_iv:         BLOB  -- 12 bytes; NULL alongside encrypted_dek
salt:           BLOB  -- 32 bytes (v1 only); NULL for legacy v0 agents, and NULL alongside encrypted_dek
kdf_version:    INT   -- 0 = legacy SHA256, 1 = HKDF-SHA256, 0 also when there's no wrapped DEK at all
```

**H6 fix — wrapping is opt-in by owner role, not universal.** Every agent used to get a wrapped
Master DEK regardless of who owned it, which made an ordinary, folder-restricted user's
self-service agent key (limit 20 per user) cryptographically a key to the *entire* vault — the
folder ACL and read-only flag are enforced only in software over already-decrypted content, not
by the key material itself. Now `encrypted_dek`/`dek_iv`/`salt` are only populated when the
agent's owner is a superadmin at creation time (`AgentEndpoints.MapPost "/"`,
`AgentCommand.HandleCreateAsync`) — a superadmin can already unlock the vault through the web UI,
so their agent doing it too adds no capability an attacker didn't already have by compromising
that person. Every other owner gets `NULL` in all three columns: `Agent.CanAutoUnlock` is false,
`AgentAuthMiddleware` never attempts to decrypt anything for that row, and the key authenticates
exactly as before — it just can't unlock a locked vault, and a stolen copy of the database file
yields nothing usable from its row alone. Migration `014_agent_dek_optional.sql` retroactively
clears these three columns (and resets `kdf_version` to 0) for every pre-existing agent whose
owner isn't a superadmin; this is irreversible (there is no way to re-wrap without the plaintext
API key, which is shown only once at creation). Demoting a superadmin
(`UserService.UpdateUserAsync`) does the same clearing live, for the same reason. **Promoting** a
user to superadmin does NOT retroactively wrap their existing agents' keys either, for the exact
same reason (no plaintext API key to derive from) — only a newly created agent, made after the
promotion, gets wrapped.

**KDF v1 (current):** `derived_key = HKDF-SHA256(api_key, salt=tbl_agent.salt, info="bmb-agent-dek-v1")` with a per-agent random 32-byte salt. The salt prevents pre-computation: an attacker who steals the database AND a leaked api_key from one agent cannot precompute keys for any other agent.

**KDF v0 (legacy):** `derived_key = SHA256(api_key || "bmb-encrypt")`. Still accepted on read for agents created before the migration; new agents are always v1. `AgentAuthMiddleware` dispatches by `kdf_version` so a single API surface handles both — but only ever for a row that has a wrapped DEK at all (see the H6 note above).

**Key point:** `key_hash != derived_key`.
- `key_hash = SHA256(api_key)` — for lookup
- `derived_key = HKDF(api_key, salt, info)` — for AES-GCM (v1)

Even with database access (key_hash + salt), the derived key cannot be reconstructed without the original api_key (SHA256 is irreversible, key has 128 bits of entropy).

### Node Identity (`tbl_node_identity`)

The Ed25519 private key used to sign sync events is stored encrypted under the master DEK:

```sql
ed25519_public_key:    BLOB  -- 32 bytes, plaintext (used for verification by peers)
ed25519_private_key:   BLOB  -- v=0: raw 32-byte seed (legacy); v=1: AES-256-GCM(seed, master_dek)
ed25519_private_key_iv: BLOB -- 12 bytes (v=1 only)
ed25519_private_key_v:  INT  -- 0 or 1
```

**Why encrypted:** Without it, a rooted attacker who exfiltrated `beememorybank.db` could sign arbitrary sync events as this node and propagate hard-deletes / restore-network across the cluster.

**Migration:** `UpgradePrivateKeyToV1Async` runs on every successful unlock — fresh nodes are always created v=1 (`InitializationService` and the mobile `NodeSetupService.JoinAsync` both call `NodeIdentityCrypto.EncryptPrivateKey` before persist). Legacy v=0 nodes get upgraded the first time the user unlocks. The `SignWithIdentity` helper dispatches by `Ed25519PrivateKeyV`.

### Sentinel (`tbl_node_identity.sentinel_value`)

```sql
sentinel_value: BLOB  -- AES-256-GCM("BeeMemoryBank", master_dek)
```

**Purpose:** Allows verifying Master DEK compatibility between nodes without decrypting data.
- During join: the sentinel is transferred along with the key slot
- During sync: can verify that the local DEK is compatible with the remote sentinel
- `decrypt(sentinel, local_master_dek) == "BeeMemoryBank"` → DEK matches

The `sentinel_value` column is part of the initial schema (`001_initial_schema.sql`).

## SessionService — Master DEK Lifecycle in Memory

```csharp
public class SessionService
{
    private byte[]? _masterDek;  // null = locked
    private readonly object _lock = new();

    // Unlock via password (Web UI Login)
    public async Task<bool> UnlockAsync(string password)
    {
        // Argon2id(password, salt) → KEK → unwrap Master DEK → _masterDek
    }

    // Unlock via agent (Bearer token → encrypted DEK)
    public void UnlockWithDek(byte[] masterDek)
    {
        lock (_lock) { _masterDek = masterDek; }
    }

    // Lock
    public void Lock()
    {
        lock (_lock) { Array.Clear(_masterDek!); _masterDek = null; }
    }

    // Get DEK (returns a copy — caller zeroes it out)
    public byte[] GetMasterDek() => _masterDek?.ToArray()
        ?? throw new InvalidOperationException("Session locked");
}
```

**Security:** The Master DEK exists only in RAM. `Array.Clear()` zeroes the bytes. On process restart, the DEK is lost and a new unlock is required.

## Password Unification

Before the unification (legacy nodes), every BeeMemoryBank install had a single shared key slot of `slot_type='password'`. Anyone who knew the master password could unlock the vault, but there was no per-user separation: the database had no concept of "Bob's slot" vs "Alice's slot".

Phase A introduced per-user slots:

- **A1 (server-side migration).** `LegacyPasswordSlotMigrationService` runs on the first successful `UnlockAsync` call. If the node still has a legacy `slot_type='password'` row, the service either (a) deletes it when a `user`-type slot already exists, or (b) promotes the legacy slot by creating a synthetic admin user (`tbl_user` row with role=superadmin, `key_slot_id` pointing at the existing slot). The migration runs OUTSIDE the password-mismatch catch path, so a migration failure surfaces as a 500 error rather than being silently misclassified as "wrong password".
- **A2 (fresh nodes never create legacy slots).** `InitializationService.InitializeAsync` now creates a `slot_type='user'` slot directly bound to the initial superadmin user, plus the corresponding `tbl_user` row. New nodes never carry the legacy slot at all. The `tbl_migration_marker` table records a `legacy_password_unified` marker so the A1 migration is a no-op on these nodes.

After unification:

- Each active user with login access owns one `tbl_key_slot` row of type `user`.
- `KeyManagementService.ChangePasswordAsync` rotates a user's slot in place: derives a new KEK from the new password, wraps the existing Master DEK with it, swaps the slot's `encrypted_master_dek`/`iv`/`salt`. The Master DEK itself is unchanged. Other users' slots are untouched.
- Recovery keys live as separate `slot_type='recovery'` rows. They're issued via the Admin UI; the user receives the key once and stores it offline. A recovery slot can be used like a password slot to unlock the vault, then the admin should rotate the master password and re-issue the recovery key.

`AddPasswordSlotAsync` is whitelisted to `["user", "recovery"]` slot types only — it cannot create the legacy `password` type.

## DEK Rotation

DEK rotation replaces the Master DEK — the single key that wraps all per-article and per-media DEKs. Reasons to rotate:

- **Key compromise:** if the Master DEK is suspected leaked, rotation re-encrypts every wrapped DEK with fresh key material.
- **Periodic key hygiene:** limits the blast radius of an undetected compromise.

### Confidential per-peer envelopes (ADR 0006, since 2026-09-04)

The new DEK is no longer wrapped under the *old* DEK for transport — that scheme meant every
current **and revoked** peer already had the material to unwrap every future rotation, since
`tbl_event.payload` is plaintext JSON replicated to every node (see "What is NOT Encrypted" below).
The initiator now seals the new DEK once per currently-trusted peer, in a per-peer X25519 envelope
derived from the Ed25519 identity keys every node and whitelist row already carries — no new key
material needed, and a revoked peer simply receives no envelope it can open:

```
DekEnvelope.Build (per trusted peer):
  peer Ed25519 seed/pubkey → X25519 (birational map; BouncyCastle
                                      Ed25519.ValidatePublicKeyFull rejects any
                                      off-curve/small-order/garbage key rather than
                                      silently deriving a usable-but-wrong secret)
  HKDF-SHA256(shared_secret, salt=rotation_id, info=recipient_node_id) → envelope_key
  AES-256-GCM(newDek, envelope_key) → this peer's envelope
```

The old `AES-256-GCM(newDek, oldDek)` field survives only as a fallback for a peer that hasn't
upgraded past this change; it is nullable and omitted on an all-upgraded mesh. See
`docs/adr/0006-confidential-dek-rotation-x25519-envelopes.md` for the full design rationale,
including what this does and does not fix (a rotation whose commit already shipped under the old
scheme is not retroactively protected).

### Three-Step Flow (Initiator Node)

The rotation is initiated by a superadmin on one node and propagates to all peers via sync events.

**1. Propose** (`POST /api/dek-rotation/propose`)

- Verifies the master password against the current DEK.
- Generates a new random 32-byte DEK.
- Builds a per-peer X25519 envelope for every currently-trusted peer (see above); a peer with an
  unusable/malformed identity key is excluded from *this* rotation's envelope set — loudly logged —
  rather than aborting the rotation for the entire mesh. The initiator itself is never excluded.
- Reads current `dek_epoch` from `tbl_node_identity`, increments by 1.
- Emits a `dek_rotation_proposed` sync event carrying the envelopes, the new epoch, and a 24-hour expiry.
- Immediately emits a `dek_rotation_commit` event referencing the proposed event ID (MVP: no quorum wait).

**2. Accept** (`POST /api/dek-rotation/accept`)

Returns `202 Accepted` immediately; the destructive work runs in the background. Progress is polled via `GET /api/dek-rotation/progress`.

The accept phase:

1. Creates a **pre-rotation snapshot** automatically (`VACUUM INTO`).
2. `DekRotationMaterial.ResolveNewDek` opens this node's own envelope from the commit payload (or,
   for a legacy pre-ADR-0006 sender, unwraps the fallback `encrypted_new_dek` with the old DEK).
3. Verifies the admin's password a second time (prevents a typo from destroying the vault).
4. **Re-wrap** inside a single SQLite transaction:
   - Walks `tbl_article_body`, `tbl_article_version`, `tbl_conflict_version`, `tbl_media`, the
     embedding projection matrix, and the node's own Ed25519 identity seed — for each row, tries the
     old DEK first (the normal case: unwrap and re-wrap with `newDek`), then the new DEK (the row was
     already re-wrapped by a peer that raced ahead — left alone), and only if neither opens it is the
     row counted as unreadable and skipped, rather than aborting the whole transaction. Rows framed
     before DEK versioning existed (pre-`v1`, no AAD) keep that original framing instead of being
     silently relabelled `v1` and permanently corrupted. Uses keyset pagination (500 rows/batch) for
     linear performance. A completion tally reports rewrapped / already-on-new-key / unreadable counts
     by name, rather than a bare pass/fail.
   - Deletes all rows from `tbl_agent` (agents hold DEKs encrypted with the old Master DEK; the server cannot re-wrap them without the plaintext API keys).
   - Re-wraps the initiator's key slot with the new DEK. **Deletes all other key slots** (users must re-register).
   - Deletes recovery-type key slots.
   - Persists the chain material `LazySlotRewrapService` needs to walk this rotation later (see below), inside the same transaction that marks the rotation `Applied`.
   - Updates `tbl_node_identity`: new sentinel + new `dek_epoch`.
   - Marks the rotation state as `APPLIED` inside the same transaction.
5. Swaps the in-memory Master DEK in `SessionService`.
6. Runs a post-rotation compaction (log cleanup, non-fatal if it fails).

**Why a single transaction?** A partial state where some rows genuinely needing a rewrap end up
split between the old and new DEK is unrecoverable — the sentinel can only verify one DEK. Atomic
commit-or-rollback ensures consistency for those rows; a row that turns out to already be on the new
key (or unreadable under either) no longer forces the whole transaction to roll back, which is what
used to make a single stale/raced row brick the entire rotation permanently.

**3. Cancel** (`POST /api/dek-rotation/cancel/{eventId}`)

Cancels a proposed or committing rotation before the destructive phase completes. Sets state to `Cancelled`.

### `dek_epoch`

A monotonic integer in `tbl_node_identity` (starts at 1, incremented on each rotation). Purpose:

- **Replay shield — partially implemented.** `EventLogger` now writes the node's real
  `tbl_node_identity.dek_epoch` into `ArticleEventPayload`/`MediaEventPayload` (fixed 2026-09-04;
  it used to write the literal `1` on every event regardless of the node's actual epoch). No
  applier acts on the field yet, though — nothing today detects or rejects an event encrypted under
  a different epoch than the receiver's, which is why a peer that receives a post-rotation article
  before it applies the rotation itself still stores a DEK it cannot later unwrap. Treat "applier
  enforcement" as a design intent, not yet a property of the running system.
- **Progress indicator:** the UI shows "Epoch 3 → 4" during rotation.

### Sentinel and `VerifySentinel`

The sentinel is `AES-256-GCM("BeeMemoryBank", masterDEK)` with a **fresh random IV on every `ComputeSentinel` call**. This means:

- `ComputeSentinel(dek) == ComputeSentinel(dek)` is **always false** (different IVs).
- Comparison must use `MasterKeyManager.VerifySentinel(storedSentinel, candidateDek)`, which decrypts the sentinel with the candidate DEK and checks if the plaintext matches "BeeMemoryBank".

During login, the sentinel is used to detect whether the user's key slot is wrapped with a DEK that differs from the current node DEK — triggering lazy slot rewrap.

### Lazy Slot Rewrap

When DEK rotation completes on a peer node via auto-accept (or manual peer-accept), the peer's existing user key slots remain in place but are still wrapped with the old DEK. **Only `tbl_agent` rows and `recovery`-type slots are deleted** on the peer — user slots are deliberately preserved so that on the next login, lazy rewrap can transparently migrate them to the new DEK. (Initiator-side acceptance is different: there, all OTHER user slots are dropped because the initiator's local users are the canonical set.) When a peer's user logs in after auto-accept, the system detects a sentinel mismatch:

- `LazySlotRewrapService.TryRewrapAsync()` walks `tbl_dek_rotation_state` rows with state `Applied`, sorted by creation time.
- For each rotation, it unwraps the next DEK from the commit event payload using the current candidate DEK.
- After each step, it calls `VerifySentinel(currentSentinel, candidateDek)`. When this returns true, the chain has reached the current DEK.
- The user's key slot is re-wrapped with the current DEK. Transparent — no user action required.

**Chain persistence (2026-09-04).** This walk used to read every link out of the
`dek_rotation_commit` event in `tbl_event` — but event-log compaction deletes exactly those rows
(the initiator runs a compaction right after its own rotation), and once the commit row was gone
the walk could never start: the affected user could never unlock that node again, no retry, no
recovery short of restoring a backup. Migration 020 adds `chain_encrypted_new_dek`/`chain_iv` to
`tbl_dek_rotation_state` — a local table that is never synced and that compaction never touches —
and the rewrap writes them in the same statement that marks a rotation `Applied`. The read side
prefers this local copy and falls back to the event log only for a rotation applied before this
migration existed.

### Rotation State Machine

Stored in `tbl_dek_rotation_state`:

| State | Meaning |
|---|---|
| `Proposed` | PROPOSED event received, waiting for COMMIT |
| `Committing` | COMMIT event received, waiting for accept |
| `Applied` | Rotation completed successfully |
| `Cancelled` | Admin cancelled before destructive phase |
| `Failed` | Destructive phase threw an exception |
| `Rejected` | Peer admin rejected the rotation |

### Audit Trail

Every rotation action is logged to `tbl_audit_log`: propose, accept, cancel, peer-accept, peer-reject, auto-accept. Entries include the commit event ID, initiator user, and pre-rotation snapshot filename.

## What is NOT Encrypted (by design)

| Data | Why it's in plaintext |
|---|---|
| title | Navigation and search without unlock |
| tags | Search and filtering |
| treePath | Tree-based navigation |
| timestamps | Sorting, activity feed |
| media file name, content type, size | Attachment lists without unlock (the bytes are encrypted) |
| sync event metadata | Lamport clock, node_id, event_type |
| sync event payload | `tbl_event.payload` is plaintext JSON — see below |

**Comments are encrypted**, and are deliberately not in the table above. `CommentService.CreateAsync`
encrypts every comment with the parent article's DEK and stores it as `tbl_comment.ciphertext` with
`encrypted = 1` (the legacy `text` column is left empty). Reading one costs exactly what reading the
article costs: an unwrapped master DEK.

**The event log is plaintext, and it carries the same metadata the tables do.** `tbl_event.payload`
is a plain JSON string — nothing wraps it — so every metadata field a synced entity has travels in
the clear next to the ciphertext it describes. `article_create` / `article_update` payloads carry
`title`, `tree_path` and `concept_tags` (`ArticleEventPayload`); folder events carry the folder path
and name; `media_create` carries the file name and content type; concept-tag events carry the tag
names. Only bodies travel encrypted — as a `ciphertext_sha256` reference to a blob, or inline base64
on pre-protocol-2 events — and a comment event carries the comment's ciphertext, never its text.
Signing (Ed25519) protects the payload's integrity, not its confidentiality.

**Trade-off:** A leaked SQLite file reveals topics and structure, but NOT article texts. A leaked
event log on its own reveals the same, and so does a whitelisted peer that never does anything but
pull: the event stream alone is enough to reconstruct the whole tree, every title and every tag.

## Media Encryption (Images)

Images follow the same envelope encryption pattern as article bodies — each image gets its own random DEK.

### Storage Layout

```
{dataPath}/
  beememorybank.db         ← metadata (tbl_media: IV, encrypted_dek, dek_iv)
  media/
    {guid}.enc             ← AES-256-GCM ciphertext (includes 16-byte auth tag)
```

### Database Schema (`tbl_media`)

```sql
id              TEXT PRIMARY KEY    -- GUID
article_id      TEXT                -- FK → tbl_article (nullable: upload before save)
file_name       TEXT                -- original filename (sanitized)
content_type    TEXT                -- MIME type (allowlist: png, jpeg, gif, webp, svg+xml)
file_size       INTEGER             -- original plaintext size (max 5 MB)
encrypted_dek   BLOB                -- AES-256-GCM(media_dek, master_dek)
dek_iv          BLOB                -- 12 bytes (nonce for DEK wrapping)
iv              BLOB                -- 12 bytes (nonce for content encryption)
status          TEXT                -- 'A' (active) or 'D' (soft-deleted)
```

### Encryption Flow

1. `mediaDek = SecureRandom(32)` — unique 32-byte random key per image
2. `(ciphertext, iv) = AES-256-GCM(plaintext, mediaDek)` — encrypt image bytes
3. `(encryptedDek, dekIv) = AES-256-GCM(mediaDek, masterDek)` — wrap DEK
4. Write `ciphertext` to `media/{guid}.enc` on disk
5. Store `iv`, `encryptedDek`, `dekIv` in `tbl_media`
6. `Array.Clear(mediaDek)` — zero out key material

### Decryption Flow (on-the-fly, per request)

1. Load metadata from `tbl_media`
2. Read `media/{guid}.enc` from disk
3. `mediaDek = AES-256-GCM.Unwrap(encryptedDek, dekIv, masterDek)`
4. `plaintext = AES-256-GCM.Decrypt(ciphertext, iv, mediaDek)`
5. Return with `Content-Type` and `Cache-Control: private, max-age=31536000, immutable`

### Markdown Integration

In article markdown, images are stored as `![alt](/api/media/{guid})`. The Web UI proxy rewrites these URLs to `/api-proxy/media/{guid}` for browser access. MCP/CLI clients use the `/api/media/{guid}` URL directly.

### Sync

Media sync events carry Base64-encoded ciphertext in the JSON payload. A 5 MB image produces ~6.7 MB of Base64 data. This is acceptable for a knowledge base but not designed for photo albums.

### Cleanup

- **Soft-deleted media** (article cascade delete): purged after 30 days by CleanupService
- **Orphaned media** (uploaded but never linked to an article): purged after 24 hours

## Full-Text Content Search (Streaming Decryption)

When the session is unlocked and a user opts into content search, SearchService decrypts article bodies to search plaintext. The active-body set is scanned with a **single, streaming read** over one long-lived SQLite connection rather than windowed batches:

1. A single producer iterates `StreamActiveAsync()` — an unbuffered `DbDataReader` over the whole active-body set on one connection. SQLite in WAL mode pins a consistent snapshot for the life of that one statement/connection, so concurrent creates/soft-deletes on other connections can't shift a row out of the read (the failure mode the former `LIMIT`/`OFFSET` batches over fresh connections had).
2. Rows are pushed onto a bounded `Channel<EncryptedArticleBody>` (backpressure so the full ciphertext set is never materialized in memory ahead of decryption).
3. `ProcessorCount - 1` (min 1) worker tasks pull from the channel and do, per item: unwrap article DEK → decrypt → skip protected bodies → substring match. A per-item try/catch isolates corrupt/incompatible bodies — one bad body can't break the scan.
4. The master DEK is obtained once before the producer/workers start and cleared in `finally` after `Task.WhenAll(workers)` so it's wiped exactly once, only after every worker is done with it.

This keeps memory usage proportional to the channel capacity, not total article count.

## Cryptographic Files (BeeMemoryBank.Crypto, ~450 LOC)

| File | LOC | Purpose |
|---|---|---|
| `AesGcmHelper.cs` | ~50 | Encrypt/Decrypt with AES-256-GCM (12B nonce, 16B tag) |
| `MasterKeyManager.cs` | ~40 | Generate (32B random), Wrap/Unwrap Master DEK |
| `DekManager.cs` | ~30 | Wrap/Unwrap per-article/media DEK |
| `KeyDerivation.cs` | ~35 | Argon2id: password + salt → 32B KEK |
| `ArticleEncryptor.cs` | ~50 | High-level: encrypt body + wrap DEK |
| `MediaEncryptor.cs` | ~15 | Thin wrapper over AesGcmHelper for image encryption |
| `Ed25519Signer.cs` | ~40 | Generate keypair, Sign, Verify (BouncyCastle) |
| `AgentKeyHelper.cs` | ~50 | API key generation (`bee_` + hex), hash, DEK encrypt/decrypt |
| `SecureRandom.cs` | ~15 | CSPRNG wrapper |
| `CryptoConstants.cs` | ~20 | Key sizes, default Argon2id parameters |
