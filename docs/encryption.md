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

### Password Slot (`tbl_master_key_store`)

```sql
-- One slot per master password; may include a recovery slot
slot_type:           "password"
encrypted_master_dek: BLOB  -- AES-256-GCM(master_dek, kek), 48 bytes (32 + 16 tag)
iv:                  BLOB  -- 12 bytes (GCM nonce)
salt:                BLOB  -- 32 bytes (Argon2id salt, unique per slot)
argon_memory:        65536 -- 64 MB
argon_iterations:    3
argon_parallelism:   4
```

**Important:** The salt is randomly generated during `init` or `join`. Even with the same password on two nodes, the KEK will differ. The Master DEK is the same (transferred during join).

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
role:               TEXT  -- "superadmin", "unlocker", "user"
password_hash:      TEXT  -- Argon2id hash for login authentication
encrypted_dek:      BLOB  -- AES-256-GCM(master_dek, kek_from_user_password) — only for superadmin/unlocker
dek_iv:             BLOB  -- 12 bytes
dek_salt:           BLOB  -- 32 bytes (Argon2id salt)
```

Each superadmin or unlocker has their own key slot wrapping the Master DEK with their password. This allows multiple people to unlock the system independently. Users with the "user" role can only log in when the system is already unlocked — they have no key slot.

### Agent (`tbl_agent`)

```sql
key_prefix:     TEXT  -- "bee_a1b2c3d4" (first 12 characters for UI display)
key_hash:       TEXT  -- SHA256(full_api_key) — for database lookup
encrypted_dek:  BLOB  -- AES-256-GCM(master_dek, SHA256(api_key + "bmb-encrypt"))
dek_iv:         BLOB  -- 12 bytes
```

**Key point:** `key_hash != encryption_key`.
- `key_hash = SHA256(api_key)` — for lookup
- `encryption_key = SHA256(api_key + "bmb-encrypt")` — for AES-GCM

Even with database access (key_hash), the encryption key cannot be derived without the original api_key (SHA256 is irreversible, key has 128 bits of entropy).

### Sentinel (`tbl_node_identity.sentinel_value`)

```sql
sentinel_value: BLOB  -- AES-256-GCM("BeeMemoryBank", master_dek)
```

**Purpose:** Allows verifying Master DEK compatibility between nodes without decrypting data.
- During join: the sentinel is transferred along with the key slot
- During sync: can verify that the local DEK is compatible with the remote sentinel
- `decrypt(sentinel, local_master_dek) == "BeeMemoryBank"` → DEK matches

Migration 008 adds the `sentinel_value` column to `tbl_node_identity`.

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

Media sync events carry Base64-encoded ciphertext in the JSON payload. A 5 MB image produces ~6.7 MB of Base64 data. This is acceptable for a personal knowledge base but not designed for photo albums.

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
| `Ed25519Signer.cs` | ~40 | Generate keypair, Sign, Verify (NSec) |
| `AgentKeyHelper.cs` | ~50 | API key generation (`bee_` + hex), hash, DEK encrypt/decrypt |
| `SecureRandom.cs` | ~15 | CSPRNG wrapper |
| `CryptoConstants.cs` | ~20 | Key sizes, default Argon2id parameters |
