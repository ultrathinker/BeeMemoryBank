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
- Agents: store the Master DEK encrypted with their API key — another "entry point" to the same DEK

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

### Agent (`tbl_agent`)

```sql
key_prefix:     TEXT  -- "bee_a1b2c3d4" (first 12 characters for UI display)
key_hash:       TEXT  -- SHA256(full_api_key) — for database lookup
encrypted_dek:  BLOB  -- AES-256-GCM(master_dek, derived_key)
dek_iv:         BLOB  -- 12 bytes
salt:           BLOB  -- 32 bytes (v1 only); NULL for legacy v0 agents
kdf_version:    INT   -- 0 = legacy SHA256, 1 = HKDF-SHA256
```

**KDF v1 (current):** `derived_key = HKDF-SHA256(api_key, salt=tbl_agent.salt, info="bmb-agent-dek-v1")` with a per-agent random 32-byte salt. The salt prevents pre-computation: an attacker who steals the database AND a leaked api_key from one agent cannot precompute keys for any other agent.

**KDF v0 (legacy):** `derived_key = SHA256(api_key || "bmb-encrypt")`. Still accepted on read for agents created before the migration; new agents are always v1. `AgentAuthMiddleware` dispatches by `kdf_version` so a single API surface handles both.

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

### Three-Step Flow (Initiator Node)

The rotation is initiated by a superadmin on one node and propagates to all peers via sync events.

**1. Propose** (`POST /api/dek-rotation/propose`)

- Verifies the master password against the current DEK.
- Generates a new random 32-byte DEK.
- Wraps `newDek` with `oldDek`: `AES-256-GCM(newDek, oldDek)`.
- Reads current `dek_epoch` from `tbl_node_identity`, increments by 1.
- Emits a `dek_rotation_proposed` sync event carrying the wrapped new DEK, the new epoch, and a 24-hour expiry.
- Immediately emits a `dek_rotation_commit` event referencing the proposed event ID (MVP: no quorum wait).

**2. Accept** (`POST /api/dek-rotation/accept`)

Returns `202 Accepted` immediately; the destructive work runs in the background. Progress is polled via `GET /api/dek-rotation/progress`.

The accept phase:

1. Creates a **pre-rotation snapshot** automatically (`VACUUM INTO`).
2. Unwraps the new DEK from the commit payload using the old DEK.
3. Verifies the admin's password a second time (prevents a typo from destroying the vault).
4. **Destructive re-wrap** inside a single SQLite transaction:
   - Walks `tbl_article_body`, `tbl_article_version`, `tbl_conflict_version`, `tbl_media` — for each row, unwraps the per-item DEK with `oldDek`, re-wraps with `newDek`, updates in-place. Uses keyset pagination (500 rows/batch) for linear performance.
   - Deletes all rows from `tbl_agent` (agents hold DEKs encrypted with the old Master DEK; the server cannot re-wrap them without the plaintext API keys).
   - Re-wraps the initiator's key slot with the new DEK. **Deletes all other key slots** (users must re-register).
   - Deletes recovery-type key slots.
   - Updates `tbl_node_identity`: new sentinel + new `dek_epoch`.
   - Marks the rotation state as `APPLIED` inside the same transaction.
5. Swaps the in-memory Master DEK in `SessionService`.
6. Runs a post-rotation compaction (log cleanup, non-fatal if it fails).

**Why a single transaction?** A partial state where some rows are wrapped with the new DEK and others with the old is unrecoverable — the sentinel can only verify one DEK. Atomic commit-or-rollback ensures consistency.

**3. Cancel** (`POST /api/dek-rotation/cancel/{eventId}`)

Cancels a proposed or committing rotation before the destructive phase completes. Sets state to `Cancelled`.

### `dek_epoch`

A monotonic integer in `tbl_node_identity` (starts at 1, incremented on each rotation). Purpose:

- **Replay shield:** each sync event payload carries `dek_epoch`. Receivers can detect and drop stale events encrypted with a previous DEK.
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
| comments | Discussion without decryption |
| timestamps | Sorting, activity feed |
| sync event metadata | Lamport clock, node_id, event_type |

**Trade-off:** A leaked SQLite file reveals topics and structure, but NOT article texts.

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

## Full-Text Content Search (Batched Decryption)

When the session is unlocked and a user opts into content search, SearchService decrypts article bodies to search plaintext. To avoid loading all encrypted bodies into memory at once:

1. Get total count of active article bodies
2. Fetch in batches of 50 (`GetActiveBatchAsync(limit, offset)`)
3. For each body in the batch: unwrap article DEK → decrypt → search → clear DEK
4. Master DEK is obtained once before the loop and cleared in `finally`

This keeps memory usage proportional to batch size, not total article count.

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
