# Step B (2.6.0): implementation specification - the database is closed until unlock

Status: design of Step B of 2.6.0, in three parts that can be built side by side (section 17). The decision and its reasons
are [ADR 0010](../adr/0010-database-closed-until-unlock.md). Step A ([ADR 0009](../adr/0009-metadata-confidentiality.md),
[step-a-spec.md](step-a-spec.md)) and Step B ship together in **one release 2.6.0**; the
engine swap (SQLite3 Multiple Ciphers, databases plain) ships before, in 2.5.2.

Base of every `file:line` below: the tree of v2.5.1. `[E]` marks a line of the engine swap of release 2.5.2, `[A1]` a line of
Step A as it stands in 2.6.0. Where this document and the code disagree, the code describes
today and this document the target; a contradiction this document did not foresee is resolved by taking the reading that
fails closed.

**Amended in 2.6.2 (BMB-306):** the way-back snapshot, its key envelope, the `pre-encryption/` folder, the `snapshot_limit` blocker and the
rollback (and everything that restores a way-back) no longer exist: encrypting a node keeps no copy of it, a
node that is set up is encrypted from its first day, and no restore produces an open node. See the amendment at the end of
[ADR 0010](../adr/0010-database-closed-until-unlock.md) and [sealed-database.md](../sealed-database.md#going-back); read the sections above as
history where they disagree.

Words: **MUST** is a contract other parts rely on; **SHOULD** is the recommended way, a part may deviate for a reason it states.
"Part 1/2/3" are the parts of section 17.

## Contents

1. Terms
2. What Step B guarantees; the store layout
3. The database key
4. Doors: the unlock material readable before unlock
5. Connection lifecycle
6. The relay inbox of a closed node
7. The keyed connection factory and every place that opens a database file
8. No plaintext image of the vault on disk: snapshots, baselines, join, restore, packages
9. `chat.db`
10. Encrypt this node: the owner-started migration
11. Auto-unlock: desktops and the server checkbox
12. Phones: key custody
13. Other paths: reset, offline re-key, profiles, legacy import, operators, downgrade
14. Compatibility and the points where Step B changes Step A
15. Red tests
16. Interfaces between the parts
17. File ownership and composition rules
18. Golden files, docs, migrations
19. Open points
- Appendix A: contract files (verbatim)
- Appendix B: boot store schema and migration 040 (verbatim)
- Appendix C: the SQLCipher 4 page format as SQLite3MC writes it (verified)

## 1. Terms

| Term | Meaning |
|---|---|
| plain node | a full node whose `beememorybank.db` is a plain SQLite file: every full node until its owner seals it, every new vault in 2.6.0. Behaves exactly as 2.6.0 with Step A alone, except that a server may carry the owner-started server checkbox (11.2, 14.1). Blind hosts are never sealed and keep their plain operational database (8.6). |
| sealed node | a full node after "Encrypt this node" (section 10): `beememorybank.db` and `chat.db` page-encrypted, plus a boot store. |
| boot store | `beememorybank.boot.db`, a small plain SQLite file next to the database: doors, database-key wrappers, the public identity, the whitelist mirror, the relay inbox (2.3, Appendix B). The only file a sealed node reads before unlock. |
| door | a copy, in the boot store, of one thing that yields the master DEK: a key slot (password, recovery, legacy password, OS auto-unlock, server auto-unlock) or an auto-unlock agent's wrapped DEK (4.1). |
| DBK | database key: 32 random bytes per node, never mesh-wide; on disk only wrapped under a master DEK (3). |
| page key | the raw 256-bit SQLCipher key of one file, derived from the DBK (3.2). |
| wrapper | the DBK encrypted under one master DEK, a row of `tbl_boot_db_key` (3.3). |
| open / closed | the sealed database's state: open while the DBK is in memory (the vault is unlocked, or a temporary open runs), closed otherwise (5.1). |
| inbox | events, blobs and position reports a closed sealed node receives; replayed at open (6). |
| relay sequence | the sequence number the inbox gives an event; it is this node's log position for it, above every sealed row (6.3). |
| plain image | the bytes of a plain SQLite file, held in memory or streamed, never written to the disk of a sealed node (8). |
| staging copy | a temporary database file a flow works on (snapshot, baseline, restore); on a sealed node always encrypted with the vault page key (7.4). |
| pre-encryption snapshot | the encrypted snapshot (BMBDB2 under the master DEK, the product's existing snapshot, plus the key envelope below) the migration takes before it changes anything; the node's "pre-cutover backup" and the only way back (10.7). |
| key envelope | `key-envelope.json` inside the pre-encryption snapshot: copies of the password and recovery key slots of that day, so the snapshot opens with the master password or recovery key of that day whatever happened to the DEK afterwards (10.7). |
| plain copy | any file in the data directory that is, by its content and whatever its name or folder, a plain SQLite image of the vault or of the chat history, a WAL or journal of one (pages in clear), or a gzip, tar, tar.gz or zip that holds one as any member, whatever the member is called (the live plain database before the migration, an old cold backup, a staging leftover, a copy somebody made). None may survive the switch (10.2, 10.7). |
| plain companion | a file beside a sealed pair that holds pages in clear: a plain `chat.db`, or a WAL or rollback journal of either database whose page images are plain SQLite pages (2.5). A node whose pair has one is Broken: it does not start (2.5, A.2). |
| enable record | `<data>/server-auto-unlock.enabled`: the server's local note that the owner enabled exactly one server auto-unlock slot here; never in a backup, a snapshot or a package (11.2). |
| quiesced set | the sealed database, the chat database and the boot store copied while nothing can write any of the three: the one file-level backup of a sealed node the product makes (8.5) and the one an operator may make (8.7). |

## 2. What Step B guarantees; the store layout

### 2.1 Target and residual

A stolen disk of a sealed node must be useless. After "Encrypt this node":

- `beememorybank.db`, its `-wal` and `-shm`, `chat.db` and its sidecars, every staging copy and every snapshot hold no
  metadata readable without a door; no SQLite schema text, no title, path, tag, user name, agent name, audit line,
  activity row, remote account or chat title is in any file a sealed node writes (red tests 15.1);
- the boot store holds only allow-listed columns (Appendix B, golden 18): wrapped keys exactly as exposed as today's key
  slots (Argon2id KEKs, ADR 0003), agent key hashes and salts, database-key wrappers, the node id, display name and public
  key, the whitelist (peer display names, addresses, TLS pins and flags: the residual ADR 0009 already accepts for blind
  operators), sealed and control events received while closed (opaque by Step A), ciphertext blobs received while closed,
  position reports;
- a locked sealed node keeps no open key: the master DEK, the DBK and the page keys exist only in memory while open;
- **no plaintext copy of the database survives the switch, anywhere in the data directory** (10.7): the migration wipes
  and deletes the plain files it replaced, refuses to start while a plain copy of the vault lies in the data directory
  (10.2 `plain_copies`), and takes its way back as an encrypted snapshot (the pre-encryption snapshot), not as a plain
  file. The preflight scan reads every file of the data directory by its content, skips no folder, and refuses to start while it
  finds a plain image (10.2); a canary scan of the whole data directory after the switch finds nothing (15.3.9).

Residual, documented (docs and ADR 0010): file sizes and counts of the above; the media `*.enc` files and search-index
segments (encrypted already, sizes visible); baselines, snapshots and packages (encrypted by Step A or BMBDB2);
free-space remnants of the plain files from before the migration (SSD, wear levelling and copy-on-write file systems keep
old blocks even after the in-place overwrite of 10.6; the docs say so, and recommend full-disk encryption of the volume);
`os-auto-unlock.dat` and the server key file when the owner turned those on (section 11: then a stolen disk opens
everything); process memory, swap, hibernation files and crash dumps of an unlocked node; explicit plaintext exports and
offline backups the owner made or asked for (ZIP export, downloads, a cold backup kept outside the data directory); another
name for a database file outside the data directory (a hard link or a copy the owner made there: the product sees the link
count of its own live files and refuses to proceed while it is above one, 10.2, but it cannot see where the other name is);
the pre-encryption snapshot, which is encrypted but opens with the master password or recovery key of the day it was taken
for as long as the owner keeps it (10.7).

### 2.2 Files of a data directory

| File | Plain node | Sealed node |
|---|---|---|
| `beememorybank.db`, `-wal`, `-shm` | plain SQLite | SQLCipher 4 pages under the vault page key (7.1); the whole schema as today (2.4) |
| `chat.db`, sidecars | plain SQLite (rows sealed under the node chat key) | page-encrypted under the chat page key; row sealing unchanged (9); absent on a node without chat. A `-wal` or `-journal` of a sealed file has a plain header and encrypted page images (Appendix C); any plain page image in it, or a plain `chat.db`, is a plain companion and the node does not start (2.5) |
| `beememorybank.boot.db`, sidecars | absent | plain SQLite, `PRAGMA application_id = 0x424D4253`, Appendix B |
| `seal-journal.json`, `seal-journal.json.unreadable`, `*.seal-new`, `*.seal-old*` | absent | only during a migration or its repair (10.3, 10.6); `*.seal-old*` (the plain files being replaced, with their `-wal`, `-shm`, `-journal`) exist only between the swap and their wipe, which the next start finishes; the journal an interrupted start could not read is kept as `.unreadable` until the repair ends |
| `tmp/` (`SnapshotStaging`) | plain staging copies, as today | encrypted staging copies only (7.4, 8) |
| `snapshots/`, `baselines/`, `media/`, `search-index-segments/` | as today | as today (already encrypted) |
| `pre-encryption/` | absent | the pre-encryption snapshot with its key envelope (10.7); not listed, not pruned |
| `os-auto-unlock.dat`, `server-auto-unlock.key`, `server-auto-unlock.enabled` | when turned on | when turned on (11); never inside a backup set (8.5, 8.7) |
| `updates/pre-update-*/` | plain copy (locked path) or BMBDB2 | a quiesced set of the sealed files and the boot store (8.5); a plain one from before the switch blocks the migration until it is erased or moved out (10.2) |

### 2.3 The boot store

- Schema: Appendix B (`BootMigrations/B001_boot_store.sql`, an embedded resource of `BeeMemoryBank.Storage` under
  `BeeMemoryBank.Storage.BootMigrations.`). Its own ledger `tbl_boot_migration (version, filename, applied_at)` and its own
  runner (`BootMigrationRunner`); the ghost rule of `MigrationRunner` (`libs/BeeMemoryBank.Storage/Sqlite/MigrationRunner.cs:37-65`)
  never sees this ledger, and the vault's runner never sees this file. Golden `docs/boot-migrations.golden.txt`.
- Its own connection factory (`BootStoreConnectionFactory`): plain, WAL, `foreign_keys=ON`, `busy_timeout=5000`,
  `temp_store=FILE`; never registered as `IDbConnectionFactory` (a repository of the vault must never land on it by mistake).
- Allow-list golden `docs/design/boot-store.golden.txt`: every table and column (name and declared type), sorted. A test
  compares the live schema with it; a new column fails until the file is changed on purpose (which forces the question "is
  this metadata?"). A second test asserts that the boot `tbl_whitelist` has every column the vault's `tbl_whitelist` has
  (so a later vault migration that adds a whitelist column fails until the boot store gets one).
- Size: doors and wrappers are a few rows; the inbox is bounded (6.2); `VACUUM` of the boot store runs after an import
  that removed more than half of its pages.
- Never in it: a user row, a user name, a password hash, an agent name or description, an ACL, an audit row, a title, a
  path, a tag, a decrypted payload, a protocol-1..3 content event, an open key.

### 2.4 The sealed database

The sealed `beememorybank.db` holds **every table it holds today**, Step A's columns and tables included; key slots, users
and agents stay authoritative there. Nothing moves between files, because the product's transactions are single-file:
an authored change writes the content row, its blob and its event in one transaction (`EventLogger.AppendEventAsync`
with the caller's transaction, `libs/BeeMemoryBank.Vault/Sync/EventLogger.cs:371-447`, blob in the same transaction
`:27-34`), a DEK rotation rewraps every key-bearing row in one transaction (ADR 0003,
`libs/BeeMemoryBank.Vault/Sync/DekRotation/DekRewrapper.cs:65-268`), and SQLite does not commit a transaction across two
WAL files atomically. Moving the log, the blobs or the key slots to the plain file would break those guarantees; the boot
store therefore holds **copies** that can be rebuilt from the sealed database (4.3) and the **inbox** of the closed period
(6), which is emptied into the sealed log at open.

### 2.5 Which mode a data directory is in

`VaultStoreFiles.Diagnose(dataPath)` (Appendix A.2; `DetectMode` is its `Mode`) after the seal journal is resolved (10.6) and
before anything opens the database. It answers a mode and, for Broken, a stable reason code and the relative names the reason
concerns, so that every refusal is named:

- no boot store, plain or no database: **Plain** (today's start, unchanged);
- a boot store with our application id next to an encrypted database whose length is whole pages **and no plain companion**
  (below): **Sealed**;
- anything else: **Broken**, with the reason `db_without_boot` (an encrypted database without its boot store), `boot_beside_plain_db`
  (a boot store beside a plain database), `boot_not_ours`, `db_missing_or_empty`, `db_not_page_aligned`, `seal_leftovers_no_journal`
  (a `*.seal-old*` file beside the live files while no `seal-journal.json` exists), `plain_companion` or `companion_unreadable`.
  The node refuses to start with a message naming the files and the ways back (restore the boot store from the same backup as the
  database; restore the vault from a snapshot, a peer or a blind copy; for a plain companion, move the file out of the data
  folder). It never opens an encrypted file as plain and never creates an empty vault beside it, and it deletes, moves and changes
  nothing. The `*.seal-old*` case is the backstop for a start that did not run the resolver of 10.6, which always decides what a
  leftover is (wipe, or refuse with a named recovery) before this check.

**Plain companions (MUST, every start).** The files that can sit beside a sealed pair and still hold pages in clear are `chat.db`
and the `-wal` and `-journal` files of `beememorybank.db` and of `chat.db` (a `-shm` holds only the WAL index, no page image).
Before `Sealed` is returned each of them, when present and not empty, must be a valid sealed file or hold no plain page:

- `chat.db` must not start with the SQLite magic. Absent is fine (a node without a chat database, a CLI-only deployment), empty is
  fine; a chat database that is a plain SQLite file, a copy cut from a plain backup or left by a half-done restore, is a plain
  companion.
- A WAL or a rollback journal is read frame by frame (record by record) and must contain no **plain page image**. A sealed file's
  own WAL and journal keep a plain header (the 32-byte WAL header, the 24-byte frame headers, the journal header: page numbers,
  salts, checksums) and hold the page images encrypted (checked, Appendix C), so the magic of a WAL or a journal proves nothing and a
  sealed node's own WAL has it; only the page image counts. The **plain page test** (identical in `VaultStoreFiles` and in the AppPaths
  copy `VaultStoreProbe`, kept equal by a test): the frames are read from the first one in file order, at most the first 1024 and the
  last 1024 of a longer file (a record of a journal is `4 + page size + 4` bytes from the sector offset in its header), and a page
  image is plain when it begins with `SQLite format 3\0` (page 1) or has a **valid b-tree page header**: the type byte (2, 5, 10 or
  13) at offset 0, at offset 100 on page 1; the fragmented-bytes field at most 60; the cell pointer array ending at or before the
  cell content start; the cell content start within the page; the first free block 0 or inside the page; every cell pointer between
  the end of the pointer array and the page size minus 4. Every page that belongs to a table or index b-tree passes with certainty; a
  ciphertext page passes with a probability of about 2^-32 (a test feeds four million random pages and a real sealed WAL and journal,
  none is refused). A WAL or journal that holds no b-tree page at all (only overflow or free-list pages) is not detected, which is the
  documented limit (19.17). A file whose header is neither the SQLite magic, nor a WAL header (magic `0x377F0682` or `0x377F0683`,
  version 3007000, a power-of-two page size from 512 to 65536) nor the journal magic is not examined: SQLite ignores it.
- A companion that exists but cannot be read (a lock other than sharing, a permission error) is `companion_unreadable`: fail closed,
  the message names it, the owner closes the program that holds it and starts again.

The check runs at **every start**, before the key source or the relay or any hosted service exists, and is the same code for
`VaultStoreProbe.IsSealedPair` (7.6), so the resolver of 10.6 cannot call a pair sealed that the start would refuse. It reads at most
five files, and of a WAL or journal at most 2,048 frames (8 MiB at 4096 bytes); it is deliberately not the full-directory scan of 10.2 (a data directory can hold 50,000 media files and large
archives, and a start must stay cheap): that scan stays the preflight, the final scan of the migration and the erase route. The
named refusal for a plain companion reads: "This node's database is encrypted, but <names> hold unencrypted data of this node, and a
stolen disk would show it. Move the file out of the data folder (keep it elsewhere if you need its content), or restore the data
directory from one backup set (8.7.2), then start the node again. Nothing was opened, changed or deleted." The refusal is reported
by `bmb status`, `bmb vault verify` and (as `broken_reason` and the relative names, no content) by the Web start page of a node that
cannot start.

### 2.6 The relay store of Step A on a sealed node

Step A keeps received-but-not-applied events in `tbl_event` with `apply_state` (step-a-spec 6.1) and lets a locked node
serve them (6.9). On a sealed node `tbl_event` is in the sealed file, so while the database is closed received events go
to the boot store's inbox (section 6), are served from there, and are replayed through `EventApplier.ApplyAsync` when the
database opens; from then on Step A's held rows and drain work as Step A specifies. A plain locked node behaves exactly
as Step A says.

## 3. The database key

### 3.1 Generation

- 32 bytes from the CSPRNG (`IDatabaseKeyService.Create`, Appendix A.4), per node; never sent anywhere, never in a
  package, a snapshot or an event.
- `dbk_generation` (boot meta): 1 at the migration. A new DBK and generation + 1 only when the database file is rebuilt
  under a new key: the offline re-key (13.2). A restore keeps the DBK (8.4); a node reset makes the node plain (13.1).
- The DBK is in memory only while the database is open (the gate); cleared on close.

### 3.2 Page keys

`page_key(purpose) = HKDF-SHA256(ikm = DBK, salt = empty, info = UTF8("bmb-db-page-v1:" + purpose), L = 32)`, purpose
`vault` or `chat` (`DbPurposes`, A.2). The page key is the SQLCipher raw key (`x'<64 hex>'`, never a passphrase: a
passphrase costs 29 to 366 ms per connection). Two files, two keys:
a page from one file never decrypts in the other.

### 3.3 Wrapping under the master-key chain

`tbl_boot_db_key` (Appendix B) holds one row per (master DEK fingerprint, DBK generation). Exact format (`DatabaseKeyCrypto`,
`libs/BeeMemoryBank.Vault/Sealed/DatabaseKeyCrypto.cs`; Vault: a blind program never carries it, ADR-0002 of the blind node):

| Item | Value |
|---|---|
| DBK | exactly 32 bytes (`CryptoConstants.KeySize`) |
| master DEK | exactly 32 bytes; any other length is refused before any crypto (`ArgumentException`, no key in the message) |
| `dek_fp` | `DekFingerprint.Of(master DEK)` (`libs/BeeMemoryBank.Crypto/DekFingerprint.cs:12-31`): 64 characters, lowercase hex (`^[0-9a-f]{64}$`) |
| `iv` column | 12 random bytes from `RandomNumberGenerator`, drawn fresh by every `Wrap` call (`CryptoConstants.IvSize`); a row is never re-encrypted under the same iv |
| `wrapped` column | `ciphertext (32 bytes) \|\| GCM tag (16 bytes)` = **48 bytes** (`CryptoConstants.TagSize`) |
| cipher | AES-256-GCM, key = the master DEK, nonce = `iv`, plaintext = the DBK |
| AAD | UTF-8 bytes of `"bmb-dbk-v1:" + node_id + ":" + generation + ":" + dek_fp`, where `node_id` = this node's `Guid.ToString("D")` (36 characters, lowercase, hyphenated; boot meta `node_id`), `generation` = `dbk_generation.ToString(CultureInfo.InvariantCulture)` (a positive decimal integer, no sign, no padding) and `dek_fp` as above |

`TryUnwrap(row, masterDek, nodeId)` (the `IDatabaseKeyService.TryUnwrapAsync(masterDek)` of A.4 reads `node_id` and the
generation from the boot meta keys `node_id` and `dbk_generation`; it considers the one row `(DekFingerprint.Of(masterDek),
dbk_generation)` and no other) behaves exactly like this:

1. Row validation before any crypto: `row.DekFingerprint` matches the pattern, `row.Iv.Length == 12`, `row.Wrapped.Length == 48`,
   `row.DbkGeneration >= 1`. Any violation: return `null` ("no wrapper"), log one line naming the generation only (never the
   fingerprint, the lengths or any byte), and go on as if the row were absent. A malformed row is never an exception.
2. `row.DekFingerprint` must equal `DekFingerprint.Of(masterDek)` (ordinal compare of the two 64-character strings).
3. AES-GCM decryption with the AAD above. An authentication failure (`CryptographicException`, a tampered `wrapped`, `iv`,
   `dek_fp`, a wrong node id, a wrong generation, a foreign boot store, a wrong DEK) is caught inside `TryUnwrap` and returned
   as `null`: no exception, no inner exception, no message other than the debug line "dbk wrapper did not authenticate
   (generation N)" leaves the method. The caller maps `null` to the refusal `no_wrapper` (4.4 step 3) and tries the next door.
4. The decrypted buffer must be exactly 32 bytes; otherwise it is cleared and `null` is returned.

The `node_id` of the AAD is the boot meta `node_id`, written once when the boot store is created (the migration, the offline
re-key) and never changed afterwards: it binds a wrapper to its boot store, it is not the live identity (that is
`tbl_boot_identity`, which a standalone restore or a join may replace without invalidating a single wrapper).

Buffers (MUST): every temporary that holds a DBK, a master DEK copy, a page key or a candidate plaintext is a `byte[]` cleared
with `CryptographicOperations.ZeroMemory` in a `finally` on every path (success, authentication failure, exception). The DBK that
`TryUnwrap` returns and the page keys that `DerivePageKey` returns belong to the caller, who clears them in its own `finally`; the
gate keeps the DBK only as long as the database is open (5.4). A key-bearing .NET `string` (the `Password` of a connection
string, the `VACUUM INTO` target) cannot be cleared, so its **lifetime is defined instead, as one contract (7.1, 7.2)**: it is
built by `DbConnectionFactory` / `SqlCipherScheme` once per open generation and handed to nothing but Microsoft.Data.Sqlite; the
provider keys its connection pool by the connection string, so for the length of **one open generation** the key-bearing string also
lives inside the provider's pool and inside the live connections that pool holds, and nowhere else; before the gate publishes
the state Closed every connection of the generation is disposed, the pool is flushed (`SqliteConnection.ClearPool`) and the
factory's reference is dropped. It is never copied to a log, a status answer, an exception, a cache key of ours, `DatabaseId` or
any field that outlives the generation (15.1.3 tests every boundary and the open-generation case, 15.3.5 the closed case). A
locked full node therefore holds no DBK, no page key, no master DEK, no open metadata key (Step A's key ring zeroes its keys at
lock, `MetadataKeyRing.Clear` on `SessionService.Locked`) and no key-bearing cache entry. `DatabaseKeyCrypto` exposes an internal test seam
`Action<byte[]>? Cleared` that runs on each cleared buffer after zeroing, so 15.2.7 can assert that every temporary was zeroed.

The chain on disk is therefore door (password KEK, recovery KEK, OS or server secret, agent key) -> master DEK -> wrapper
-> DBK -> page keys. There is no other copy: no wrapper inside the sealed database (it could not help open it), no DBK in
the OS key store, no DBK on a phone's Keychain or Keystore (12).

### 3.4 Which wrappers exist

A door yields some master DEK, not necessarily the current one: a peer that auto-accepts a DEK rotation keeps its users'
slots on the old DEK and rewraps each lazily at that user's next login (`LazySlotRewrapService`, migration
`020_dek_rotation_chain_material.sql:1-20`). So the boot store keeps a wrapper for every DEK some door may yield:

- every door row records `dek_fp` once known: the writer that knows the DEK passes it (`PendingDoor.DekFingerprint`); an
  unlock records the fingerprint of the DEK the door it used yielded (`IBootDoorStore.SetDekFingerprintAsync`); a rewrite
  keeps the known fingerprint of a door whose `content_sha256` did not change;
- **wrappers kept** = the current DEK, plus the DEK of every door with a known `dek_fp`, plus, if any door's `dek_fp` is
  unknown, every DEK the session holds (`SessionService.GetCandidateDeks`, `libs/BeeMemoryBank.Vault/Core/Services/SessionService.cs:531-555`:
  current, in-memory retired, persisted retired); a wrapper a door needs whose DEK the session does not hold is never
  deleted;
- every other wrapper is deleted by the rewrite (4.3). On the rotation's initiator, which deletes every other slot and
  every auto-unlock agent (`DekRewrapper.cs:147-165`), this leaves one wrapper, under the new DEK: a node the rotation left
  out, which holds the old DEK, cannot open this node's database from a stolen disk. On an auto-accepting peer the old
  wrapper stays until every user slot under it has been rewrapped or deleted; that equals today's exposure (the chain
  material `tbl_dek_rotation_state.chain_encrypted_new_dek` already lets an old-DEK holder reach the new DEK on that
  node's disk, migration 020) and is documented.

### 3.5 DEK rotation

The DBK does not change and no page is rewritten (never `PRAGMA rekey` for a password or DEK change). On a sealed node,
`DekRewrapper.RewrapAllAsync` (`DekRewrapper.cs:65`, called by the initiator `server/BeeMemoryBank.Api/Services/DekRotationService.Rewrap.cs:40`
and by the auto-accepting peer `libs/BeeMemoryBank.Vault/Sync/DekRotation/PeerDekRotationApplier.cs:166`) does, through
`IUnlockMaterialMirror`:

1. before its transaction commits: `AddDbKeyWrapperAsync(newDek)` (boot write, its own transaction) and, on the initiator,
   `AddPendingDoorAsync` for the initiator's slot rewrapped under the new DEK (`DekRewrapper.cs:152-157`);
2. the existing transaction commits unchanged (agents with a wrapped DEK deleted, slots rewritten or deleted, sentinel and
   epoch bumped);
3. after the commit: `RewriteAsync()`; on failure of step 2, `RewriteAsync()` too (it drops the pending rows). On the initiator and on
   an auto-accepting peer alike the commit has deleted the `server_auto_unlock` slot (11.2 item 6), so the same hook then removes the
   enable record and a node-created key file (`ServerAutoUnlockService.DisableLocalAsync`; a failure is logged and the stale
   rule of 11.2 removes what is left at the next start).

Kill points (red test 15.2.6): a crash before 1 or between 1 and 2 leaves the old database and an extra unused wrapper
(the next rewrite deletes it); a crash between 2 and 3 leaves old door copies yielding the old DEK, whose wrapper still
exists: the password opens the database, the sentinel does not match, the lazy rewrap reaches the new DEK through the chain
material (`SessionService.cs:260-275`), the unlock rewrites the boot store. Every crash point leaves the node unlockable
with the password it had.

**The metadata key rotates with every DEK rotation - a precondition of Step B, not an option.** The MEK
(step-a-spec 4) lives in `tbl_metadata_key`, inside the sealed database; Step B does not touch it, but Step B's promise to a
node the rotation left out ("a stolen disk of this node is useless to you" above; "metadata written afterwards is unreadable to
you" on the wire) is the sum of two things: the DBK wrapper pruning of 3.4 (the disk) and the new MEK generation under the new
DEK (the stream). The second must hold even when publishing the key fails after the DEK swap, so Step A's rotation (14.4 item 7)
creates the new generation inside the same transaction as the DEK swap, publishes its `metadata_key_set` event after the commit
with a durable retry, does not report the rotation complete until the event is in the log, and lets this node author no sealed
metadata between the swap and the event. Step B does not edit that code (it is Step A's, 17.2); the test that proves it is
15.4.4. An auto-accepting peer creates neither a key row nor the pending marker (the initiator alone publishes); the Step A
tests prove that too, and the Step B edits of `DekRewrapper.cs` build on that change.

### 3.6 Password change, new slot, recovery key, auto-unlock, agents

Only doors change (4.3): a password change or a new user slot (`UserService.cs:179, 228, 377, 477`;
`KeyManagementService.cs:84-93, 234`), a recovery key created or replaced, OS or server auto-unlock turned on or off
(`OsAutoUnlockService.cs:112-170, 246-260`; section 11), an agent created or revoked (`AgentEndpoints.cs`, `AgentCommand.cs`).
The DBK and its wrappers stay.

### 3.7 Recovery

- **Lost password:** unchanged. The recovery key is a door (`recovery` slot); "Forgot your password" (`/api/session/recover-access`,
  `server/BeeMemoryBank.Api/Endpoints/SessionEndpoints.cs:140-200`) opens the database temporarily with the DEK the recovery
  door yields (`IVaultGate.OpenTemporarilyAsync`, 5.7), writes the new slot, rewrites the boot store and closes again; the
  session stays as it was, as today.
- **Lost boot store, database intact:** the database cannot be opened (2.5 Broken). Restore from a snapshot (BMBDB2, under
  the master DEK, independent of the DBK), rejoin from a peer, or restore from a blind copy (step-a-spec 12). The docs say:
  back up the boot store with the database (every product backup path does, 8.5, 18).
- **Lost everything but a recovery box on a blind node:** Step A's restore (step-a-spec 12) builds a new node; it may be
  sealed afterwards. No bypass exists: without a door, a peer or a recovery box the data is gone, as the bodies are today.

## 4. Doors: the unlock material readable before unlock

### 4.1 Which doors

| Source in the sealed database | Copied as | Not copied |
|---|---|---|
| every `tbl_key_slot` row (`001_initial_schema.sql:33-43`): `user`, `recovery`, legacy `password`, `os_auto_unlock`, `server_auto_unlock` | `kind = 'slot'`, `source_id = slot_id`, slot type, wrapped DEK, iv, salt, Argon2 parameters | which user owns it |
| every `tbl_agent` row with `encrypted_dek IS NOT NULL AND status = 'A'` (migration 014, `AgentAuthMiddleware.cs:183-199`) | `kind = 'agent'`, `source_id = id`, `key_hash`, `salt`, `kdf_version`, wrapped DEK, iv | name, description, prefix, owner, counters |

Not doors: user rows and password hashes (user names stay encrypted; the unlock is password-only, 4.5), the
desktop update hand-off file (`UpdateUnlockHandoff`, a one-shot DPAPI/Keychain blob outside the database, consumed at start:
it yields a DEK and goes through 4.7), the iPhone quick unlock (Keychain, 12.2), the Android biometric (it stores the
password, 12.1).

### 4.2 Copy format

`content_sha256` = lowercase hex SHA-256 of `LP(kind) LP(source_id as decimal) LP(slot_type or "") LP(key_hash or "")
LP(kdf_version or "") LP(argon_memory or "") LP(argon_iterations or "") LP(argon_parallelism or "") LB(salt or empty)
LB(encrypted_master_dek) LB(iv)`, where `LP(s)` = int32 little-endian byte length of UTF-8(s) then the bytes and `LB(b)` the
same for raw bytes. Unique index `(kind, source_id, content_sha256)`: several rows per source are allowed (a pending
replacement next to the current one).

### 4.3 Write protocol

The boot copy is derived. Three rules make every crash point safe for availability and keep removed doors from lingering:

1. **Add or replace a door:** before the sealed transaction commits, `AddPendingDoorAsync(door)` (row with `pending = 1`).
2. **Remove a door** (agent revoked, user deleted or demoted, recovery slot deleted, auto-unlock turned off): before the
   sealed transaction commits, `RetireDoorAsync(kind, source_id)` deletes its copies.
3. **Rewrite:** after the commit (and in a `finally` when it failed), `RewriteAsync()` replaces every door row with the
   sealed database's current set, keeps known `dek_fp` values, rebuilds the wrapper set of 3.4, refreshes the identity and
   whitelist mirrors, and stores the sealed `unlock_material_version` it saw (all in one boot transaction).

Backstop: migration 040 (Appendix B) adds triggers that bump `tbl_migration_marker` key `unlock_material_version` inside the
writing transaction on every change of `tbl_key_slot`, of the unlock columns of `tbl_agent`, of `tbl_user.role`,
`is_active`, `key_slot_id`, of the authentication columns of `tbl_whitelist` and of the public identity. While open, a
watcher (every 10 s, and at close) rewrites when the version differs from `boot_meta.unlock_material_version`. A
writer this specification did not foresee (raw SQL in a restore, a future feature) is therefore mirrored within seconds,
and the red test 15.2.3 lists the known writers.

Every unlock that succeeds ends with `RewriteAsync()`. So does every close (before the key is cleared).

| Crash point | Boot store holds | Effect |
|---|---|---|
| addition: after rule 1, before commit | old doors + pending new one | old ones work; the pending one opens the file but the door check (4.4 step 6) finds no such row and refuses; next rewrite drops it |
| addition: after commit, before rewrite | old doors + pending new one | both work; next rewrite makes the new one current |
| replacement (password change): same two points | old copy + new copy | the old password opens once (the slot id exists), the rewrite then drops it; never a lock-out |
| removal: after rule 2, before commit | without the door | if the commit did not happen the door is missing until the next rewrite (availability only; other doors work) |
| removal: after commit | without the door | consistent |

Residual (documented): a removed door that a crash left in the boot store between the commit and the rewrite (rule 2 runs
first, so only a removal whose rule 2 itself failed) opens the database offline until the next unlock.

### 4.4 Unlock on a sealed node

`SessionService.UnlockAsync(password)` (`SessionService.cs:98-118`), `VerifyMasterPasswordAsync` (`:121-136`) and
`TryOpenWithRecoveryKeyAsync` (`:151-162`) share `TryDeriveAuthorizedMasterDekAsync` (`:194-355`). On a sealed node:

1. **Door source:** closed: `IBootDoorStore.ListAsync()` (slots with a salt and Argon2 parameters, recovery only when asked,
   same KDF bounds `:203-213`); open: `tbl_key_slot` as today.
2. Argon2id KEK, unwrap the DEK (`:216-247`), unchanged.
3. Closed only: `IVaultGate.OpenAsync(dek)`: `dek_fp` -> wrapper of the current generation (3.3) -> DBK; no wrapper or a
   failed tag -> refuse this door (continue with the next, exactly like a wrong password).
4. The gate derives the page keys, opens the database, runs the open steps (5.3). A key that does not open the file
   (`VaultKeyMismatchException`, SQLite error 26) closes everything and fails the unlock loudly (log: "the boot store does
   not belong to this database"; no key in the line).
5. The sentinel check and lazy rewrap run as today on the open database (`:249-283`). **Pair diagnosis (never a refusal by
   itself):** the gate reads `unlock_material_version` from the open database and from the boot meta; if the database's value
   is lower than the boot store's (a database restored without its boot store of the same moment, or a lost last write
   after a power cut) and the unlock then fails at step 5 or 6, the logged and `last_refusal` code is `store_stale` instead of
   the generic one ("the database is older than its boot store: restore the data directory from one backup set, 8.7"); the
   caller still sees the same `false`. A lower database value alone never blocks an unlock: the rewrite at the end of the
   unlock repairs the boot store from the database (4.3), which is what a lost last write needs.
6. **Door check:** the door's `source_id` names a current `tbl_key_slot` row (any content: a replaced slot is accepted once,
   4.3) and the slot type may unlock (the superadmin rule for `user` slots, `:283-320`, now reading `tbl_user` inside the
   open database: this is "password-only unlock first, username afterwards").
7. Any refusal after step 3: `IVaultGate.CloseAsync("refused")`, wipe, continue with the next door; the caller sees the same
   `false` as a wrong password (no oracle, `:321-330`).
8. Success: the session installs the DEK (`:170-176`), `IBootDoorStore.SetDekFingerprintAsync` for the door, the post-unlock
   chain (`TriggerPostUnlockCatchUp`, `:399`) gains first step `IVaultGate.CompleteOpenAsync()` (import, 6.6; state Open;
   `RewriteAsync`), then today's tasks and Step A's drain (step-a-spec 6.7).

Rate limits and the semaphore (`:36-45`) are unchanged; one unlock at a time.

### 4.5 Login on a sealed node

`POST /api/session/login` (`SessionEndpoints.cs:44-125`) reads `tbl_user` first today. While the database is closed:

1. `session.UnlockAsync(req.Password)` (password only; 4.4). Failure: 401 `{ "error": "Invalid password, or this server is
   locked: only an administrator can open it.", "code": "session_locked_or_invalid" }` - the same answer for a wrong
   password, a non-superadmin's password and an unknown user, so nothing is learned before unlock.
2. Success: the existing flow runs on the open database (`AuthenticateAsync(username, password)`); a user name that does not
   match: `session.Lock()` (which closes the database again) and the same 401.

While open, login is unchanged. The Web login page shows "This node's database is encrypted and closed. Sign in
with an administrator's password to open it." when `GET /api/session/status` says `sealed: true, unlocked: false`.

### 4.6 Agents

`AgentAuthMiddleware` (`server/BeeMemoryBank.Api/Middleware/AgentAuthMiddleware.cs:43-199`) reads `tbl_agent` and `tbl_user`
first. While closed, for a `bee_` key: `IBootDoorStore.FindAgentAsync(AgentKeyHelper.ComputeKeyHash(key))` -> the
DEK (`DecryptDekV1` or legacy `DecryptDek`, `:186-196`) -> `TryUnlockWithDekAsync(dek, door)` (4.7); the door check is "an
agent row with this key hash, status `A`, `encrypted_dek` not null, owner active superadmin". Then the existing code runs on
the open database. A `bmbrt_` remote token never opens anything (it has no DEK): 503 `vault_sealed`.

### 4.7 Unlock with a DEK

`SessionService.UnlockWithDek(byte[])` (`SessionService.cs:357-366`) verifies nothing today. Step B adds
`Task<bool> TryUnlockWithDekAsync(byte[] masterDek, BootDoor? door)`: closed -> `OpenAsync`, sentinel check, door check, then
as `UnlockWithDek`; false and closed again on any refusal. Callers on a sealed node: the agent path (4.6), OS auto-unlock
(`OsAutoUnlockService.TryAutoUnlockAsync`, `libs/BeeMemoryBank.Infrastructure/OsAutoUnlock/OsAutoUnlockService.cs:189-240`,
which reads the sentinel before `UnlockWithDek` at `:209-216`: that order is reversed on a sealed node), the update hand-off
(`UpdateUnlockHandoff.cs:78-120`, same reversal), the server auto-unlock (11.2) and the iPhone quick unlock
(`mobile/BeeMemoryBank.FullIos/Services/QuickUnlock.cs:87-127`, same reversal). `UnlockWithDek` stays for plain nodes.

## 5. Connection lifecycle

### 5.1 States

`IVaultGate` (Appendix A.1; registered in every full-node host; absent on blind hosts, where everything behaves as
`Plain`):

| State | Database | Requests (5.6) | Hosted services (5.5) |
|---|---|---|---|
| Plain | open from start, as today | as today | as today |
| Closed | no key; every `CreateConnection` of the vault and chat factories throws `VaultSealedException` | allow-list + relay; everything else 503 | wait |
| Opening | being opened, migrated, checked | as Closed | wait |
| Importing | open; inbox replay running | content routes work; the relay set still answers from the boot store | run |
| Open | open | as on a plain node | run |
| Closing | quiescing | as Closed | stop their loop body |

### 5.2 Start of a sealed node (pre-open, no key)

In this order, before Kestrel serves (`server/BeeMemoryBank.Api/Startup/ApiStartupTasks.cs` and the hosts of 5.8, 5.9):

1. `VaultStartup.Enter` resolves an interrupted migration (10.6: a valid journal, a missing or unreadable one, an orphaned
   `*.seal-old*` file) and an interrupted re-key, holds the lease (`libs/BeeMemoryBank.AppPaths/VaultStartup.cs:52-93`). A
   leftover it cannot prove safe ends the start with a named refusal (10.6), before `DetectMode`.
2. `VaultStoreFiles.Diagnose` (2.5; `DetectMode` is its mode): Plain -> today's start (`ApiStartupTasks.cs:66-327`, unchanged); Broken ->
   refuse, naming the reason and the files, before any connection, key source, relay or hosted service exists and with nothing opened,
   created, moved or deleted - this includes a plain `chat.db` or a plain WAL or journal beside the sealed pair (2.5, checked at every
   start); Sealed -> go on.
3. `IBootStore.EnsureCreatedAsync()` (boot migrations).
4. `ISyncProtocolPolicy.MarkActivated()` when `boot_meta.activated = 1` (always on a sealed node): Step A's policy reads
   `tbl_metadata_activation` lazily (`[A1] libs/BeeMemoryBank.Sync/Metadata/SyncProtocolPolicy.cs`), which would throw
   `VaultSealedException` while closed; marking it first keeps it from ever reading.
5. Sweep `tmp/` (`SnapshotStaging.Sweep`, today `ApiStartupTasks.cs:215`): staging copies are encrypted, sweeping them is
   still right.
6. Update hand-off, then OS auto-unlock (desktop role) or server auto-unlock (server role, which first applies the stale-slot rule
   of 11.2), each through `TryUnlockWithDekAsync` (today `ApiStartupTasks.cs:228-263`, after the migrations; on a sealed node before
   them: the migrations are an open step). On a plain node the server step runs at the same place as today's OS step, with the
   plain database already open and no boot store involved.
7. Register the shutdown hook (`:320-327`; on a sealed node it also closes the gate).

Nothing else runs before the database opens.

### 5.3 Opening

`IVaultGate.OpenAsync(dek)`, under one semaphore:

1. State Opening. `IDatabaseKeyService.TryUnwrapAsync(dek)` -> DBK, or refuse `no_wrapper`.
2. Page keys (3.2); `OpenGeneration++`; the key source is open.
3. Test query `SELECT count(*) FROM sqlite_schema` through the vault factory; SQLite error 26 -> `VaultKeyMismatchException`
   -> close, refuse `key_mismatch`.
4. Open steps (`IVaultOpenStep`, A.1) in `Order`, table 2 below.
5. `ISealedRelayLifecycle.OnOpenedAsync()` (6.3).
6. State Importing; return `Done`. The caller (4.4) finishes the unlock; `CompleteOpenAsync()` replays the inbox (6.6),
   rewrites the boot store, sets state Open.

The open steps run before the caller's sentinel and door checks (4.4 steps 5-6). They are maintenance (migrations, sweeps,
backfills) that is correct for any holder of a master DEK this node's wrappers accept; a refused door closes the database
again afterwards, and no request is served in between (the state is Opening).

Table 2 - today's start tasks on a sealed node (`ApiStartupTasks.cs` line numbers):

| Task | Today | Sealed node |
|---|---|---|
| blind seed cutover (`:66-68`) | blind only | blind only (unchanged) |
| vault migrations (`:71-75`), `StoredEventRepair` (`:76`) | start | open step 100, every open (idempotent) |
| folder bootstrap (`:85-89`) | start | open step 200, every open |
| `ChatDbInitializer` (`:95-100`) | start | open step 300, every open (after the chat factory is keyed) |
| Lamport clock (`:103-107`) | start | open step 400, every open: `Initialize(max(clock, max lamport_ts))` |
| restore and DEK-rotation crash sweeps, standalone-staging cleanup, media staging and orphan sweep (`:115-218`) | start | open step 500, **first open of the process only** |
| OS auto-unlock (`:228-263`) | start | pre-open (5.2 step 6) |
| media blob backfill (`:270-296`) | start, background | open step 600, first open, background |
| concept tag embedding backfill (`:299-317`) | start, background | open step 700, first open, background |

### 5.4 Closing

`SessionService.Locked` (`SessionService.cs:77`, raised by `Lock()` `:580-607`) -> `IVaultGate.CloseAsync("lock")` (the gate
subscribes; `SessionService` need not change for it):

1. State Closing (requests now see Closed).
2. `IUnlockMaterialMirror.RewriteAsync()` and `ISealedRelayLifecycle.OnClosingAsync()` while the database can still be read.
3. Quiesce the vault and chat factories (`DbConnectionFactory.BeginQuiesce`/`WaitDrainedAsync`, `:148-177`) with a 30 s limit;
   when they are drained, run `PRAGMA wal_checkpoint(TRUNCATE)` on the vault and on the chat database (best effort, a busy
   result is logged and ignored; one last keyed connection per file, disposed at once) so that a closed node's files are
   self-contained: no `-wal` content is needed to read them, which is what makes a stopped-node file backup consistent (8.7) and a
   pre-update cold copy complete (8.5). Then **flush the key-bearing pools**: `CloseForSealAsync` (7.2) disposes the connections of
   the generation (the chat keep-alive connection included), calls `SqliteConnection.ClearPool` for the pool of each factory and
   drops the factory's connection-string reference, and returns only after all three are done. After the 30 s limit: log a warning
   and go on - a connection still in use keeps its own connection string and key schedule until its owner disposes it (`ClearPool`
   has taken it out of the pool, so it is closed when it comes back and cannot be reopened); that straggler is the one bounded
   exception to "no key-bearing string after close", and no new connection is handed out meanwhile.
4. Clear the DBK and the page keys; `OpenGeneration` stays; state Closed is published **after** steps 3 and 4 have returned, never
   before (a request that sees Closed finds no key anywhere, 3.3).

Shutdown (`ApplicationStopping`) runs the same, and then runs `PRAGMA wal_checkpoint(TRUNCATE)` on the boot store (plain, no key
needed; also on a node that was never unlocked since its start), so that the three files of a cleanly stopped node are each
self-contained (8.7.1). The Android app locks on every `OnSleep`
(`mobile/BeeMemoryBank.Mobile/App.xaml.cs:99-109`): on a sealed phone every background closes the database; reopening costs
one door (biometric or password) as today.

### 5.5 Hosted services

Each loop awaits `IVaultGate.WaitUsableAsync(ct)` at the top of an iteration (resolved with `GetService`; absent on blind hosts:
no wait) and treats `VaultSealedException` thrown in the middle as "stop this iteration quietly" (debug log).

| Service (registration) | Touches the vault | Sealed and closed |
|---|---|---|
| `SyncScheduler` (`libs/BeeMemoryBank.Sync/NodeDependencyInjection.cs:73`) | positions, log, whitelist | waits (a locked node cannot pull or push anyway: `SessionNodeAuthSigner`) |
| `CleanupService` (`:80`) | log, blobs | waits |
| `PendingIndexProcessor` (`libs/BeeMemoryBank.Vault/Sync/DependencyInjection.cs:105`) | articles | waits |
| `PendingEmbeddingProcessor` (`libs/BeeMemoryBank.Embeddings/DependencyInjection.cs:83`; Sync's copy) | articles | waits |
| `AuditLogPruningHostedService` (`ApiServices.cs:202`) | audit | waits |
| `DownloadCleanupHostedService` (`:201`) | files only | runs |
| `RemoteAccountSyncScheduler` (`:205`) | remote accounts | waits |
| `ChatHistoryBackfillProcessor` (`:211`) | chat.db | waits (it must not open chat.db while closed) |
| `RecoveryReconcileWatcher`, `StateAnchorScheduler` (`Recovery/RecoveryApiServices.cs:23, 31`) | recovery tables | wait |
| `BlindAlarmStarter` (`ApiServices.cs:289`, in `BlindAlarmService.cs`) | sync cycles (memory) | runs; any vault read it does waits (phase 2, 17.2) |
| `MdnsAnnouncer` (`libs/BeeMemoryBank.Infrastructure/DependencyInjection.cs:33`) | node identity | waits (no LAN announcement until unlock) |
| Step A drain, baseline scheduler | sealed tables | started by the post-unlock chain, as Step A says |
| the boot-store watcher (4.3) | boot store, vault | runs only while open |
| blind-only services (`BlindRoleServices.cs:44, 109`, `BlindBackupServiceCollectionExtensions.cs:56`) | blind database | unchanged (blind hosts are never sealed) |

### 5.6 Requests while closed

A new `SealedVaultMiddleware` sits right after `AgentAuthMiddleware` (`server/BeeMemoryBank.Api/Startup/ApiPipeline.cs:61`),
so an agent's request can open the database first (4.6). Nothing before it may touch the vault: `PublicSurfaceMiddleware`,
`RateLimitMiddleware`, `MaintenanceMiddleware` (`:46-52`) are memory-only today; `AgentAuthMiddleware` gets the sealed
branch (4.6); `CallerScopeMiddleware` (`:64`) moves behind the new middleware. When the gate is not usable:

- **relay set** (6.1) -> `ISealedRelay.HandleAsync`, also during Importing;
- **allow-list** -> the endpoint: `/health`, `/version`, `GET /api/session/status`, `POST /api/session/unlock`,
  `POST /api/session/login`, `POST /api/session/recover-access`, `GET /api/vault/status`, the Web's static assets, `/Login`,
  `/RecoverAccess`, `/` (the Web shows the login);
- **everything else** -> 503 `{ "error": "This node's database is encrypted and closed: unlock it first.", "code": "vault_sealed" }`
  with `Retry-After: 30`; `/mcp` gets the same as a JSON-RPC error (`McpSessionGuardMiddleware`) after the agent path
  had its chance. `VaultSealedException` thrown anywhere maps to the same 503 (`Helpers/ExceptionStatusMap.cs`).

`GET /api/session/status` gains `sealed` (bool) and `state` (5.1 name). A test counts `CreateConnection` calls on the vault
and chat factories while a closed node answers the allow-list and the relay set: zero (15.3.2).

### 5.7 Temporary opens

`IVaultGate.OpenTemporarilyAsync(dek)` opens without unlocking the session, for: recover-access (3.7), a CLI verb given the
password (5.9), `bmb rekey` reading the source (13.2). The handle closes on dispose if it opened. Content routes still refuse
(they check `session.IsUnlocked`, as today); hosted services run while it is open.

### 5.8 Phones and desktops as hosts

- Android (`mobile/BeeMemoryBank.Mobile/App.xaml.cs:33-95`): `OnStart` runs migrations, folder bootstrap, `journal_mode=WAL`,
  `synchronous=NORMAL`, `PhoneRotationAutoArm` and the media backfill before unlock. On a sealed phone: `VaultStartup`
  resolution and `DetectMode` first; the rest becomes open steps (100, 200, a step 800 for `PRAGMA synchronous=NORMAL` and
  the rotation auto-arm); the unlock page and biometric unlock are unchanged (12.1).
- iPhone (`mobile/BeeMemoryBank.FullIos/Services/FullNodeServices.cs:64-82` `PrepareAsync`): the same split; quick unlock 12.2.
- Both apps call one platform-free class, `PhoneVaultLifecycle` (Vault): resolve, detect, pre-open, register the open
  steps; so the phone sequence is tested without a device (15.3.11).
- Desktop full app: the node process hosts the Api; nothing on the desktop side opens the database
  (`desktop/` has no `DbConnectionFactory` use); the Web login and OS auto-unlock (11.1) are the doors.

### 5.9 CLI

`CliServiceProvider.CreateAsync` (`server/BeeMemoryBank.Cli/CliServiceProvider.cs:31-72`) runs migrations and reads the clock at
once. On a sealed directory: no vault migration at construction; `bmb status` reads the boot store (mode, state of
the running node through `GET /api/vault/status` when one runs, inbox summary) and never needs the password; every verb that
reads or writes the vault in-process (`article`, `agent`, `user`, `init reset`, `snapshot create`) takes the master password
(`--password-stdin` or a prompt) and opens through `OpenTemporarilyAsync`, or answers "this vault is encrypted; give the
master password, or use the running node". Two processes on one sealed WAL database work.

### 5.10 Holding a closed database for a file copy

`IVaultGate.HoldClosedAsync(reason)` (A.1) is the only way the product copies the files of a sealed node while its process runs
(the pre-update copy, 8.5). It succeeds only in state Closed (in any other state it throws `InvalidOperationException`; the caller
takes a snapshot instead, 8.3). It first waits, up to 30 s, for an unlock that is in progress to end. While the lease lives
(`IsHeld`), the unlock routes look at `IsHeld` before they look at the password and answer 503 `vault_busy` with `Retry-After: 5`
(no door is used, no boot-store row is written, the caller may try again); `OpenAsync` returns `VaultOpenResult.Refused("busy")` and
`OpenTemporarilyAsync` throws `VaultBusyException` as the backstop; disposing the lease lifts the hold. The state stays Closed. The relay's writes are paused in the same breath (`ISealedRelayLifecycle.PauseWritesAsync`, 6.8), which is what
makes the boot store as still as the sealed files.

## 6. The relay inbox of a closed node

### 6.1 Routes

While the gate is Closed, Opening or Importing, `SealedVaultMiddleware` hands these to `ISealedRelay` (A.5); they
use only the boot store and the in-memory `SyncTokenStore` (`server/BeeMemoryBank.Api/Services/SyncTokenStore.cs:11-96`), never
`SyncEndpoints.cs`:

| Route (today) | While closed |
|---|---|
| `GET /api/sync/identity` (`SyncEndpoints.cs:70`) | from `tbl_boot_identity` and the protocol policy (`Declared`, capabilities) |
| `POST /api/sync/challenge` (`:94`), `POST /api/sync/authenticate` (`:115`) | as today against the whitelist mirror (active rows only); no protocol or capability observation is recorded |
| `GET /api/sync/events` (`:211`) | Closed and Opening: inbox rows after `after` (6.3), same page limits and JSON; 503 `vault_sealed_range` when `after` is below what the inbox can serve. Importing: 503 `vault_sealed_range` (6.3) |
| `POST /api/sync/events` (`:449`) | receipt into the inbox (6.2); answer shape as today (`SyncApplyResult`, stored events counted as applied, `LastAppliedSequence` = the stored prefix) |
| `POST /api/sync/report-position` (`:364`) | recorded in `tbl_relay_report` (max per peer), merged at import (6.6) |
| `POST /api/sync/blobs/check` (`:590`), `POST /api/sync/blobs` (`:609`), `POST /api/sync/blobs/get` (`:669`) | against `tbl_relay_blob` only (6.5) |
| `GET /api/sync/ping` (`:740`) | as today |
| every other sync, blind, join, readiness, activation, status route | 503 `vault_sealed` |
| every route above that would write (`POST /api/sync/events`, `report-position`, `blobs`) while writes are paused for a file copy (6.8) | 503 `vault_sealed_busy` with `Retry-After: 5`, deferrable: nothing is stored and the pusher's cursor does not move |

### 6.2 Receipt

For each pushed event, in order, with today's answer rules (`SyncEndpoints.cs:449-580`): a deferrable refusal ends the stored
prefix (the pusher's cursor stops there); a permanent one is answered as the open node would answer it, without a quarantine
row (that table is in the sealed database):

1. `SyncProtocolVersion.CanApply`; `EventInvariants.Violation` (`libs/BeeMemoryBank.Sync/EventInvariants.cs`).
2. Already in the inbox -> counted as applied (duplicates of rows in the sealed log cannot be seen while closed; the import
   drops them, 6.6).
3. Originator: an active row of the whitelist mirror, else refused as today's unknown or revoked originator (an unknown one
   is deferrable: the pusher retries; it may be a node added while this one was closed, 6.4).
4. Ed25519 signature (`EventSignature.BuildPayload`); `SealedEnvelope.WireViolation` (`[A1] libs/BeeMemoryBank.Sync/Metadata/SealedEnvelope.cs`).
5. **Clear content** (`EventClasses.IsClearContent(protocol, type)`, step-a-spec A.1) -> not stored, answered as deferred
   ("this node is locked; retry after unlock"): no protocol-1..3 content ever enters the boot store.
6. A `whitelist_revoke` or `whitelist_update` from an active superadmin of the mirror -> `IBootMirrorStore.SuspendAsync(target)`
   (6.4), then stored.
7. Stored with `origin_peer` = the pusher, `origin_seq` = its `sequenceNum`, `received_at` (UTC), `bytes` = payload length.

Limits: the push size limits of today (`SyncEndpoints.cs:60-66`); the inbox refuses with 507 Insufficient Storage once it holds
more than 2 GiB of events and blobs or free space drops under 1 GiB (environment `BMB_SEALED_INBOX_MAX_MB` overrides the first;
the pusher retries later). Logs name event ids and node ids only.

### 6.3 Sequence numbers

The inbox serves events with this node's sequence numbers, so a peer's position on this node stays meaningful across the
closed period:

- `seq_floor` (boot meta) is above every `sequence_num` the sealed `tbl_event` holds. At open
  (`ISealedRelayLifecycle.OnOpenedAsync`, before anything can append): `sqlite_sequence` of `tbl_event` is raised to the
  highest relay sequence the inbox ever served (so every append, imported or authored, gets a number above every row a peer has
  seen), then `seq_floor = seq + 1 + 1,048,576` with `seq` the raised `sqlite_sequence` value, `clean_close = 0`. While open, a
  30 s timer raises it again when `max(sequence_num) + 524,288 > seq_floor`. At a clean close (`OnClosingAsync`):
  `sealed_head = max(sequence_num)`, `seq_floor = sealed_head + 1`, `clean_close = 1`.
- Relay sequence of a new inbox row = `max(seq_floor, max(relay_seq) + 1)`.
- `GET /api/sync/events?after=P` while Closed or Opening: after a clean close, serve when `P >= sealed_head`; after a crash
  (`clean_close = 0`), serve when `P >= seq_floor - 1`; otherwise 503 `vault_sealed_range` (the peer gets those rows elsewhere or
  after unlock). While Importing: always 503 `vault_sealed_range`; rows that arrive then are never served from the inbox (they are
  imported and numbered by the sealed log), so no peer's position can pass an event the open node appends meanwhile.
- Gaps in sequence numbers are harmless: lag is counted in rows, not by subtraction (`SyncEndpoints.cs:799`).

### 6.4 The whitelist while closed

The mirror is rewritten from the sealed whitelist at every rewrite (4.3). While closed only one change is applied to it: a
verified `whitelist_revoke` or `whitelist_update` of node N from an active superadmin sets N's mirror row to status `S`
(suspended): N can no longer authenticate, push or pull here until the next unlock rewrites the mirror. Additions are not
applied (a node added while this one was closed waits for unlock: deferred, fail closed). This avoids re-implementing the
row-version rules of `EventApplier.Whitelist.cs:12-240` on the mirror; the import applies the real events in order (6.6).

### 6.5 Blobs

`blobs/check` answers "missing" for every hash not in `tbl_relay_blob` (the sealed `tbl_blob` cannot be read; a pusher then
ships blobs it would not have to; correct, only wasteful). `blobs` stores hash-verified ciphertext (`BlobHash.Matches`) with
today's size limits. `blobs/get` serves `tbl_relay_blob` and answers 503 `vault_sealed` for other hashes (deferrable on the
caller's side, `BlobMissingException`).

### 6.6 Import at open

`ISealedRelayLifecycle.ImportAsync()`, called by `CompleteOpenAsync` once the session holds the DEK, again every 60 s
while rows are left, and at the next open:

1. Blobs first: every `tbl_relay_blob` row into `tbl_blob` (`IBlobRepository.StoreAsync`, insert-or-ignore by hash), then
   deleted from the inbox.
2. Events in relay order through `EventApplier.ApplyAsync(evt, new ApplyContext(ReceivedFrom: origin_peer, ReceivedSeq: origin_seq))`
   (`[A1] EventApplier.cs:85-183`; step-a-spec A.2), without `Originators`: the whitelist lookup runs on the sealed whitelist
   as it stands after the rows before it were applied, so revocations and key changes received while closed take effect at
   their place in the order, and a node revoked before the database closed is refused (it could only have reached the inbox
   through a stale mirror). Results: `Applied`, `Held` (Step A holds what it cannot open yet; its drain applies it),
   already present (duplicate of a sealed row) and `SilentlyDropped` -> delete the row; a permanent failure ->
   `SyncEventQuarantine.RecordFailureAsync` as `SyncClient` does (`libs/BeeMemoryBank.Sync/SyncClient.cs:278-300`), delete
   the row; a deferrable failure -> keep the row and every later one, stop, retry later (the order is kept).
3. Reports: each `tbl_relay_report` row -> `ISyncPushPositionRepository.RecordReportedPositionAsync` when it is not below the
   recorded position and not above the head (the rule of `SyncEndpoints.cs:390-405`), then cleared.
4. `RewriteAsync()` (the whitelist mirror becomes the sealed whitelist; suspensions end).
5. The last pass and the switch to Open hold the relay's lock: a push that arrives during Importing either lands before the
   last pass (and is imported) or after the switch (and goes to the normal endpoint). Only rows a deferrable failure stopped
   (step 2) stay; the 60 s retry imports them, before any later inbox row.

Equivalence: an unlocked node that had received the same events in the same order would have applied them the same way;
the only difference is that a closed node refuses clear content and pushes from suspended or unknown nodes, which it gets
again after unlock.

### 6.7 Status

`GET /api/vault/status`: `{ mode, state, sealed_at, dbk_generation, inbox: { events, bytes, blobs, blob_bytes,
oldest_received_at }, auto_unlock: { role, os, server }, last_refusal: <code> | null, migration: <10.9> | null }` (`role` is
`desktop` or `server`, 11.0; `auto_unlock.server` is the effective state of 11.2, true only when the slot, the key file and the enable record
agree; `last_refusal` is a refusal code of 4.4 and never a name or a key); answers while closed (from
the boot store). It carries no path, no size of any plain file and no count of anything in the sealed database. The Web shows "Locked - N changes received, they will be applied after unlock"
on the login page and the encryption page; `bmb status` prints the same.

### 6.8 Pausing the writes for a file copy

`ISealedRelayLifecycle.PauseWritesAsync()` (A.4) takes the relay's write lock (the lock 6.6 step 5 uses), waits for the
receipts in flight to finish, and returns a lease; while it lives the write routes of 6.1 answer 503 `vault_sealed_busy` and the inbox
and the boot store receive no write from the relay (the 30 s floor timer and the mirror's watcher only run while the database is open,
and a pause is only granted while it is closed: 5.10). Reads (`GET events`, `blobs/get`, `identity`, `ping`) keep answering. The lease is
held for the seconds a copy takes (8.5); disposing it lifts the pause.

## 7. The keyed connection factory and every place that opens a database file

### 7.1 Recipe (checked with SQLite3MC 2.4.0, Appendix C)

- **Cipher per connection, by URI.** `SELECT sqlite3mc_config('default:cipher', 'sqlcipher')` changes only the connection it
  runs on (a database created afterwards through `Password=` on another connection was ChaCha20); so the Data Source is
  `file://<uri path>?cipher=sqlcipher&legacy=4` (`SqlCipherScheme.DataSource`, A.2).
- **Raw key through `Password=`:** `SqliteConnectionStringBuilder.Password = "x'<64 hex>'"`; Microsoft.Data.Sqlite sends
  `PRAGMA key` on every new physical connection and checks it with `SELECT COUNT(*) FROM sqlite_master`; a wrong key fails in
  `Open()` with SQLite error 26. `hexkey=` in a URI is a hex passphrase, not a raw key: never use it.
- **Pool and key: one contract.** Microsoft.Data.Sqlite keys its connection pool by the connection string, so the string that holds
  the key *is* the pool's identity, and the provider offers no opaque pool name; Step B does not write a pool of its own. Pooled
  reuse therefore keeps the key inside the provider's pool for the length of **one open generation and no longer**: one pool per
  factory per generation (a new generation is a new key and a new string; the previous pool is cleared before the new string is used),
  and every pool is flushed with `SqliteConnection.ClearPool` before the gate publishes the state Closed (5.4, 7.2). Connections
  that are not pooled (`OpenFile`, every staging connection, the checkpoint at close) use `Pooling=False` and hold the string
  only while they live. Nothing else may hold it: no cache key of ours, no log, no status, no exception (3.3).
- **Then** `PRAGMA journal_mode=WAL; foreign_keys=ON; busy_timeout=5000; temp_store=MEMORY` and the two functions
  (`DbConnectionFactory.cs:120-139`). `temp_store=MEMORY` because SQLite3MC does not encrypt temporary files; the engine release keeps `FILE` for plain connections (`[E] DbConnectionFactory.cs:123`), and keeps it for every plain
  connection of a sealed node (the boot store). Cost: an in-place `VACUUM` of a 351 MiB vault peaked at 257 MiB working set
  with memory temp store; the in-place `VACUUM`s of a sealed database (7.5) are rare.
- Two processes on one keyed WAL file, FTS5, `VACUUM INTO`, read-only opens and the user functions all work keyed.

### 7.2 `DbConnectionFactory`

`libs/BeeMemoryBank.Storage/Sqlite/DbConnectionFactory.cs`:

- Constructor `DbConnectionFactory(string path, IVaultKeySource? keys = null, string purpose = DbPurposes.Vault)`; registration
  `services.AddSingleton(sp => new DbConnectionFactory(dataPath, sp.GetService<IVaultKeySource>()))`
  (`libs/BeeMemoryBank.Storage/NodeDependencyInjection.cs:27`). `keys == null` or `keys.Mode == Plain`: exactly today's
  behaviour (and the engine swap's `temp_store=FILE`).
- Sealed: while `!keys.IsOpen`, `CreateConnection` throws `VaultSealedException` before touching the file; never an unkeyed
  open. While open, the connection string is built once per `OpenGeneration` from `DataSource(path)` and
  `Password(GetPageKey(purpose))` (the key copy cleared right after), kept in one private field, and dropped at close; a
  generation change clears the old pool before the new string is used.
- **Lifetime of the key-bearing string (MUST; one contract for the vault, chat and staging connections, 3.3, 7.1):**

  | Connection | Pooling | The key-bearing string is held by | It ends |
  |---|---|---|---|
  | vault connections of `DbConnectionFactory` | pooled by the provider (pool identity = the string) | the factory's one private field, the provider's pool, each live connection | `CloseForSealAsync`: connections disposed, `ClearPool`, field set to null; all before state Closed (5.4) |
  | chat connections of `ChatDbConnectionFactory`, the keep-alive connection included | pooled, its own string and pool | the same three places, for the chat factory | the same call; the keep-alive connection is disposed first |
  | `OpenFile(path, readOnly)` (staging copies, other files) | `Pooling=False` | the connection only, built per call from the current generation's key | the caller disposes it; it counts in the factory's drain (5.4); a call while closed throws `VaultSealedException` |
  | `VACUUM INTO` target (`VacuumIntoTarget`) | - | one local variable of the command | the command ends |
  | the checkpoint connection at close | `Pooling=False` | the connection only | disposed in the same step |
  | boot store | plain, no key | - | - |

  **While open** the key-bearing string may therefore be in exactly those places; **once the state is Closed** it is in none of
  them (the one bounded exception is a straggler connection that was still in use after the 30 s limit, 5.4). No other field,
  cache, dictionary, closure, log line, status answer or exception message of ours holds it, in either state.
- `DatabaseId` (`:56`, used as a cache key by `libs/BeeMemoryBank.Core/Services/FolderAccessService.cs:30` and opened directly by
  the blind `BlindSeedService.cs:731`): plain mode unchanged (a blind host's factory is always plain); sealed mode
  `DataSource(path)`, which holds no key. MUST: no string the factory exposes contains key material.
- `CloseForSealAsync(TimeSpan)` = quiesce (`:148-177`) + dispose the generation's connections + `ClearPool` of the keyed pool + drop the
  string; it returns only after all of them; used by the gate (5.4).
- `OpenFile(string path, bool readOnly)` (new): a non-pooled connection to another file (a staging copy) keyed with this
  factory's page key when sealed, plain otherwise; used by every staging site of 7.5.
- Tests: `CreateSealedForTests(name)` = `CreateInMemory` (`:44-52`) with a random page key and a fake key source.

`ChatDbConnectionFactory` (`server/BeeMemoryBank.Api/Services/ChatDbConnectionFactory.cs:23-41`): the same with purpose
`chat`; its keep-alive connection (`:31`) is opened at the first open of the gate and closed at close, never at construction on a
sealed node.

### 7.3 What fails closed

A sealed node never opens the vault or chat file without its page key: no code path catches `VaultSealedException` or
`VaultKeyMismatchException` and retries unkeyed; the factory has no "plain fallback". Red test 15.1.4.

### 7.4 Staging copies and `VACUUM INTO`

- `VACUUM INTO '<file>'` on a keyed connection writes a copy encrypted with the **same** key (verified: the copy has no magic
  and opens through `ATTACH` without a `KEY` clause in a keyed connection; Appendix C). So the snapshot statement at
  `SnapshotService.Create.cs:90` needs no change; every later open of that copy uses `OpenFile` (keyed).
- A copy under **another** key: `VACUUM INTO` `SqlCipherScheme.VacuumIntoTarget(path, key)` (the migration target 10.4, the
  re-key target 13.2).
- A plain copy of a sealed database (`VACUUM INTO 'file:...?key='`) is **never** written on a sealed node
  (section 8).
- `ATTACH` of a staging copy into a keyed connection: same key -> no `KEY` clause; other key ->
  `ATTACH 'file://...?cipher=sqlcipher&legacy=4' AS x KEY "x'<hex>'"` (verified); a plain package database -> `KEY ''`
  (verified); a plain file without `KEY` fails with error 26 (loud, good).

### 7.5 Every site

Rule letters: **F** = goes through the keyed factory or `OpenFile` (a one-line change); **S** = a staging copy, keyed with the
vault key (7.4); **C** = produces or consumes a container: plain image through the page codec (section 8); **P** = plain by
design on purpose (blind host, legacy plain input, a plain package database), no change; **U** = unchanged statement, works
keyed. Owner = the Step B part that edits the file; "ph2" = the file belongs to a Step A part and is edited once Step A's changes are in (17.2).

| Site | Today | Rule | Owner |
|---|---|---|---|
| `libs/BeeMemoryBank.Storage/Sqlite/DbConnectionFactory.cs:27, 49, 56, 69, 115, 123, 176, 245` | the factory | 7.2 | 1 |
| `server/BeeMemoryBank.Api/Services/ChatDbConnectionFactory.cs:31, 37` | chat factory | 7.2 | 1 |
| `libs/BeeMemoryBank.Storage/NodeDependencyInjection.cs:27` | registration | key source passed | 1 |
| `libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindMobileServices.cs:47, 56` | blind phone factory, WAL | P | - |
| `libs/BeeMemoryBank.Blind.PhoneClient/Sync/Blind/BlindPhoneReplicaClient.cs:182, 194, 469, 540, 580` | blind phone package | P | - |
| `server/BeeMemoryBank.Api/Services/Blind/BlindPackageBuilder.cs:124` | format-1 package (refused once activated; a sealed node is activated) | P (fails with error 26 if ever reached on a sealed node: closed) | - |
| `server/BeeMemoryBank.Api/Services/Blind/BlindSeedService.cs:258, 343, 358, 545, 562, 731` | blind seed | P | - |
| `server/BeeMemoryBank.Api/Services/BlindBackup/BlindBackupService.cs:159` | blind backup `VACUUM INTO` | P | - |
| `server/BeeMemoryBank.Api/Services/BlindConsole/BlindWipeService.cs:219` | blind wipe `VACUUM` | P | - |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Create.cs:90` | `VACUUM INTO` staging | U (copy inherits the key) | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Create.cs:105-122` (`EncryptDatabaseAsync`) | BMBDB2 of the file bytes | C | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Create.cs:44-52` (`CreateAsync` parameters) | snapshot of the live database | new `wipeStaging` (the staging copy goes through `SecureFileWipe`) for the pre-encryption snapshot, which uses the existing `outputDirectory` (10.4 step 3) | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Maintenance.cs:135, 223` (`FilterSecretsFrom`, `CompactInPlace`) | opens the staging copy | S | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Maintenance.cs:217` | `VACUUM` of staging | U (keyed, memory temp store) | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Crypto.cs:80-166` | BMBDB1/2 encrypt and decrypt the file bytes | C (reads/writes plain images in memory only) | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.Restore.cs:51, 164` | opens the staged restore | C then S | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SnapshotService.NetworkRestore.cs:83, 263` | `ATTACH` the downloaded db | C then S (`ATTACH` without key) | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/SessionSnapshotKeyOperations.cs:156` | opens the staged db | S | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/Recovery/RecoveryRestoreService.cs:230, 242, 637, 685` | package db, evidence, `ATTACH` | C then S; a plain package db `KEY ''` | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/Recovery/RestoreEvidence.cs:82` | evidence db | S | 1 ph2 |
| `server/BeeMemoryBank.Api/Services/BlindPhone/AndroidBackupRestoreSource.cs:114` | package db | C then S | 1 ph2 |
| `libs/BeeMemoryBank.Vault/Sync/SnapshotJoinClient.cs:231` | joiner `ATTACH` | P (a joiner is a new, plain node in 2.6.0, 14.3) | - |
| `server/BeeMemoryBank.Cli/Commands/SnapshotCommand.cs:52` and its encryption | CLI snapshot | U + C | 1 |
| `server/BeeMemoryBank.Api/Services/UpdateService.cs:571-605` | locked cold backup: checkpoint + copy | sealed: a quiesced set under `HoldClosedAsync` and `PauseWritesAsync`, the boot store checkpointed and copied as a file, copies verified, never the key files (8.5) | 1 |
| `libs/BeeMemoryBank.Rekey/RekeyRunner.cs:288, 301, 311, 334` | re-key copy, scrub | F; target under a new key (13.2) | 1 |
| `libs/BeeMemoryBank.Rekey/Steps/ChatRekeyStep.cs:245` | chat re-key | F (chat key) | 1 |
| `libs/BeeMemoryBank.Rekey/Steps/RekeyPreflight.cs:37, 429-447` (`IsVaultCopy`) | finds plain copies by magic | 7.6 | 1 |
| `libs/BeeMemoryBank.Storage/Sqlite/MigrationRunner.cs:174` | `VACUUM` after a migration | U | - |
| `libs/BeeMemoryBank.Sync/StoredEventRepair.cs:159` | `VACUUM` | U | - |
| `libs/BeeMemoryBank.Vault/Core/Services/NodeResetService.cs:180` | `VACUUM` | U; reset ends plain (13.1) | 3 |
| `server/BeeMemoryBank.Web/Services/LegacyMigrationService.cs:52, 136` | v1 import into a new vault | P (makes a plain vault) | - |
| `tools/BeeMemoryBank.Migrator/V1Reader.cs:48` | v1 reader | P | - |
| `mobile/BeeMemoryBank.Mobile/App.xaml.cs:47`, `mobile/BeeMemoryBank.FullIos/Services/FullNodeServices.cs:74` | WAL, synchronous pragmas at start | open steps (5.8) | 3 |
| `libs/BeeMemoryBank.AppPaths/VaultFiles.cs:34, 105` and callers `Profiles/VaultCopier.cs:38`, `Desktop/Services/ProfileSwitchService.cs:544`, `Desktop/Views/AddExistingProfileDialog.axaml.cs:69`, `CreateStorageDialog.axaml.cs:79`, `ManageStoragesWindow.axaml.cs:228, 285` | vault = SQLite magic | 7.6 | 1 |
| `libs/BeeMemoryBank.AppPaths/LegacyDataRescue.cs:119-121, 417-431` | rescue of pre-1.0 plain installs | P | - |
| Step A's baseline producer and self-check (`step-a-spec` 7.3) and the for-join archive (11) | `VACUUM INTO` staging, plain image into BMBCH1 | S + C | 1 ph2 |
| SQLite backup API | not used by the product | - | - |

### 7.6 Recognising a vault without the magic header

- `VaultFiles.Inspect` (`libs/BeeMemoryBank.AppPaths/VaultFiles.cs:66-77`): a folder is a vault when `beememorybank.db` has the
  SQLite magic **or** `beememorybank.boot.db` has the magic and `application_id = 0x424D4253` (`VaultStoreFiles`, A.2 - AppPaths must
  not reference Storage, so the same small checks are written once more as `VaultStoreProbe` in
  `libs/BeeMemoryBank.AppPaths/VaultStoreProbe.cs`: `HasSqliteMagic`, `ReadApplicationId`, `IsSealedPair(dataDir)` - boot store with
  our magic and application id beside a non-empty database without the SQLite magic whose length is a multiple of 4096 **and no
  plain companion** (2.5: `PlainCompanions(dataDir)`, the same plain page test on `chat.db` and the WAL and journal files, written
  once more beside it) - and `HasSealOldLeftovers`; `VaultFiles.Inspect` and the `SealSwapResolver` of 10.6, both in AppPaths, use
  it, and the scanner of 10.2 uses the page test for its WAL and journal rule; a test in a project that references both assemblies
  runs `VaultStoreProbe` and `VaultStoreFiles.DetectMode` on the cases of 15.1.15 (plain companions included, with real sealed and
  real plain WAL and journal files) and asserts that they agree, so the two copies cannot drift). The six callers need no change; `VaultCopier`
  copies the boot store like every other non-transient file and its verification covers it (test).
- `RekeyPreflight.IsVaultCopy` (`libs/BeeMemoryBank.Rekey/Steps/RekeyPreflight.cs:429-447`) looks for stray copies of the vault
  that a re-key would leave readable. A sealed copy has no magic: a file of at least 4096 bytes, a multiple of 4096 long, that
  opens with the vault page key and has `tbl_article_body` and `tbl_key_slot` is reported as "an encrypted copy of this vault
  (opens with the current key)"; a file of that shape that does not open is reported as "a file that may be an encrypted copy
  (cannot be checked)". The check must never silently stop finding copies.

## 8. No plaintext image of the vault on disk

### 8.1 The rule

On a sealed node no code path writes a plain SQLite image of vault data to disk: not in `tmp/`, not as a temporary file,
not for a millisecond. A deleted plain file survives in free space on SSDs and copy-on-write file systems, so "staged then
deleted" would make every daily snapshot and baseline a leak at rest. Containers that must carry plain SQLite bytes
(BMBDB2 snapshots, BMBCH1 baselines and join archives: formats every 2.5 and 2.6 reader understands and that stay unchanged)
get them from memory or a stream. `SnapshotService.CreateAsync(encryptDb: false)` (`SnapshotService.Create.cs:46-52`) refuses on a
sealed node unless the caller passes a container writer.

### 8.2 Plain images and the page codec

`libs/BeeMemoryBank.Storage/Sealed/SqlCipherPages.cs`, format in Appendix C:

- `PlainImageReader : Stream` - reads a closed, checkpointed SQLCipher 4 file under a page key and yields its plain image page
  by page: every page's HMAC-SHA512 verified (a mismatch throws `InvalidDataException`), AES-256-CBC decrypted, page 1's first
  16 bytes set to `SQLite format 3\0`, the 80 reserved bytes of each page zeroed, header bytes 18 and 19 set to 1 (rollback
  journal: an image cannot be in WAL mode). The image is a valid SQLite file with 80 reserved bytes per page, which stock SQLite
  and SQLite3MC read (verified, Appendix C).
- `SealedFileWriter` - the reverse for an image with page size 4096 and at least 80 reserved bytes: random salt, random IV per
  page, HMAC; the result opens with SQLite3MC under that key (verified).
- `PlainImageImporter.ToSealedFileAsync(Stream image, long length, string target, byte[] pageKey, long memoryCap)`:
  - image page size 4096 and reserved bytes >= 80 (every image a 2.6 reader made, 8.3) -> `SealedFileWriter`, streaming, bounded
    memory;
  - otherwise (an image from a 2.5 node or a plain 2.6 node: reserved 0) and `length <= memoryCap` -> the whole image in memory,
    `sqlite3_deserialize` into a memory connection (SQLitePCLRaw 3.0.2 exposes it; verified), `VACUUM INTO`
    `SqlCipherScheme.VacuumIntoTarget(target, pageKey)` (verified), memory released;
  - otherwise -> `SealedImportTooLargeException`: "This archive was made by an older version and is too large to import into an
    encrypted database here. Restore it on a node that is not encrypted, or have an updated node make it again." Fail closed.
  - `memoryCap`: 1 GiB on desktops and servers, 256 MiB on phones; `BMB_SEALED_IMPORT_MEMORY_MB` overrides.

Every page key passed in is a copy the caller clears.

### 8.3 Producers on a sealed node

| Producer | Today | Sealed node |
|---|---|---|
| snapshots (backups, pre-update, compaction, pre-rotation; `SnapshotService.CreateAsync`, `Create.cs:46-200`) | `VACUUM INTO` staging, filter, then `EncryptDbFileAsync` reads the file and writes BMBDB2 over it (`Crypto.cs:80-118`) | staging inherits the key (S); filter through `OpenFile`; BMBDB2 built from `PlainImageReader` into memory (as today the whole file is read into memory, 2 GB limit unchanged), written over the staging path |
| baseline (step-a-spec 7.2-7.3) | `VACUUM INTO` staging, filter, seal into BMBCH1 | staging keyed; `PlainImageReader` streamed into the BMBCH1 writer; self-check (7.3 step 4): decrypt the container stream into `SealedFileWriter` (a second keyed staging file), `PRAGMA integrity_check` and the row counts on it; never a plain file |
| join archive after activation (step-a-spec 11) | BMBCH1 of the filtered staging | as the baseline |
| CLI `bmb snapshot create` (`SnapshotCommand.cs:42-115`) | `VACUUM INTO` + BMBDB2 | as snapshots |
| blind packages format 2 (step-a-spec 8) | baseline container + `control.db` + deltas | unchanged: `control.db` is a plain file of the control tables only (MEK rows, activation, recovery boxes, links, anchors, sealed secrets), allowed in clear by Step A; it is written in the package staging folder and holds no metadata |
| format-1 blind packages | plain filtered copy | never on a sealed node (activated nodes refuse them, step-a-spec 2) |

### 8.4 Consumers on a sealed node

| Consumer | Sealed node |
|---|---|
| snapshot restore, standalone or same node (`SnapshotService.RestoreAsync`, `SnapshotService.Restore.cs`) | BMBDB2 decrypted in memory (as today) -> `PlainImageImporter` -> staging under the vault page key (same DBK: the DBK is node-local and survives the restore) -> the existing staging edits through `OpenFile` -> swap. Before the swap: `IUnlockMaterialMirror.AddDbKeyWrapperAsync(stagedDek)` and `AddPendingDoorAsync` for each key slot of the staged database (so the restored doors open it after a crash); after: `RewriteAsync` |
| network restore (`SnapshotService.NetworkRestore.cs:83, 263`) | downloaded image -> importer -> staging under the vault key -> `ATTACH` without `KEY` -> the existing copy |
| restore from a blind copy (step-a-spec 12; `RecoveryRestoreService.cs:99-480`) | baseline stream -> importer -> staging -> import as Step A says; the package's `control.db` attached with `KEY ''`; slot writes (`:368`) followed by `RewriteAsync` |
| Android blind backup restore (`AndroidBackupRestoreSource.cs:114`) | as the restore from a blind copy |
| restore of the pre-encryption snapshot (10.7) | `restore-standalone <file name>` looks the name up in `snapshots/` and then in `pre-encryption/`; a name that is in both is refused with nothing changed, and the message says to rename or delete the ordinary snapshot with that name in `snapshots/` (listed on the Snapshots page) and try again, or to give the folder with the name (`pre-encryption/<file name>`); new snapshots, the CLI's included, never take a name held in either folder, so a name in both can only come from files made earlier; on a node whose own key does not open the snapshot it opens it through the key envelope with the master password given for the restore (10.7); the restored vault is plain when the node is plain (the rollback) and sealed when the node is sealed |
| join (joiner) | the joiner is a new vault: plain in 2.6.0 (14.3); unchanged |

### 8.5 Backups the product makes of the files

`UpdateService.TakePreUpdateBackupAsync` (`server/BeeMemoryBank.Api/Services/UpdateService.cs:571-605`):

- **Unlocked** (plain or sealed): a snapshot (8.3), as today. On a sealed node it is encrypted under the master DEK and holds no
  boot store; restoring it is 8.4, which rewrites the boot store.
- **Locked, plain node:** as today (checkpoint, copy).
- **Locked, sealed node: the quiesced set.** This is the only copy of the live files of a running sealed node the product ever
  makes, and it follows the same rule as the operator's stop-copy-start of 8.7.1: the three files are copied only while nothing can
  write any of them. There is one protocol, not two:
  1. `IVaultGate.HoldClosedAsync("pre-update")` (5.10). If it throws (the node was unlocked in the meantime) the snapshot above is
     taken instead.
  2. `ISealedRelayLifecycle.PauseWritesAsync()` (6.8): the inbox and the boot store receive no write.
  3. `PRAGMA wal_checkpoint(TRUNCATE)` on the boot store through a plain connection (no key needed), so that its main file is
     complete. The sealed files are not checkpointed here (it would need the key); they were at the clean close (5.4), so their main
     files are self-contained. A non-empty `-wal` or `-shm` beside them means the close was not clean (a crash): it belongs to the set
     and is copied with it.
  4. Copy `beememorybank.db`, `chat.db` (with the non-empty `-wal`/`-shm` when present) and `beememorybank.boot.db` into
     `updates/pre-update-<version>/`; then read each source again and compare its length and SHA-256 with the copy's. Any
     difference (a file changed while held, which cannot happen) deletes the copy, lifts the pause and the hold, ends with the
     failure code `backup_inconsistent`, and the update does not go on.
  5. Lift the pause and the hold (also on every error path).

  The boot store is not copied with `VACUUM INTO`: a quiesced set is file for file what a stopped node would have on disk, and the
  copy is restored by copying back (8.7.2). While the hold lasts (seconds) unlocks answer 503 `vault_busy` and peers' pushes 503
  `vault_sealed_busy`; both are deferrable and retried.
- Never copied, here or in any other product-made backup: `server-auto-unlock.key`, `server-auto-unlock.enabled`,
  `os-auto-unlock.dat` (a backup must not carry the key that opens it, and the enable record is local state, 11.2). The copy holds
  only sealed files, so it is not a plain copy in the sense of 10.7. Restoring it = 8.7.2.

### 8.6 Plain by design

Blind hosts (Docker blind node, blind phones and desktops) keep a plain operational database: after Step A it holds no metadata
(step-a-spec 9.1) and they never hold a master DEK. The v1 import (`LegacyMigrationService`, the Migrator) makes a plain vault,
which the owner may then seal. A ZIP export or a download the owner asks for is plaintext by request (residual, 2.1).

### 8.7 File-level backup and restore: two contracts

There are two supported backup contracts and they must not be mixed up in the operator guide (`docs/sealed-database.md`,
`docs/deployment.md`).

**8.7.1 A sealed full node (server, desktop, phone).** What an operator's tool (restic, rsync, a volume or file-system
snapshot) may copy, and when:

| Source | Supported | Why |
|---|---|---|
| the **data directory of a stopped node** (Docker: `docker compose stop bmb` first; service stopped; process exited by a clean shutdown) | yes, the whole directory in one run | 5.4 checkpoints and closes both databases at lock and at shutdown, so `beememorybank.db`, `chat.db` and `beememorybank.boot.db` are each self-contained and belong to the same moment; a `-wal`/`-shm` left by a crash is part of the set and must be copied with it |
| `snapshots/` of a running node (BMBDB2 files, written complete and atomically) | yes, at any time | they are the running node's backup: encrypted under the master DEK, restorable by 8.4 (standalone restore: a plain node, which the owner may seal again) |
| `media/`, `search-index-segments/`, `baselines/` | yes (already encrypted); copy them in the same run as the databases when restoring a full set | |
| the database files, `-wal`, `-shm` or the boot store of a **running** node (open or closed-but-running) | **no** | three files change at different moments (a lock rewrites the boot store and checkpoints the others; the inbox writes the boot store while the others stand still): a copy can show a state no crash could produce. The operator guide says so and gives the stop-copy-start recipe |
| `server-auto-unlock.*` (the key and the enable record), `os-auto-unlock.dat` | **never in a backup** | a backup that carries the key that opens it is a plain copy; the enable record is local state of the server that nothing restored may bring back (11.2); the recipes use `--exclude 'server-auto-unlock.*' --exclude os-auto-unlock.dat`; the key file named by `BMB_AUTO_UNLOCK_KEY_FILE` lives outside the data volume (11.2) |
| `seal-journal.json`, `*.seal-new`, `*.seal-old*` | part of the set when present | a set taken mid-migration restores into the resolver's hands (10.6), which is deterministic |

A pre-update copy (8.5) is taken by the product under the hold of 5.10 and 6.8, which is the in-process form of this table's first
row (nothing can write any of the three files); a "Back up now" snapshot is a snapshot of the running node and needs no hold.

**8.7.2 Restore of a file set** (a disaster restore, or going back to a pre-update copy). Procedure, the same in the operator guide
and in `bmb vault verify`'s output: stop the node; put **all** files of one backup set into the data directory (never a database
from one set and a boot store from another); start the node; it comes up closed. The product validates, in this order, and each
check fails closed with a named reason (nothing is opened, nothing is created beside it):

1. `VaultStartup.Enter` and `SealSwapResolver` (10.6): a set with a journal or `.seal-new`/`.seal-old*` files is resolved as after a
   crash; a leftover `.seal-old*` in a set that has a sealed pair is wiped before anything else, a leftover it cannot place ends the start
   with a named refusal.
2. `VaultStoreFiles.Diagnose` (2.5): an encrypted database without its boot store, a boot store beside a plain database, a boot store that is not ours,
   an empty database file or one whose length is not a multiple of 4096 (a copy cut inside a page), or a sealed pair beside a plain
   companion (a plain `chat.db`, or a WAL or journal of either database that holds a plain page: a set assembled from a plain backup and
   a sealed one) -> **Broken**, the node refuses to start and names the reason, the files and the two ways back.
3. The first unlock (4.4): the wrapper of the door's DEK unwraps the DBK and the page key opens the file (a boot store of another node
   or lineage -> `key_mismatch`, 5.3 step 3); the pair diagnosis of 4.4 step 5 names a database older than its boot store `store_stale`.
4. `bmb vault verify` (new verb; `--password-stdin`): opens the database temporarily and runs `PRAGMA integrity_check` on the
   vault and on `chat.db` (the page HMACs make a cut-off or damaged page an error, not silent garbage), `PRAGMA integrity_check` on
   the boot store, and checks that every key slot and auto-unlock agent has a door and that the wrapper of the current DEK
   unwraps. It prints `ok` or the failing check. The operator guide makes it the step before the node meets its peers again.
5. **Server auto-unlock: a restore never turns it on (the one rule of 11.2 item 3).** The first start of a restored set applies the
   three-part rule of 11.2 before it reads any key or tries any unlock; the enable record is local state that no set carries (8.7.1),
   so a restore starts the server locked unless the local record of this very server still matches the restored slot. (i) **An
   in-place restore over the very server that enabled it**, whose own enable record and key file are still where they were and whose
   restored slot is byte-identical to the one the record names: the three parts agree and the server stays armed as the owner left
   it. That is the preservation of an earlier explicit owner choice, not a new enable operation - the restore created no slot, no
   record and no key file, and the product cannot tell such a restore from a restart (19.13). (ii) **Every other restore** - a fresh
   one into an emptied or new directory, one on another machine, a copy, any restore whose slot differs from the one the record
   names: the set's `server_auto_unlock` slot has no matching enable record here, so it is stale: locked start, the door retired in
   the boot store at once, the slot deleted at the first unlock, an audit entry. This holds with the operator's key file
   (`BMB_AUTO_UNLOCK_KEY_FILE`) still mounted and with the node-created key file absent or present. Turning it on again takes the
   owner's two-step enable (11.2) on the restored node. A restore never creates the record.
6. A restored data directory brings the node back **as it was at the backup moment, with its node id and log head**. Whether that
   is safe for the mesh is the question of 14.5 (rollback and "keep-identity" restore): the guide sends the operator there.

A restore never reintroduces a plain copy: a set taken from a sealed node holds only sealed files; a set taken before the
migration is a plain set and restoring it yields a plain node (that is the rollback of 10.7, deliberate).

**8.7.3 A blind node** is not sealed and never will be. Its backups keep the contract of step-a-spec 9.8: `BlindBackupService`'s
`VACUUM INTO` staging copy of the operational database plus `baselines/current/` through restic, no note count anywhere;
restore from such a backup is step-a-spec 12 with the backup folder as the source. Step B changes nothing there; the end-to-end
test scans the backup folder in process after the canary mesh has run (15.4.6) and the Linux and Docker check runs the restic binary round trip in the
blind container and scans the restored files (15.5 step 9).

## 9. `chat.db`

Sealed with the vault, under the chat page key (3.2): the migration converts it (10.4), the factory keys it (7.2), the
initializer runs as an open step (5.3), the chat re-key step opens it keyed (`ChatRekeyStep.cs:245`). Row sealing under the
node chat key (`tbl_node_data_key`, migration 026) stays: two layers. Chat titles (`chat_conversation.title`, plain rows today)
are covered by the page encryption. A sealed node without `chat.db` (a CLI-only deployment) has nothing to convert. Node reset
deletes it as today (`INodeResetHook`).

## 10. Encrypt this node: the owner-started migration

### 10.1 Who and where

The owner, per node, explicitly: Admin page "Database encryption" (`/VaultEncryption`, Web), `POST /api/vault/seal` (internal key, superadmin, not an agent, password re-entry), `bmb vault seal` (calls the
running node; `--password-stdin`), Android Security page, iPhone settings (a new page). Nothing starts it by itself: no
upgrade, start, join or restore migrates a node.

### 10.2 Preflight (`GET /api/vault/seal/preflight`)

Each blocker has a code and a sentence; the migration starts only when every condition holds:

| Blocker code | Must hold |
|---|---|
| `not_full_node` | a blind host is never sealed |
| `already_sealed` | mode is Sealed |
| `vault_locked` | the session is unlocked (the DEK wraps the DBK) |
| `not_superadmin` | the caller is a superadmin, not an agent |
| `not_activated` | Step A is activated on this node (`ISyncProtocolPolicy.SealsMetadata`): a closed node can only relay sealed events (6.2); the order is "activate, then encrypt" |
| `events_held` | `IHeldEventStatus.AnyHeldAsync()` is false (step-a-spec A.5) |
| `rotation_pending` | no `tbl_dek_rotation_state` row Proposed or Committing, and the marker `mek_publish_pending` of 14.4 item 7 is not set (the node still has to publish a new metadata key) |
| `restore_running` | no restore Pending/Downloading/Applying, no maintenance mode |
| `identity_v0` | the node identity seed is wrapped (`ed25519_private_key_v = 1`; the post-unlock catch-up upgrades v0, `SessionService.cs:466-495`) |
| `integrity` | `PRAGMA integrity_check` = `ok` on the vault and on `chat.db` |
| `free_space` | free bytes on the data volume >= 3.5 x (database + `-wal` + `chat.db` + its `-wal`): the two sealed targets, the pre-encryption snapshot and `VACUUM` headroom |
| `snapshot_limit` | the database file is within what the pre-encryption snapshot can hold (`SnapshotService.MaxEncryptableDbSize`, 2 GiB today, `SnapshotService.Crypto.cs:24`: BMBDB2 reads the whole file into memory). Above it no way-back snapshot can be made: the migration starts only if the owner ticks "I keep my own offline backup of this node; encrypting cannot be undone from the node itself" (the same tick as `phone_no_copy`, 10.8), and the step 3 of 10.4 is then skipped |
| `engine` | `sqlite3mc_version()` answers and a keyed round trip in `tmp/` works |
| `unc_path` | the data directory is a local path (A.2 refuses UNC) |
| `plain_copies` | `PlainVaultCopyScanner` (below), in `Preflight` mode, finds nothing: no file of the data directory, whatever its name or folder, is a plain image of the vault or of the chat history (or a WAL or journal with plain pages, or an archive holding one as any member) other than the two live files the migration replaces, and neither live file has a second name |
| `mobile_foreground` | phones: the app is in the foreground, battery >= 30 % or charging |

There is no "log compacted past activation" requirement: the log stays in the sealed file (2.4), so its pre-activation
plaintext rows are covered by the page encryption.

**`PlainVaultCopyScanner`** (`libs/BeeMemoryBank.AppPaths/PlainVaultCopyScanner.cs`: the content-based scan opens no SQLite
connection, and AppPaths - a library with no project reference, which the resolver, the Api, the CLI, Profiles and Rekey can all
reach - is the one place every caller can use; `SecureFileWipe` and `FileIdentity` live there too). `Scan(dataPath, mode)` decides **by content, never by name, folder or schema**, and **skips nothing** apart from an unreadable runtime lock whose exact live-handle identity is proven:
`media/`, `search-index-segments/`, `snapshots/`, `baselines/`, `pre-encryption/`, `updates/` and `tmp/` are walked like every other
folder. It does not follow a symbolic link or reparse point (it reports the link as a finding of kind `link`, its target is not
scanned). For every regular file it opens the file with full sharing, reads the first 512 bytes and classifies it:

| Content | Verdict |
|---|---|
| first 16 bytes `SQLite format 3\0` | finding `plain_sqlite` (whatever tables it has: there is no table-name test) unless the file is a live file the mode allows (below) |
| WAL header (magic `0x377F0682` or `0x377F0683` at offset 0, version 3007000, a power-of-two page size from 512 to 65536) or rollback-journal magic (`D9 D5 05 F9 20 A1 63 D7`) | the **plain page test of 2.5** is applied to its frames (records): a plain page image -> finding `plain_journal`, unless it is the `-wal` or `-journal` of a plain live file the mode allows; no plain page (a sealed file's own WAL or journal, a WAL with only its header) -> fine. **The magic alone decides nothing**: the WAL and the journal of a sealed file have the same plain header (Appendix C), so a rule "WAL magic = plain" would flag every open sealed node |
| gzip header (`1F 8B 08`, reserved flag bits clear) | an archive (below): the first 512 inflated bytes start with the SQLite magic -> `plain_archive`; they are a tar -> every member is read; anything else (a rotated log) -> fine; a stream that cannot be inflated -> `unverified_archive` |
| tar header (the 512-byte block at offset 0 has a valid header checksum; the `ustar` magic is not required, so v7, pax and GNU tars count) | an archive (below): every member |
| zip local header `PK\x03\x04` | an archive (below): every entry |
| the magic of another compression or archive format this version cannot open: bzip2 (`BZh`, a digit 1-9, `31 41 59 26 53 59`), xz (`FD 37 7A 58 5A 00`), zstd (`28 B5 2F FD`), lz4 (`04 22 4D 18`), 7-Zip (`37 7A BC AF 27 1C`), RAR (`52 61 72 21 1A 07`) | finding `unverified_archive` ("an archive of a kind this version cannot look into"); the owner moves it out of the data folder |
| anything else | fine: SQLite cannot read it as a plain database; this includes every SQLCipher-encrypted file, a sealed copy of the vault included |

**Archives: every member, whatever it is called (MUST).** The verdict on an archive never uses a member's name, extension, folder,
size or position: `payload.bin`, `data`, `x.jpg` and `beememorybank.db` are judged alike, and the first, the third and the last
member are judged alike. Members are read as streams in memory; nothing is extracted to disk.

- A **tar** (plain, or inside a gzip) is read from its first entry to its end (pax and GNU extended headers merged,
  `System.Formats.Tar`); a **zip** is read through its central directory, every entry. For every regular-file member the first 16
  bytes (512 for the nested-archive test) are read and:
  - they start with the SQLite magic -> the archive is a finding `plain_archive` (the finding is the archive file, its detail names
    the member; the scanner stops reading that archive);
  - they start like a gzip, a tar, a zip or one of the formats of the table that this version cannot open (**a nested archive**) ->
    `unverified_archive` ("an archive inside an archive, which this version does not open"). Nested archives are not opened in
    2.6.0 and are never silently accepted;
  - anything else (BMBDB1 or BMBDB2, BMBCH1, SQLCipher bytes, an encrypted media blob, text, an image) -> that member is fine.
  Directory, symbolic-link and hard-link entries carry no bytes of their own and are skipped (the scanner never extracts, so a link
  entry cannot reach another file).
- The archive's verdict is the worst of its members and of the next point: `plain_archive`, then `unverified_archive`, then fine.
- **Fail closed, `unverified_archive`**, also when the archive cannot be read to its end or its content cannot be seen: truncated or
  corrupt data, a tar header whose checksum fails after the first block, an entry type or sparse layout the reader does not support, a
  gzip stream that cannot be inflated, a zip whose central directory cannot be read, a zip entry that is encrypted (general-purpose
  bit 0, or an AES extra field) or compressed with a method the platform cannot open (anything but stored, deflate and deflate64),
  or a resource limit: **100,000 entries** or **16 GiB of inflated bytes** read from one archive (constants of the scanner; the
  tests inject smaller ones). A file that cannot be opened at all stays `unreadable`.
- An archive whose container does not begin at offset 0 (a self-extracting archive, a file with an archive appended) is not opened;
  that is a documented limit (19.18).

Allowed live files: in mode `Preflight` the top-level `beememorybank.db` and `chat.db` (plain, about to be replaced) and their
`-wal`/`-shm`/`-journal`; in mode `Sealed` the top-level `beememorybank.boot.db` and its sidecars (plain by design) - in `Sealed` mode a
plain `beememorybank.db` or `chat.db` is a finding. In both modes a **copy of the boot store** anywhere (a SQLite file whose header
application id, bytes 68-71, is `VaultStoreFiles.BootApplicationId`; the quiesced set of 8.5 holds one) is not a finding of
`plain_sqlite`: the boot store carries only allow-listed columns (2.3), so a copy of it is exactly as exposed as the original, and
the erase must never wipe the boot store of a pre-update set (a vault database has application id 0). The `-wal` and `-journal`
of the sealed files, in `Sealed` mode, are judged by the plain page test (a sealed node's own WAL is not a finding, a plain one is). A file that cannot be opened or read is a finding of kind `unreadable` (fail closed,
apart from an empty regular runtime-lock file whose path identity equals a handle held by this node right now: `bmbd` hands its `node.lock` proof to
the Api child and an Api registers its own `.instance.lock`; links, reparse points, directories, hard links, a changed identity, or a nonzero length before
or after the failed read are never exempt). The owner moves every other unreadable path. The scan does not stop at the first finding. A finding is `(relativePath, bytes, lastWriteUtc, kind)`.

**Other names of the same bytes (hard links).** The scanner takes the file identity (`FileIdentity`, `libs/BeeMemoryBank.AppPaths/FileIdentity.cs` next to `SecureFileWipe`: volume and file index,
Windows `GetFileInformationByHandle`, Linux and macOS `stat`/`fstat`; null when it cannot be read) of each live file and of each finding.
A finding with the same identity as a live file is reported as `shares_identity` (a second name for the live database or its
sidecar), and a live file whose link count is above one is reported as a finding `live_linked` on the live file itself (another name
exists somewhere, in the data directory or outside it). When the identity of a live file cannot be determined on the platform the scan
answers the finding `identity_unavailable` and the preflight stays blocked (fail closed; the checks of 15.5 prove the Windows, Linux and
macOS implementations). Findings of kind `shares_identity`, `live_linked`, `link`, `unreadable` and `unverified_archive` are **never
erased by the product**: erasing through the user-selected name would overwrite the live database through its second name. The page
says what the owner has to do ("This file is another name for your database. Remove that name yourself.").

Typical findings: `updates/pre-update-*/beememorybank.db` from a locked update before the switch, `tmp/` leftovers,
`*.standalone-staging`, `*.rekey-*` folders, a plain snapshot someone left in `snapshots/` (`dbEncrypted: false`), a `.gz`, `.zip`, `.tar` or `.tar.gz` holding
a database as any member under any name, a copy with another extension. A blocker `plain_copies` shows them in words ("These files are unencrypted copies of your
vault. A stolen disk would open them."). The owner has two ways out: **move them out of the data folder** (an offline backup kept
elsewhere is the owner's own affair), or press **Erase these copies** (`POST /api/vault/seal/erase-plain-copies`, body
`{ "password": "<master password>", "paths": [...] }`, internal key, superadmin, not an agent, 401 on a wrong password): the server runs
the scanner again and erases exactly the paths that are both in `paths` and in the fresh result with kind `plain_sqlite`,
`plain_journal` or `plain_archive` - never any other path, never one outside the data directory, never a link, never one of the kinds
above that cannot be erased - with the wipe of 10.6 (which itself refuses to overwrite a file that has another name: A.6), and writes
an audit entry `plain_copies_erased` (a count, no names). The answer lists what was erased and, for each refused path, its kind. The
same endpoint works on a sealed node (mode `Sealed`) for a copy found after the switch. The migration itself erases nothing but the
files it replaces (10.6). The scanner also runs at the end of the migration (10.4 step 11) and in the final scan of every kill-point
test.

### 10.3 Journal

`<data>/seal-journal.json`, written like `RekeySwapJournal.Write` (temp file, flush to disk, rename, directory flushed;
`libs/BeeMemoryBank.AppPaths/RekeySwapResolver.cs:50-66`): `{ "phase": ..., "started_at": ..., "files": ["beememorybank.db", "chat.db"],
"snapshot": "pre-encryption/<file name>" }` (`snapshot` is empty on phones, 10.8, and when the owner ticked `snapshot_limit`, 10.2). Phases: `building` -> `verified` -> `swapping`; the
journal is deleted when the swap and the wipe of 10.6 are finished. It holds no name, no path of a note, no key. A journal that cannot
be read is never deleted by the start that finds it: it is renamed `seal-journal.json.unreadable` and kept until the repair of 10.6 has
ended with no `*.seal-*` file left.

### 10.4 Steps (`SealMigrationService`)

1. Preflight again under the lock; maintenance mode (`MaintenanceModeService.Enter("Encrypting the database...")`); pause
   `SyncScheduler` (`PauseAsync`); hold `EventWriteGate`; wait for the hosted services' current iteration; checkpoint
   (`PRAGMA wal_checkpoint(TRUNCATE)`) the vault and chat databases.
2. Journal `building`.
3. **Pre-encryption snapshot** (servers and desktops; phones 10.8; skipped only when the owner ticked `snapshot_limit` of 10.2): `SnapshotService.CreateAsync(filterSecrets: false, sign: false,
   encryptDb: true, outputDirectory: "<data>/pre-encryption", wipeStaging: true, additions: <the key envelope>)` - the product's
   existing snapshot (BMBDB2, the whole database encrypted under the master DEK) plus the entry `key-envelope.json` (10.7, built from
   the key slots of the open database; `SnapshotAdditions.ExtraFiles` already lists such an entry in the signed manifest), written
   to a folder the listing, the retention (`PruneOldSnapshots(keepCount: 2)` would otherwise delete it) and the delete button do not
   read (`SnapshotService.Create.cs:45`). Its plain staging copy is wiped, not just deleted (`wipeStaging`, 10.6; an edit of
   `SnapshotService.Create.cs`). Self-check before the migration goes on: the container decrypts with the session's DEK to a
   stream that starts with the SQLite magic and whose length equals the staging copy's, **and the key envelope opens it**: with the
   master password given to the preflight (the owner types it for the migration, 10.1) the envelope's slots yield a DEK whose
   sentinel check passes and which decrypts the snapshot's database header. Failure: delete the file, failure code `snapshot`, nothing
   else touched. The name goes into the journal.
4. `dbk = IDatabaseKeyService.Create()`; page keys; generation 1.
5. `VACUUM INTO` `SqlCipherScheme.VacuumIntoTarget("<data>/beememorybank.db.seal-new", vaultKey)` from a plain connection of the
   live database; the same for `chat.db.seal-new`. (The source is the plain file itself: nothing new is written in clear.)
6. `IUnlockMaterialMirror.WriteNewBootStoreAsync("<data>/beememorybank.boot.db.seal-new", <keyed factory on the target>, dbk, 1,
   session.GetCandidateDeks())`; boot meta: `node_id`, `dbk_generation = 1`, `sealed_at`, `activated = 1`, `seq_floor`, `clean_close = 0`.
7. Verification (10.5) on the three targets. Any failure: wipe and delete the three targets (they are encrypted, but they hold a
   key-less image of the vault; delete is enough, wipe is cheap), delete the journal, leave maintenance, report the failing check;
   the node is unchanged and the pre-encryption snapshot stays where it is.
8. Flush the targets to disk; journal `verified`.
9. Quiesce the vault and chat factories (no connection left), journal `swapping`, then the swap **and the wipe**: the service runs
   the resolver's forward path (10.6) itself, which ends with the plain files wiped and deleted.
10. The gate switches to Sealed and opens with the session's DEK (4.4 steps 3-8, without a door); leave maintenance; resume the
    scheduler.
11. Final scan: `PlainVaultCopyScanner.Scan(dataPath, Sealed)` must be empty and no `*.seal-old*` file may exist. If the wipe of 10.6 could
    not remove something, the journal stays and the status says so ("A leftover unencrypted file could not be removed: <relative
    name>. Restart the node, or remove it yourself."); the next start retries. A finding the scan makes that is not a `*.seal-old*`
    file (a plain copy that appeared meanwhile, a file another name of which exists) does not undo the migration: the status
    `done` carries `plain_copies_found` with the relative names (signed-in answers only) and the owner uses "Erase these copies"
    (10.2) or removes the other name.
12. Audit entry `vault_sealed` (no names); status (10.9) `done`, with the name and size of the pre-encryption snapshot (10.7).

### 10.5 Verification (MUST, before the journal says `verified`)

On the targets, through keyed connections:

- `PRAGMA integrity_check` = `ok`, `PRAGMA foreign_key_check` empty (vault and chat);
- the row count of every table of the source (user tables and FTS shadow tables) equals the target's; `tbl_migration` equal;
  `max(sequence_num)` of `tbl_event` and the `tbl_event` row of `sqlite_sequence` equal (no log position is truncated, lowered or
  reused by the copy);
- the sentinel (`tbl_node_identity.sentinel_value`) verifies with the session's DEK;
- up to 20 random article bodies (blob + `tbl_article_body` key) decrypt with the session through the target;
- SHA-256 of `SELECT id, tree_path FROM tbl_folder ORDER BY id` and of `SELECT id, title FROM tbl_article ORDER BY id` equal;
  an FTS query for a word from 5 random titles returns the same ids;
- raw bytes: the targets do not start with the SQLite magic, and none of the 20 sampled titles (UTF-8) and folder paths occur
  in the target files (a canary scan on real data);
- the boot target: its schema equals the golden (2.3); one door per key slot and auto-unlock agent of the source; the DBK
  unwraps with the session's DEK; the vault page key derived from it opens the target.

### 10.6 Swap and its resolver

`SealSwapResolver.Resolve(dataDir)` (`libs/BeeMemoryBank.AppPaths`), called by `VaultStartup.Enter` under the exclusive
lease right after `RekeySwapResolver.Resolve` (`VaultStartup.cs:66`), decides from **what is on disk first and the journal second**,
like the re-key resolver. A **leftover** below is any file named `beememorybank.db.seal-old*` or `chat.db.seal-old*` (`-wal`, `-shm`,
`-journal` included); a leftover is a startup repair state whether or not a journal exists.

Reading the journal: a valid one has the phase `building`, `verified` or `swapping`. A journal that is missing is "no journal". A journal
that cannot be read (not JSON, wrong shape, an unknown phase) is **not deleted**: it is renamed `seal-journal.json.unreadable` (flushed)
and kept as evidence until the repair ends; the case then counts as "no journal".

- journal `building`, no leftover (or no journal and only `.seal-new` targets, no leftover): delete the `.seal-new` targets, delete the
  journal; the node stays plain with its old files (they were never touched). The pre-encryption snapshot, if one was written,
  stays (it is encrypted; the next attempt writes another).
- **a leftover and no valid journal, or a leftover beside a journal in the phase `building`** (the migration never produces this; a
  corrupt journal, a hand-interrupted restore or a restored set does): the live pair decides.
  (a) the live pair (ignoring the leftovers) is **Sealed** by `VaultStoreProbe.IsSealedPair` (7.6: `DetectMode`'s Sealed branch, which
  AppPaths cannot call, plain-companion check included, 2.5) - a boot store of ours beside an encrypted, page-aligned database with no plain `chat.db`, WAL or journal beside it: the swap had passed its renames (it only starts after the verification of 10.5), so every leftover is a
  plain file that was replaced: **wipe each of them** (`SecureFileWipe`, below), flush the directory. A wipe that fails rebuilds a
  journal in the phase `swapping` that lists the leftovers, so the retry of the forward path below applies: the node starts sealed
  and closed with `migration.wipe_pending`. The renamed `.unreadable` journal is deleted when no `*.seal-*` file is left.
  (b) the live pair is **not provably Sealed** (a live `beememorybank.db` that is absent or plain, a boot store that is missing or
  foreign): **the start is refused** with the named state `seal_leftovers`: the message lists the relative names, names the
  pre-encryption snapshot when `pre-encryption/` holds one, and says "restore the data directory from one backup set (8.7.2) or from
  the pre-encryption snapshot (10.7); do not delete any `*.seal-old*` file before you have checked which of them is your database".
  The resolver deletes, moves, renames and wipes nothing in this case: an automatic guess could destroy the only copy.
- journal `verified` or `swapping`: go forward, idempotently, one file at a time in the order `beememorybank.db`, `chat.db`,
  `beememorybank.boot.db`. For a file F with `<F>.seal-new` present: (a) if `<F>` exists and `<F>.seal-old` exists too (a leftover
  of an earlier attempt), wipe `<F>.seal-old` first; (b) if `<F>` exists, rename it to `<F>.seal-old` and its `-wal`/`-shm` to
  `<F>.seal-old-wal`/`<F>.seal-old-shm`; (c) rename `<F>.seal-new` to `<F>`. (The boot store has no old plain file; its rename is
  last, and its presence is what makes the node sealed, 2.5.) A crash between (b) and (c) leaves `<F>` absent with `.seal-new` and
  `.seal-old` present: the next pass skips (b) and does (c). When all three renames are done: **wipe** every `*.seal-old*` file
  (`SecureFileWipe.TryWipe`, below), flush the directory, delete the journal. If a wipe fails (file locked, read-only volume) the
  journal stays at `swapping` and the node starts sealed and closed as usual; the wipe is retried at every start and reported in
  `GET /api/vault/status` as `migration.wipe_pending` (relative names, no content).

`SecureFileWipe.TryWipe(path, afterOverwrite, sharedWithAnotherName)` (A.6, verbatim): refuses to write through a symbolic link or
reparse point (it deletes the link only); **refuses to overwrite a file that has another name**: when the caller's
`sharedWithAnotherName(path)` answers true (a link count above one, or the same identity as a live file) the file is not overwritten,
only this name is removed and the call returns `true`, the bytes living on under the other name, which is the caller's to report
(the resolver puts the relative name into `migration.linked_plain`; the erase of 10.2 never gets this far, it refuses such a path
first); opens the file for writing with write-through, allowing readers but no other writer; overwrites its whole
length with random bytes in 64 KiB blocks; flushes to disk (`Flush(true)`); calls `afterOverwrite(path)` (a test seam: the content
is already random there and a test may read it with `FileShare.ReadWrite`);
truncates to zero; flushes; deletes; returns `false` instead of throwing on an I/O or access failure. It is best effort: SSDs with
wear levelling, copy-on-write and journaling file systems may keep earlier blocks; that is the documented remnant of 2.1. Used by
the resolver, by the erase of 10.2, by `SnapshotService` for the staging copy of the pre-encryption snapshot, and by the failed-target
cleanup of 10.4 step 7.

Each rename uses the retrying move of the re-key (`RekeySwap`), with the directory flushed after each step. Kill-point tests
15.3.7 and 15.3.9: a process killed at any step starts either plain with the old files or sealed with the new ones, never neither,
the data equals the source, and **after the next start** no file of the data directory (listing and raw bytes) holds a canary of
the old plain database.

### 10.7 No plaintext copy survives the switch; the way back

The requirements "a stolen disk must be useless" and "rollback = restore the pre-cutover backup" are both kept by
making the way back an **encrypted** file:

- **Nothing plain remains.** The two plain files the migration replaced are wiped and deleted in the same pass that swaps them
  (10.6); every other plain copy in the data directory blocks the migration until it is erased or moved out by the owner
  (10.2 `plain_copies`); the final scan of 10.4 step 11 and the canary scan of 15.3.9 prove it on a real data directory. The
  boot store and the status carry no path, size or name of any plain file, because there is none.
- **The way back is the pre-encryption snapshot** (10.4 step 3): `<data>/pre-encryption/<file>`, a normal snapshot (BMBDB2 under the
  master DEK, so a stolen disk does not open it any more than it opens the sealed database) plus the key envelope below, not pruned,
  not listed, deleted by the product only when the owner asks (`DELETE /api/vault/pre-encryption`: internal key, superadmin, not an
  agent, password re-entry, audit entry `pre_encryption_deleted`; it removes the snapshot and its `.sig`, nothing else). The
  encryption page and the Admin page (signed in; the unauthenticated status of 6.7 never names it) show its name, size and date after
  the migration and the sentence "The way back is this encrypted snapshot of the node as it was before the encryption. It opens with
  the master password or recovery key you had on <date>, even if you have changed them since. Keep it until you are sure the node
  works, then delete it." A password change or a key rotation while the snapshot exists adds "The way-back snapshot still opens with
  your old password. Delete it if you no longer need it." to its confirmation.
- **The key envelope** (`key-envelope.json`, an extra entry of the snapshot's tar.gz, listed with its SHA-256 in the manifest like
  every other entry): `{ "v": 1, "created_at": <UTC>, "sentinel": <base64 of tbl_node_identity.sentinel_value>, "slots": [ { "type":
  "user" | "recovery" | "password", "wrapped": <base64>, "iv": <base64>, "salt": <base64>, "argon_memory": n, "argon_iterations": n,
  "argon_parallelism": n } ] }`. The slots are copies of the `tbl_key_slot` rows a password or the recovery key opens and that the
  unlock itself accepts (4.4 step 6): `user` slots of active superadmins, `recovery`, the legacy `password` slot - **never**
  `os_auto_unlock`, `server_auto_unlock` or an agent's wrapped DEK, and no user id, user name or slot id. They are the same
  Argon2id-wrapped bytes the boot store keeps as doors, so the envelope exposes nothing a stolen disk of the sealed node does not
  already show; it does not make the snapshot weaker than the node, and it is not stronger either. Why it exists: BMBDB2 is encrypted
  under the master DEK, `SessionSnapshotKeyOperations` opens it only with the session's *current* DEK, and a DEK rotation (every
  rotation makes a new DEK) or a node reset leaves no way to the old one; the envelope carries the old DEK, wrapped
  under the password and recovery key of the snapshot's day. It therefore survives any later password change, DEK rotation, restart or
  reset, and dies only when the owner deletes the snapshot or loses both the old password and the old recovery key.
  The restore uses it (an edit of `SessionSnapshotKeyOperations.cs` and `SnapshotService.Restore.cs`): when the
  session's key does not open the BMBDB2 database (`CryptographicException`) and the archive holds `key-envelope.json` and the restore
  request carries a master password, each envelope slot is tried under the same Argon2id bounds as `FindStagedMasterDek`
  (`SessionSnapshotKeyOperations.cs:141-145, 185-186`); the DEK that passes the sentinel check decrypts the database, is cleared in a `finally`
  and is never logged; no slot opens it -> the restore is refused before anything is replaced, as today for a foreign snapshot.
- **Rollback procedure** (executable; documented in `docs/sealed-database.md`, tested end to end by 15.3.13 and 15.4.3): to make this
  node plain again:
  1. Note the snapshot's file name (the encryption page, `bmb vault status`) and the date; you may copy the file anywhere, it is
     encrypted and carries its envelope.
  2. `bmb init reset` on the sealed node (13.1: it ends plain and empty; `snapshots/` and `pre-encryption/` stay).
  3. Set the node up again (the Web setup or `bmb init`) **with the master password you had on the day of the snapshot** (or, if that
     is lost, the text of the recovery key you had that day): a new, empty, plain vault. (The restore route asks the password once and
     uses it twice, to prove you may restore into this vault and to open the envelope, so the two must be the same.)
  4. `bmb snapshot restore-standalone <file name>` (the name is looked up in `snapshots/` and then in `pre-encryption/`) with that
     password: the new vault's own key does not open the snapshot, the envelope does; the snapshot's database replaces the empty vault,
     the node becomes a **new node** with a new id and key pair (`docs/snapshot-restore.md`), keeps the activation state the snapshot
     had, and locks. Sign in with the same password.
  5. Join the other devices to this node again; pair the blind copies again. If you want the node sealed again, press "Encrypt this node".
  What is lost: changes made on this node after the snapshot that no peer received (everything a peer received comes back by the
  join), and the chat history (`chat.db` is in no snapshot, today as before). Going back further than the activation (to a 2.5.x
  binary) needs the offline pre-activation backup the owner took when they pressed "activate" (step-a-spec 5.4): that is 14.5.
- A plain unencrypted export of a sealed node does not exist in 2.6.0 (open point 7).

### 10.8 Phones

The Android and iPhone apps run the same service in process, from a foreground page with a progress bar that keeps the screen
on; a phone killed mid-way is resolved at the next start (10.6). Steps 1 and 9 there: no maintenance middleware; the pages
are blocked by the progress page. A phone has no snapshot service (`SnapshotService` is Api-only), so step 3 is replaced by the
preflight blocker `phone_no_copy`: the migration starts only if some full peer has reported a position at or above this phone's
log head (it holds nothing a peer has not got), or the owner ticks "This phone holds the only copy of these notes; encrypting
cannot be undone". The way back on a phone is to remove the vault and join again from a peer (a join always produces a plain
vault in 2.6.0, 14.3), after which the owner may encrypt again. The wipe of 10.6 applies to the phone's data directory; flash
storage keeps the remnant of 2.1.

### 10.9 Progress and failure

`GET /api/vault/seal/progress`: `{ phase, percent, step, error_code, error }`; `VACUUM INTO` gives no progress, so percent moves
per step. Failure codes name the check (`snapshot`, `plain_copies`, `verify_integrity`, `verify_counts`, `verify_sentinel`,
`verify_bodies`, `verify_tree`, `verify_scan`, `verify_boot`, `wipe`, `io`, `cancelled`).

## 11. Auto-unlock

The feature is split by **what the node is**: a desktop keeps its existing opt-in with a warning; a server gets
a new checkbox with a double warning. The split is by host role (11.0), never by operating system: a Windows or macOS machine that
runs the node as a server (a service, a headless `bmbd`, a container) gets the server checkbox exactly like a Linux or Docker host.

### 11.0 Host role

`HostRole` (`Desktop` or `Server`; `libs/BeeMemoryBank.Infrastructure/AutoUnlock/HostRole.cs`) is read once at start from
the environment variable `BMB_HOST_ROLE`: the value `desktop` (ordinal, case-insensitive) is `Desktop`; **anything else or nothing is
`Server`** (when in doubt the stricter warnings apply). Only the desktop applications set it, in one place: `NodeLifecycleService`
sets `BMB_HOST_ROLE=desktop` on the child's `ProcessStartInfo`, next to `BMB_STDIN_LIFELINE` and `BMB_INTERNAL_KEY`
(`desktop/BeeMemoryBank.Desktop/Services/NodeLifecycleService.cs:252-266`; that one line is the only change to the file). Nothing else
sets it: not `.env.example`, not a compose file, not the Dockerfile, not an installer, not a setting in the database, not an import.
The phones have no role (their custody is section 12). `GET /api/keys/auto-unlock/status` and `GET /api/vault/status` report
`role`. The Web shows by role: **Desktop** - the OS auto-unlock card of 11.1 and no server checkbox; **Server** - the
server checkbox of 11.2 and no OS card for enabling (11.2, "OS auto-unlock on a server").

### 11.1 Desktop role: OS auto-unlock

The existing opt-in (`OsAutoUnlockService`, Windows DPAPI and macOS Keychain, `libs/BeeMemoryBank.Infrastructure/Secrets/UserSecretStores.cs:14-16`;
Admin card `server/BeeMemoryBank.Web/Pages/Shared/Admin/_AdminSecurity.cshtml:170-220`) stays, off by default. Its slot is a door (4.1); at start
the service unlocks through `TryUnlockWithDekAsync` before anything else (5.2 step 6). The server routes of 11.2 answer 409
`role_desktop` on this role. On a sealed node the card adds:

> Your notes are encrypted on this computer's disk, but with auto-unlock on, this computer opens them at start without your
> password. If someone gets this computer or its disk, their protection rests only on your {Windows|macOS} account password
> (and on disk encryption, if you use it).

The confirmation dialog (`:212`) gets the same sentence. Off stays the default; nothing turns it on.

### 11.2 Server role: "Start unlocked after a restart"

Everything in this section applies to a node whose role is `Server` (11.0), on every operating system, **plain or sealed**. Today a
restarted server stays locked until the master password is entered, which is the behaviour of every plain server; so a plain server
gets the same checkbox with the same warnings, and a sealed server gets it too. Nothing else about a plain node changes: with the checkbox off, which is the state of every node after every upgrade, no part of
this section runs on any node (no key file is read, no auto-unlock code path is entered, no boot store is created by it; a plain node
never has one), and a plain server that never uses the checkbox starts exactly as 2.5 does (red test 15.2.8).

- **Where shown:** the encryption page (`/VaultEncryption`), the Admin security card (in the place of the OS card) and
  `bmb vault auto-unlock status|enable|disable`.
- **Three parts, all required, and they must agree before anything is read or unlocked:**
  1. *The slot* - a key slot of type `server_auto_unlock`: the master DEK wrapped (`MasterKeyManager.WrapMasterDek`) under a 32-byte
     random secret used directly as the KEK, `salt` and Argon2 columns NULL (so the password path never tries it, as the OS slot,
     `OsAutoUnlockService.cs:23-31`), at most one (a code-created partial unique index, as `KeySlotRepository.cs:74-80`). It is **data**:
     it lives in `tbl_key_slot` and travels with every copy of the database. On a sealed node its door copy (4.1) is in the boot store;
     on a plain node there is no door copy and no boot store, the slot is read from the plain database. It is **per server**:
     `tbl_key_slot` is not replicated (`SnapshotTables.cs:27-37` lists the replicated tables; key slots are in
     `StrippedByDropping`), so no event, join or sync carries the slot or the choice from one node to another.
  2. *The secret* - 64 hex characters in a **file** on every operating system, never in DPAPI or the Keychain, which would bind it
     to an account and are the desktop's feature: `BMB_AUTO_UNLOCK_KEY_FILE` when set (an operator-provided path, for example a
     Docker secret or a mount outside the data volume; the node reads it and never writes, moves or deletes it), otherwise
     `<data>/server-auto-unlock.key` written through `OwnerOnlyFile.MoveNoReplace` and `OwnerOnlyFile.CreateNew`
     (`libs/BeeMemoryBank.Core/IO/OwnerOnlyFile.cs`: an ACL for the current user only on Windows, mode 0600 elsewhere; the node
     refuses to read a key file that is not owner-only and says so).
  3. *The enable record* - `<data>/server-auto-unlock.enabled`, ASCII JSON `{ "v": 1, "slot_sha256": <the slot's content hash, the
     formula of 4.2>, "dek_fp": <DekFingerprint of the DEK the slot wraps>, "enabled_at": <UTC> }`, written through `OwnerOnlyFile` by
     the enable call as its **last** step. It holds no secret. It is **local state of this server, not data**: no snapshot, baseline,
     package, join archive or product-made backup ever contains it, and the backup contract excludes it together with the key file
     (8.5, 8.7.1). It is what says "the owner enabled exactly this slot on this server".
  Boot meta `server_auto_unlock = 1` is only a mirror of the effective state for the status and is rewritten at every start from the
  three parts. No slot, no auto-unlock, whatever files or variables exist; a slot without a matching record is never used.
- **Off by default, and off means exactly today's behaviour:** after every restart the server stays locked until the owner signs in
  with the master password or a superadmin agent unlocks it (4.6). With the checkbox off the node reads no key file, runs no
  auto-unlock code path, and the status says `server: false`.
- **Turning it on** (`POST /api/keys/server-auto-unlock/enable`, internal key, superadmin, not an agent, session unlocked) takes two
  steps in the Web and in the CLI (`bmb vault auto-unlock enable` prints both texts and reads the phrase; it never takes them as
  flags):
  1. a warning with "Cancel" as the default:
     > Strongly not recommended on a server. With this on, the key that opens this server's database is kept on the server
     > itself, so anyone who gets this server's disk, a copy of its data folder or a backup of it can read the whole vault: the
     > database encryption no longer protects it.
  2. a separate confirmation: the master password and the typed phrase `START UNLOCKED`:
     > Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then opens
     > everything.
  The API refuses (400 `confirmation_required`) unless the body is `{ "password": "<master password>", "confirmation": "START UNLOCKED" }`
  (exact, case-sensitive); a wrong password is 401 (rate-limited like re-authentication today); the desktop role gets 409
  `role_desktop`. An audit entry `server_auto_unlock_enabled` (no names) is written. The writes happen in this order, so that every
  prefix of them is inert (no record, no auto-unlock): the key file (node-created case), the slot, its door (sealed node), the enable
  record, the boot meta mirror.
- **Never on by anything but that call** (the list below is exhaustive; each line has a test, 15.2.8). The words
  "turned on" mean a new slot, a new record or a new key file coming into existence; **an in-place restore of an identical slot over
  the server that enabled it (item 3 (i)) creates none of the three and keeps in force a choice the owner already made**, so it is
  preservation, not enabling, and it is the only restore outcome that leaves a server armed. The enable record is created by the
  enable call alone: no import, upgrade, migration, default, configuration or restore path may create it, and no backup or package
  may contain it:
  1. *Upgrade and migration:* there is no `server_auto_unlock` slot before 2.6.0, and no migration SQL, startup task or
     "Encrypt this node" creates one. The encryption preflight shows a non-blocking warning `auto_unlock_on` when the owner had it on
     already ("encryption then does not protect against a stolen disk"), and copies nothing else.
  2. *Every import of key slots* (snapshot restore in the three modes of `docs/snapshot-restore.md`, restore from a blind copy or an
     Android backup, join and for-join archives, `SnapshotJoin`, baselines, blind packages, the offline re-key): a `server_auto_unlock`
     row arriving in an imported `tbl_key_slot` is **deleted in the staging database before it replaces the live one** (the edits
     of `SnapshotService.Restore.cs`, `NetworkRestore.cs`, `RecoveryRestoreService.cs`, `AndroidBackupRestoreSource.cs`;
     `KeyMaterialStep` already keeps only the owner's slot), and the boot store retires its door at the rewrite. A restore of a backup
     made while the checkbox was on therefore leaves it off - always, whatever the record or the key file say, because the slot is gone.
     (Even if a path were missed, the slot would be judged by item 3 like any slot that arrives with data.)
  3. *File-level restore and copy* (a data directory put back from a restic, rsync or volume backup, the copy-back of a pre-update
     set, a data directory copied to another machine). The files carry the **slot** but never the **enable record** (the contract of
     8.7.1 excludes `server-auto-unlock.*`, and the product's own backups never copy them, 8.5). The rule, with its one exception,
     is: **a restore starts the server locked, unless the local record of this very server still matches the restored slot.**
     (i) An **in-place restore** over the directory of the very server on which the owner enabled it keeps the record and the key
     file where they are (the restore replaces the database files, not the local files); if the restored slot is byte-identical to
     the one the record names (nothing about it changed since the backup) the three parts agree and the server stays armed as the
     owner left it. That is the existing choice staying in force, not a new enable: the restore created no slot, no record and no key
     file. (ii) **Every other restore** - a fresh restore into an emptied or new directory, a restore on another machine, a copy, and
     any in-place restore whose slot differs from the one the record names (it was enabled, disabled, rotated away or replaced since
     the backup) - finds no record that matches, so at the first start the slot is a **stale slot** - see "At start" - and nothing
     unlocks, whether the operator's key file (`BMB_AUTO_UNLOCK_KEY_FILE`) is still mounted or not and whether the node-created key
     file is present or not.
     In every case restoring never creates the record: only the enable call does. (iii) A backup that was taken without the
     excludes carries the node-created key file and the record too: it is a plain copy by 8.7.1, and its restore is equivalent to the
     stolen disk the double warning describes - the guide says so and the Linux/Windows recipes carry the excludes. The same
     qualification applies, word for word, wherever this behaviour is stated: 8.7.2 step 5, 13.4, 14.5, ADR 0010 decision 8 and the
     operator guide (`docs/sealed-database.md`, `docs/deployment.md`).
  4. *Defaults and configuration:* no environment variable, `appsettings` key, command-line flag, compose key, installer option or
     settings import enables it; `BMB_AUTO_UNLOCK_KEY_FILE` only names where the secret lives once the owner has enabled the
     checkbox (set without a slot, the node logs "key file configured, auto-unlock is off" and does nothing); a key file that appears
     by itself (copied in, restored) without the slot and the matching enable record is inert.
  5. *Agents and remote routes:* the routes are `RequireSuperadmin().RequireNonAgent()`; an MCP agent and a `bmbrt_` token cannot call them.
  6. *DEK rotation:* the initiator deletes every other slot already (`DekRewrapper.cs:159-165`), and the peer auto-accept list
     `('recovery', 'os_auto_unlock')` (`:179-180`) gains `'server_auto_unlock'`; the status then shows it off, the enable record
     and the key file (if the node created it) are removed at once by the rotation hook of 3.5, and the owner turns it on again if
     they want. A record that outlives its slot is removed at the next start in any case (the stale rule below).
  7. *Reset:* the node reset wipes the slot with the other slots and removes `<data>/server-auto-unlock.enabled` and
     `<data>/server-auto-unlock.key` (the latter if the node created it).
  A golden `docs/design/key-slot-writers.golden.txt` lists every code site that inserts into `tbl_key_slot`; a test greps
  the sources and fails when a site is added that the golden does not name, so a new writer has to be checked against this list.
- **At start** (pre-open, 5.2 step 6; on a plain node at the place of today's OS auto-unlock, with the plain database open and no
  session yet; before any key file is read and before any unlock is tried), `ServerAutoUnlockService.EvaluateAsync` looks at
  the three parts and answers one of four states:
  1. *off* - no slot, no record (every node that never used the checkbox): nothing is read, nothing is written, the node starts
     locked exactly as in 2.5; the call costs one read of the slot table (plain node) or of the boot store (sealed node) and one
     `File.Exists`;
  2. *armed* - the slot (the boot door on a sealed node, `tbl_key_slot` on a plain one) exists, the record parses, its `slot_sha256`
     and `dek_fp` equal the slot's, and the key file (the configured one, else `<data>/server-auto-unlock.key`) is readable,
     owner-only and holds 64 hex characters -> `TryUnlockWithDekAsync`. Any failure after that (the secret does not unwrap, the
     sentinel does not match) is a warning in the log without a key and without the path beyond its configured name; the node stays
     locked and the state stays armed (a transient problem must not erase the owner's choice);
  3. *stale slot* - a slot exists and the record is missing, unreadable or does not match it: the slot came with restored or copied
     data, or a crash left a half-done enable. It is **never used**: nothing is unlocked and no key file is read. The state is
     repaired where that needs no key: the boot door is retired and boot meta `server_auto_unlock = 0` (sealed node), the slot row is
     deleted at once on a plain node and by the open step `StaleServerSlot` (order 50, every open) on a sealed node, an audit entry
     `server_auto_unlock_retired_stale` (no names) is written, and a node-created key file is wiped and removed (an operator's file
     never is). The status shows `server: false` and a one-time notice "Start unlocked after a restart was switched off because this
     data came from a backup or a copy; turn it on again if you want it";
  4. *stale record* - a record exists and no slot, or a slot that does not match: the record is removed (it is the server's own
     file), the key file as in 3.
- **Turning it off** (`POST /api/keys/server-auto-unlock/disable`, same protections, password re-entry): remove the enable record
  first (every prefix of the sequence is inert), then retire the door, delete the slot, delete `<data>/server-auto-unlock.key` if the
  node created it (never an operator's file; the answer says to remove that one).
- **OS auto-unlock on a server:** `POST /api/keys/auto-unlock/enable` answers 409 `role_server` ("On a server use 'Start unlocked
  after a restart'"). An `os_auto_unlock` slot the owner created earlier keeps working (nothing is turned off by an upgrade either);
  the card shows it with the sentence of 11.1 in its strong form and a "Turn off" button.
- **Tests on every supported OS:** the server-role tests (enable, restart, disable, key-file permissions, the inventory of the list
  above) carry no platform dependency except the permission assertion (a Windows ACL check or a Unix mode check, each skipped on the
  other family); they run on Windows, on Linux in the SDK image (15.5), and on a Mac (15.5).

## 12. Phones: key custody

### 12.1 Android full app

Unchanged custody: the master password is typed, or stored encrypted under an Android Keystore AES key that requires the
fingerprint (`mobile/BeeMemoryBank.Mobile/Platforms/Android/BiometricService.cs:28-160`), and `UnlockPage` calls
`SessionService.UnlockAsync(password)` (`Pages/UnlockPage.xaml.cs:61, 112`). On a sealed phone that unlock goes through the
boot doors (4.4). Neither the DBK nor a page key is ever put in the Keystore (only wrapped under the master-key
chain). The app locks, and the database closes, on every `OnSleep` (5.4). OS backups exclude the app data
(`AndroidManifest.xml:13-15`), boot store included. "Encrypt this phone's database" on the Security page (10.8).

### 12.2 iPhone full app

Quick unlock (`mobile/BeeMemoryBank.FullIos/Services/QuickUnlock.cs`): a 256-bit unlock key in the Keychain behind Face ID or Touch
ID, this device only, wraps the master DEK; nothing about the DBK changes. On a sealed iPhone `UnlockAsync` uses
`TryUnlockWithDekAsync` (4.7) and checks the sentinel after the open (the order of `:105-114` reversed); a key that no longer
opens the vault turns quick unlock off as today. The data directory is excluded from iCloud backup
(`Platforms/iOS/IosContainer.cs:30, 40`). Encryption from the settings (10.8).

### 12.3 Blind copies

Unchanged: they hold no master DEK, their database holds no metadata after Step A; they are never sealed.

### 12.4 Acceptance boundary for the phones

The native part of the phone work needs a Mac, a phone or an emulator, so it is split in two on purpose:

- **Covered by automated tests that run anywhere:** `PhoneVaultLifecycle` and every rule of 10.8, 12.1 and 12.2 that does not need an OS
  (start order, open steps, background lock closing the database, unlock with password and with a fake Keychain, interrupted
  migration resolved at the next start, `phone_no_copy`, no DBK or page key handed to any key-store interface - a fake
  `IKeyStore` that throws if given 32 bytes of the DBK or a page key); the XAML and C# of the pages are written and compiled
  where the MAUI workload is installed (`dotnet build` of the mobile projects); static checks that run anywhere: the project files reference the SQLite3MC bundle on
  both platforms (the engine swap of 2.5.2), the Android manifest keeps `allowBackup=false` and the iOS data directory stays excluded from
  iCloud backup (`AndroidManifest.xml:13-15`, `IosContainer.cs:30, 40`), and the Keystore/Keychain wrappers are unchanged files.
- **Covered by a separate release check on a Mac and real or simulated devices before 2.6.0
  ships:** (1) build the Android app and the iPhone app from the release branch; (2) on an Android device or emulator: join,
  activate, encrypt the phone in the foreground, background the app (it locks and the database closes), unlock with the
  fingerprint, reopen, kill the app mid-encryption and start it again (resolved), restart with the OS backup excluded; (3) the same
  on an iPhone or simulator with quick unlock (Face ID/Touch ID or the simulator's enrolled biometric); (4) a canary scan of the app
  data folder pulled from the device or simulator; (5) battery and free-space blockers. Until that check is green the release notes
  do not offer "Encrypt this phone" (the feature stays hidden behind the lifecycle's `PhoneEncryptionAvailable` flag, which is set to
  true only after the check). The phone apps still ship their Step A parts (iPhones need 2.6.0 builds); only the phone's own
  encryption button waits for the check.

## 13. Other paths

### 13.1 Node reset

`NodeResetService.ResetAsync` (`libs/BeeMemoryBank.Vault/Core/Services/NodeResetService.cs:52-185`) wipes the tables in place and
`VACUUM`s. On a sealed node: the wipe runs keyed (the reset needs the master password; a closed database is opened with
`OpenTemporarilyAsync`), then the
gate closes, `beememorybank.boot.db` and the encrypted `beememorybank.db` and `chat.db` (now empty) are deleted and a new empty
plain database is created; the node is an uninitialized plain node, as after a reset today. The owner may encrypt it again after
setup. (Keeping it sealed across a reset would leave a database no door opens.) A reset leaves `snapshots/` and
`pre-encryption/` alone (today it touches the tables, `media/*.enc` and the hooks' files only), which is what makes the rollback of
10.7 possible: reset, set the node up again with the old password, restore the pre-encryption snapshot through its key envelope. It
removes `<data>/server-auto-unlock.enabled` and, when the node created it, `<data>/server-auto-unlock.key` (11.2). The reset is the
reason the snapshot carries the envelope: after it no key of the old vault is left on the node.

### 13.2 Offline re-key (`bmb rekey`)

`RekeyRunner` (`libs/BeeMemoryBank.Rekey/RekeyRunner.cs`) copies D to `D.rekey-new`, runs the steps, verifies, swaps whole
directories (`RekeySwap`). On a sealed D: the owner password opens the source through the boot doors
(`OpenTemporarilyAsync`); the copy is `VACUUM INTO` under a **new** DBK (generation + 1) page key (7.4), the steps and the scrub run
keyed, `ChatRekeyStep` keyed; the new vault gets a new boot store (`WriteNewBootStoreAsync` with the new DEK only); the D1 check
(`RekeyD1Check.cs`) also asserts that no door or wrapper of the old boot store opens the new vault. The parked old directory
keeps its own boot store.

### 13.3 Profiles and the desktop

`VaultFiles.Inspect` (7.6); moving or copying a profile copies the boot store; a sealed profile on a USB disk is opened on
another computer with its password like any sealed node.

### 13.4 Operators

- Docker: the boot store lives in the data volume next to the database (`docker-compose.yml:32-33`, `/app/data`); a volume backup
  must contain both, taken of a stopped container (8.7.1; `restart: unless-stopped` brings a stopped node back only when it is
  started again, so the recipe is `docker compose stop bmb`, back up, `docker compose start bmb`).
  `BMB_AUTO_UNLOCK_KEY_FILE` is documented in `.env.example` and the compose comments with the rule that the file lives
  outside the backed-up volume; a compose example mounts it read-only as a Docker secret. The same text says that a server restored
  from a backup into a fresh directory, onto another machine, or with a changed slot starts locked even when that file is mounted,
  because the enable record is never in a backup (11.2 item 3 (ii)); that the one exception is an in-place restore over the very
  server that enabled it with a byte-identical slot, which keeps the owner's earlier choice in force (11.2 item 3 (i)); and gives the
  `--exclude 'server-auto-unlock.*'` of every recipe.
- Reading the sealed database by hand: the page key is never shown by the product. Operators use snapshots (logical, under the
  master DEK); docs/sqlite-engine.md explains that DB Browser or the `sqlite3mc` shell cannot open a sealed file without the key.

### 13.5 Downgrade

A 2.5 binary on a sealed directory: the first statement fails (error 26) and the node does not start; nothing is written, and the
files stay byte-identical (tested, 15.4.2). Downgrade is never in place once a node is activated or sealed; the exact edges, the
backup each one uses and what happens to later writes are the table of 14.5.

## 14. Compatibility and the points where Step B changes Step A

### 14.1 Nodes that are not sealed

Every code path of section 5 to 13 is dormant on a plain node: `DetectMode` = Plain, the factories have no key source, the gate
reports Plain and opens nothing, no boot store exists, the startup is today's. A node that has not activated Step A cannot be
sealed (10.2). Red test 15.3.1 runs a plain node's existing test classes with the B composition registered.

There is exactly one thing a plain node gains in Step B besides the (inert) encryption page: the server checkbox of 11.2, for a
node whose role is Server. It is off on every node after every upgrade; with it off, `ServerAutoUnlockService.EvaluateAsync` finds no
slot and no record, reads no key file, writes nothing and leaves the start as it is in 2.5 (red test 15.2.8). The owner who turns it
on on a plain server gets a slot in `tbl_key_slot`, a key file and an enable record, and still no boot store. So the sentence "Step B
changes nothing on a node the owner has not encrypted" holds with this one, owner-started, off-by-default exception.

### 14.2 The mesh

Sealing is node-local: no event, no protocol change, no package or archive format change. A sealed node's archives and baselines
are plain SQLite images with 80 reserved bytes per page inside the existing containers, which 2.5 and 2.6 readers open with stock
SQLite. A closed sealed node relays sealed events and control events like a locked Step-A node, except: it refuses clear content
(deferred), it cannot serve rows older than its last open head (503 `vault_sealed_range`), and it applies nothing until unlock.

### 14.3 New vaults

`init`, `join`, a standalone restore and the v1 import create plain vaults in 2.6.0; the owner encrypts each node after activation
(the order is "upgrade every node, activate, then encrypt this node on each full node"). Making new vaults sealed at creation is a
later release (open point 19.1).

### 14.4 Changes to Step A behaviour on a sealed node

1. Step A 6.9 "a locked node serves held rows": a closed sealed node serves its inbox (6.1), not `tbl_event`.
2. Step A 6.4 rule 1 "control events apply at receipt, locked or not": on a closed sealed node they are stored in the inbox and
   applied at open in order (6.6); revocations take effect at receipt through the mirror suspension (6.4).
3. Step A's protocol policy must not be the first to read the database on a sealed node: B calls `MarkActivated()` pre-open (5.2).
4. Step A's baseline producer, its self-check and the encrypted join producer use keyed staging and the page codec on a sealed node
   (8.3); they run only while open (they already require an unlocked node).
5. Step A's restore (12) imports through `PlainImageImporter` on a sealed node (8.4).
6. Step A's drain starts after the inbox import (4.4 step 8).
7. **The metadata key rotates with every DEK rotation, as a part of the rotation (a change in Step A's code, required by
   Step B 3.5).** Required: (a) on the initiator, when `SealsMetadata`, the next `tbl_metadata_key` row (generation = max + 1,
   wrapped under the **new** DEK) is inserted in the **same transaction** that swaps the DEK (`DekRewrapper` /
   `RewrapDestructiveCoreAsync`), together with a durable marker `tbl_migration_marker('mek_publish_pending', '<key_id>')`; (b) after the
   commit, `metadata_key_set` for that row is appended and the marker deleted in one transaction; (c) a failure is not caught as
   non-fatal: the rotation's progress stays `Completing`, `dek_rotation_completed` and the success answer wait for (b), and a retry
   (every 60 s and at every unlock) repeats (b); (d) while the marker is set, this node's sealed authoring is refused with a
   deferrable error (`metadata_key_publish_pending`, HTTP 503 with `Retry-After`) - it must not seal with the old generation, which
   a node the rotation left out can still open; (e) a crash between the commit and (b) resumes at (b) after the next unlock. Test:
   a publisher that throws once after the DEK commit leaves the marker and refuses
   authoring, the retry publishes, and a node holding only the old DEK cannot open any event authored after the rotation reported
   complete; and an auto-accepting peer, on the same rotation, creates neither a `tbl_metadata_key` row nor the pending marker (the
   initiator alone publishes). The end-to-end test is 15.4.4.
   The Step B edits of `DekRewrapper.cs` (3.5, 17.2) build on this change.

### 14.5 States, edges, downgrade and mixed networks

A full node is in one of four states; this table is the contract for every move between them and for every pairing in a mixed
mesh. It extends step-a-spec 2, 5.6 and 5.8 and does not change them.

| State | Meaning |
|---|---|
| **N0** | a 2.5.x binary (no Step A, plain database) |
| **N1** | 2.6.0, Step A not activated, plain: on the wire exactly 2.5.1 (step-a-spec 2) |
| **N2** | 2.6.0, activated, plain database |
| **N3** | 2.6.0, activated, sealed (open or closed, section 5) |

Two backups are named, so that "restore the pre-cutover backup" always points at something that exists:
the **pre-activation backup** = the owner's own offline plaintext backup that the activation dialog tells them to take
(step-a-spec 5.4), plus the automatic pre-update snapshot of the 2.6.0 update (8.5); the **pre-encryption snapshot** = 10.4 step 3.

| Edge | Who and how | Data and log positions | Way back |
|---|---|---|---|
| N0 -> N1 | the update to 2.6.0; the pre-update backup (snapshot unlocked, cold copy locked, 8.5) is taken first | migrations 037-040 only add tables, columns and triggers; no row is rewritten; no event, cursor or position changes; nothing is activated or sealed | **in place is supported while N1**: the 2.5 binary opens the same database; the ghost rule of `MigrationRunner` (`MigrationRunner.cs:37-65`) drops the ledger rows 037-040 and ignores their objects, and a later re-update re-applies them idempotently; or restore the pre-update backup |
| N1 -> N2 | the owner, once, `POST /api/sync/activation` after readiness (step-a-spec 5.3/5.4): every active peer must be `ready` (capability seen within 14 days); a `not_capable`, `stale` or `unknown` peer is a blocker, a revoked row is not | no row is rewritten; from then on the node writes protocol 4; `metadata_key_set` and `metadata_activation` are appended to the log (positions only rise) | **never in place** (step-a-spec 5.8): restore the pre-activation backup on every node (network restore from the pre-activation snapshot, or a standalone restore plus re-join); changes made after that backup are lost mesh-wide, and the activation dialog and the guide say so |
| N2 -> N3 | the owner, per node, "Encrypt this node" (section 10) | the copy is `VACUUM INTO` of the whole database: every row, `max(sequence_num)` and `sqlite_sequence` of `tbl_event` are verified equal (10.5); `seq_floor` only rises (6.3) | 10.7: reset, set the node up again with the old password, restore the pre-encryption snapshot through its key envelope (it opens whatever happened to the password or the DEK since); the node is a new node and its devices join it again; lost: unsynced changes of this node and the chat history |
| N3 under a 2.5.x binary, or under any binary without Step B | an operator starts an old binary on a sealed data directory | the first statement fails with SQLite error 26 and the process stops; **no file is written or changed** (the three files are byte-identical before and after); a 2.5 desktop app does not see a vault in the folder (no SQLite magic: `VaultFiles.Inspect` answers `Other`, `libs/BeeMemoryBank.AppPaths/VaultFiles.cs:66-77`) and refuses to create one there ("This folder is not empty", `CreateStorageDialog.axaml.cs:79-92`) | not in place: 10.7, or restore an offline pre-activation backup into an **empty** folder with the old binary |
| N3 closed and restarted | a restart without unlock | received events wait in the inbox (6.2), applied at unlock in order; nothing a peer sent is dropped | unlock (4.4) |
| N3 put back from a file backup or a pre-update set | an operator copies a quiesced set back (8.5, 8.7.2) | the node returns with its node id and log head as of the backup moment; no position is lowered by Step B, but the mesh may hold newer rows from this node (the rule (1) below applies); server auto-unlock follows 11.2 item 3: a fresh, other-machine or changed-slot restore starts locked (stale slot); an in-place restore over the same server with a byte-identical slot keeps the owner's earlier choice in force, which is preservation and not a new enable | the next backup; or a network restore / join from a peer for what the backup lacks |

Pairings in a mixed mesh (what A does when B is in the state in the row; "refused" always happens before any write on either side):

| A talks to B | Result | Local data of the refused side |
|---|---|---|
| N0 with N1 | as 2.5.1: protocol 3 both ways | unchanged |
| N0 with N2 or N3 | the N0 node gets 426 at authenticate and never pulls from a node declaring 4; the activated node never pulls from N0 either (step-a-spec 5.6) | **untouched**: its database, log and positions are not read, written, truncated or overwritten by anyone; its own events stay in its log. Ways out: update it to 2.6.0 (it becomes N1, pulls the activation through the compatibility window, becomes N2, and its events authored in clear before that are applied as stragglers, step-a-spec 5.7), or, if it cannot be updated, join it again as a new device (the join archive is refused with 426 until it runs 2.6; the old copy is the owner's to export or discard) |
| N1 with N2 or N3 | accepted: declared 3 with the capability (step-a-spec 5.6); N1 pulls the activation and becomes N2 | unchanged |
| N2 with N3 (open) | identical on the wire | - |
| any node with N3 (closed) | sealed and control events are stored in the inbox and relayed; a clear-content event is answered deferred, the pusher's cursor stops there and it retries after unlock; `GET events` below the last open head answers 503 `vault_sealed_range`, the puller's cursor stays | no cursor moves backwards, no position is reused, nothing is overwritten (6.2, 6.3) |
| a node offline across the activation | it was `ready` within 14 days (else the activation was blocked, step-a-spec 5.3), so on return it authenticates through the window, pulls the activation and becomes N2; a node revoked meanwhile is refused as any revoked node | unchanged until it pulls |
| a full node with a blind node | step-a-spec 5.5, 5.7, 9: the blind host is reseeded in format 2; a sealed node never pushes clear content to it | unchanged |

Rules that hold for every edge (MUST): (1) no Step B code path lowers, reuses or truncates a log position (`seq_floor`, the
migration copy and the inbox only raise or preserve them); the restore routes keep their documented meanings (`docs/snapshot-restore.md`:
standalone restore = a new node with an empty log; network restore = `restore_network` with the replay shield; `keep-identity` or a
file-level restore of a data folder = coherent only when no peer has pulled newer rows from that node, which the guide states in
words before the command); (2) a refusal (426, 503 `vault_sealed`, 503 `vault_sealed_range`, error 26 at start) happens before
anything is written; (3) downgrade after activation is never in place and always names the backup it uses; (4) what was written
after that backup is lost on the node unless a peer received it, in which case a join or a network restore brings it back; nothing
merges silently.

## 15. Red tests

Every scan uses Step A's `MetadataCanaries` (step-a-spec Appendix B, copied byte for byte into each test project that scans) with
the canary vault of step-a-spec 16.1. Each scan test first proves it finds the canaries on a plain node (red before sealing).

### 15.1 Part 1

1. Keyed factory: a sealed test factory writes, reopens pooled, and its file, `-wal` and `-shm` hold no canary and no SQLite
   magic; a wrong page key fails at open with `VaultKeyMismatchException`; a closed key source makes `CreateConnection` throw
   `VaultSealedException` and opens no file handle (checked with a file lock probe).
2. `temp_store` is 2 on keyed connections and 1 on plain ones; a large keyed sort leaves no file in the temp directory.
3. Key hygiene, in the three states of the one contract (3.3, 7.1, 7.2), with a canary key whose 64 hex characters and raw bytes are
   searched for. (a) *At every boundary, in every state:* `DatabaseId`, the `DataSource` the factory builds, `ToString()` of the factory
   and of its key source, every exception the factory throws (wrong key, closed, mismatch), every log line of an open/close/failure
   cycle (captured logger), `GET /api/vault/status`, the SQLite error text of a failed open and every cache key of ours (the
   `FolderAccessService` cache among them) contain no key. (b) *Open generation, the allowed case:* a reflective walk (visited set, all
   fields, arrays, closures, dictionaries, task state) from the vault factory, the chat factory, the key source and the gate, plus the
   provider's pool registry (`SqliteConnectionFactory.Instance` reached by reflection; if the provider's internals change the test
   fails loudly and is adapted, it is never skipped), finds a string or byte array that contains the key in exactly the places of
   the 7.2 table - the factory's one private field, the provider's pool, a live connection - and nowhere else. (c) *Closed
   generation:* after `CloseForSealAsync` and after the gate's `CloseAsync("lock")` the same walk finds the key nowhere (connections
   disposed, pool cleared, field null, the file can be renamed on Windows); after a second open (a different key) it finds the old
   key nowhere and the new one only in the places of (b); a connection held past the 30 s limit is the one allowed straggler and is the
   only holder until it is disposed. (d) *Source test:* no file in the solution references `SqlCipherScheme.Password` or
   `VacuumIntoTarget` outside the allow-list `docs/design/key-string-sites.golden.txt`.
4. No plain fallback: a keyed factory over a plain file, and a plain factory over a sealed file, both fail (never silently open).
5. Page codec: a SQLite3MC-written keyed file -> `PlainImageReader` -> image opens in stock SQLite (`Storage.Tests` has the stock
   engine since 2.5.2, `[E] tests/BeeMemoryBank.Storage.Tests/Engine/StockSqlite.cs`), `integrity_check` ok, rows and FTS equal; one
   flipped byte in any page -> `InvalidDataException`; `SealedFileWriter` output opens in SQLite3MC; 64 MiB round trip with bounded
   memory.
6. Importer: reserve-80 image streamed; reserve-0 image under the cap via deserialize; over the cap refused; no file other than the
   keyed target appears in `tmp/` at any time (watcher on the folder).
7. Snapshots on a sealed node: the staging folder during creation (a hook between steps) and the BMBDB2 file contain no canary in
   clear; the BMBDB2 decrypts (on a plain test node) to a valid image; a full snapshot restore onto the sealed node round-trips the
   canary vault; `CreateAsync(encryptDb: false)` refuses.
8. Baseline and join archive produced on a sealed node: no plain file in `tmp/` during production; the container decrypts to a valid
   image a 2.6 plain reader imports (phase 2).
9. Every consumer of 8.4 on a sealed node (phase 2): restore from a format-2 package and from an Android backup, network restore,
   with the canary vault; `tmp/` never holds a plain file.
10. Pre-update backup of a locked sealed node, through the real `UpdateService.TakePreUpdateBackupAsync` (8.5, not an operator copy):
    a pusher test double sends events from a second task while the call runs - the call holds the gate closed (5.10) and pauses the
    relay writes (6.8), a push that arrives during the copy is answered 503 `vault_sealed_busy` and is stored, once, when the double
    retries after the release; an unlock attempt during the copy is answered 503 `vault_busy` and works after it; at the end every
    copied file equals its source file for file (length and SHA-256, re-read), the boot copy has no `-wal`, a sealed `-wal` is copied
    only when one existed, no `VACUUM INTO` is used (a call-recording seam), and `server-auto-unlock.key`,
    `server-auto-unlock.enabled` and `os-auto-unlock.dat` are never copied; a canary scan of the folder is clean; copying the files back
    into an empty data directory starts, unlocks, and `bmb vault verify` says `ok`; a node that was unlocked in the meantime takes a
    snapshot instead; a difference injected between copy and verification ends with `backup_inconsistent`, no copy left, and the update
    does not go on.
11. `VaultFiles.Inspect` says Vault for a sealed folder and Other for a folder with a foreign boot store; `VaultCopier` copies and
    verifies the boot store; `RekeyPreflight` finds an encrypted copy of the vault and reports a non-openable one.
12. `bmb rekey` on a sealed vault: the new vault opens with the new password only; no door or wrapper of the old boot store opens it;
    canary scan of the new directory.
13. `SecureFileWipe` (A.6): `afterOverwrite` runs with the file at its original length and no canary in its bytes; the file is gone
    afterwards; a symbolic link is removed without writing through it (the target keeps its content); a locked file returns `false`
    and does not throw; an absent file returns `true`; when `sharedWithAnotherName` answers true the file is **not** overwritten and
    only this name is removed - with a real hard link (`CreateHardLinkW` on Windows, `link(2)` on Unix) the other name keeps every
    byte, and with the callback answering false the same two names are overwritten together (the control).
14. `PlainVaultCopyScanner` (10.2), content-based and skipping nothing. Every case below is planted **with no canary and with a table
    set that matches nothing the product has** (so no schema, name or canary heuristic can pass it), and is red on a scanner that
    looks only at a member's name or only at the first entry. In `Preflight` mode it finds, one case per file: a plain SQLite image in
    `updates/pre-update-x/`, in `tmp/`, a `*.standalone-staging` file, a chat-only database, a copy named `x.jpg`, and one in **each**
    of `media/`, `search-index-segments/`, `snapshots/`, `baselines/`, `baselines/current/` and `pre-encryption/`; a stray `-wal` and
    a `-journal` made by a **real plain connection** (frames holding b-tree pages), and a plain WAL without any page-1 frame (written
    after a checkpoint by an in-place update); a `.gz` of a bare database.
    *Archives, every member (10.2):* a tar.gz whose **first** entry is a plain database (a snapshot made with `dbEncrypted: false`);
    a tar.gz whose **later** entry is the database - second, third and last, under the names `payload.bin`, `data`, `x.jpg` and
    `beememorybank.db`, after a manifest, a text file and a real BMBDB2 entry; an **uncompressed** `.tar` with the database as a
    later entry; the same in pax, GNU and v7 tar formats; a **zip** with the database as an extensionless entry (`payload`), as an
    entry in a sub-folder (`a/b/c`) and as the last entry, written with stored and with deflate entries -> all `plain_archive`.
    *Not examined, so `unverified_archive` (fail closed):* a tar.gz that holds a gzip, a tar that holds a tar, a zip that holds a zip
    or a zip that holds a gzip (**nested**); an archive with a bzip2, xz or zstd member; a top-level file with the bzip2, xz, zstd,
    lz4, 7-Zip or RAR magic; a tar.gz cut inside its second entry; a gzip with a valid header and a corrupt stream; a tar whose second
    header has a bad checksum; a zip entry marked encrypted (general-purpose bit 0); a zip entry compressed with a method other than
    stored, deflate or deflate64; a zip whose central directory is unreadable; an archive over the injected entry limit and one over
    the injected inflated-bytes limit. Also: a file it cannot open (`unreadable`, a file locked by the test); a link (`link`); a hard
    link to a live file (`shares_identity`) and a live file with a second name (`live_linked`).
    *Finds nothing:* a clean folder whose snapshots are real BMBDB2 archives (members: the encrypted database, 10,000 random-content
    media blobs among them - no blob is taken for a nested archive - a manifest, a signature and a key envelope), whose media are
    `*.enc`, whose baselines are BMBCH1, whose logs are rotated `.gz` and `.tar.gz` of text, which holds a zip export of notes
    (markdown and images) and a tar.gz whose `beememorybank.db` entry is a SQLCipher file; a copy of the boot store in
    `updates/pre-update-x/` (application id; the erase of 10.2 never touches it); a WAL that holds only its header; and **a
    sealed node's own live `-wal` and `-journal`** (real keyed connections left open): in mode `Sealed` they are no finding, while a
    plain `beememorybank.db` or `chat.db` is. It allows the live files exactly as 10.2 says for each mode. The scan of a 50,000-file
    folder (the media of the large fixture) ends in a time the test records.
15. `DetectMode` / `Diagnose` (A.2): Plain (no boot store, plain or no database), Sealed (our boot store + encrypted database), and Broken
    for each of: encrypted database without a boot store, a boot store beside a plain database, a boot store without our
    application id, an empty or missing database file beside a boot store, a sealed file whose length is not a multiple of 4096, a
    `*.seal-old*` file beside the live files with no `seal-journal.json` (with a journal file present the resolver of 10.6
    governs); the node refuses to start on Broken, names the reason and the files, and creates, moves and deletes nothing (hashes of
    every file before and after).
    *Plain companions (2.5), each beside an otherwise valid sealed pair and each with the reason `plain_companion` and the
    relative name in the answer:* a plain `chat.db` (a real plain chat database, and a no-schema SQLite file); a mixed set (encrypted
    `beememorybank.db`, plain `chat.db`); a plain `chat.db-wal` and a plain `chat.db-journal` made by real plain connections (the
    WAL once with and once without a page-1 frame); a plain `beememorybank.db-wal` and `-journal`; a plain WAL or journal behind a
    renamed copy of a plain database; two companions at once (both are named). *Sealed, not refused:* a valid sealed pair with
    `chat.db` absent; with `chat.db` sealed; with zero-length `chat.db` and sidecars; **with the sealed pair's own `-wal` and
    `-journal` and the sealed chat database's `-wal`**, taken from real keyed connections left open (their headers are plain, their
    pages are not; the test asserts the headers, so a regression to "WAL magic = plain" is red); a WAL whose 4,000 frames are
    random bytes; the plain page test fed four million random 4096-byte pages passes none, and every page of a real plain database
    (table, index, interior, page 1) passes. A companion locked by the test is `companion_unreadable`. A WAL with an invalid header is
    not examined (SQLite ignores it). Interrupted file-set restore (8.7.2): the same cases produced by removing one file of a
    sealed set at a time, and a set with a journal (resolved) - each ends in a named reason, never an empty vault; and the
    plain-backup `chat.db` copied over a sealed set is refused at the start, not later. `VaultStoreProbe.IsSealedPair` (AppPaths,
    7.6) gives the same answer as `DetectMode == Sealed` on every one of these cases (a test in a project that references both
    assemblies), and the scanner of 10.2 and `VaultStoreProbe` give the same plain/not-plain answer for every WAL and journal file of
    the corpus.
16. Pre-encryption snapshot (phase 2, 10.4 step 3, 10.7): `CreateAsync(outputDirectory: <data>/pre-encryption, wipeStaging: true,
    additions: envelope)` writes a BMBDB2 that decrypts to a valid image, is not listed, is not pruned by `PruneOldSnapshots` after
    three newer snapshots, and its plain staging copy is overwritten before deletion (the `afterOverwrite` seam). The archive holds
    `key-envelope.json`, listed in the manifest (a flipped byte fails the manifest check), with exactly the slots 10.7 allows (a test
    node that also has an OS slot, a server slot, a non-superadmin user slot and an agent: none of the four appears) and no canary.
    The restore side (phase 2, `SessionSnapshotKeyOperations`, `SnapshotService.Restore.cs`): on a node whose own DEK does not open
    the snapshot - the DEK was rotated since, or the vault was reset and set up again - `restore-standalone <name>` with the old password
    opens it through the envelope (and finds the file in `pre-encryption/`); with a wrong password or an archive without an envelope it
    refuses before anything is replaced; the DEK found through the envelope is cleared (the `Cleared` pattern of 3.3) and never logged.
17. `FileIdentity` (10.2): two names of one file give the same volume and file index and a link count of two, a copy gives a different
    identity, a missing or locked-away file gives null; the Windows and Unix implementations are both tested (the Unix ones by the Linux
    and macOS checks, 15.5). The scanner and the erase use it: a hard link to the live database made under `tmp/` is reported
    `shares_identity` and is refused by the erase (the live file is untouched, byte for byte), a live file with a second name anywhere is
    reported `live_linked`, and an identity that cannot be read blocks the preflight with `identity_unavailable`.
### 15.2 Part 2

1. Boot schema equals `docs/design/boot-store.golden.txt`; boot `tbl_whitelist` has every column of the vault's; boot ledger
   separate (the vault runner leaves it alone and vice versa, including the ghost rule).
2. After `RewriteAsync` on the canary vault: the boot file and `-wal` contain no canary (no user name, agent name, title); doors
   equal key slots + auto-unlock agents; wrappers per 3.4.
3. Writers: create, replace and delete a user slot, regenerate the recovery key, create and revoke an agent (Web endpoint and CLI),
   turn OS and server auto-unlock on and off, a DEK rotation on the initiator and on a peer: after each, the boot doors equal the
   sealed set; a revoked agent's door is gone before the endpoint returns; the trigger version moved.
4. Kill points of the write protocol (4.3 table): each leaves the node unlockable with the password it had and never with a removed
   door through the product.
5. Wrapper pruning: after an initiator rotation exactly one wrapper (the new DEK); the old DEK opens nothing in the boot store; on an
   auto-accepting peer the old wrapper stays until the last old-DEK slot is rewrapped.
6. Rotation kill points (3.5): crash before the wrapper, between wrapper and commit, between commit and rewrite: each restart
   unlocks with the password and reaches the new DEK.
7. DBK crypto (3.3): a round trip; each of `wrapped`, `iv`, `dek_fp`, the node id, the generation tampered or swapped -> `null`
   without an exception; malformed rows (iv of 0, 11 and 13 bytes; wrapped of 0, 47 and 49 bytes; `dek_fp` upper-case, 63 or 65
   characters, not hex; generation 0 and -1) -> `null` and one log line without lengths or bytes; a DEK of 31 or 33 bytes ->
   `ArgumentException` without bytes; a foreign boot store (other `node_id`) -> `null`; the AAD built from `Guid.ToString("D")` and
   the invariant decimal generation under a non-English culture (a test sets `CultureInfo.CurrentCulture` to `ar-SA` and
   `tr-TR`); every temporary buffer is zero after the call (the `Cleared` seam) on success, on authentication failure and on an
   exception; the returned DBK is the caller's.
8. Server auto-unlock (11.0, 11.2). *Role:* `BMB_HOST_ROLE` unset, empty, `server`, `desktop ` with a stray space or `1` -> Server;
   `desktop` (any case) -> Desktop; on Desktop the server routes answer 409 `role_desktop`; on Server `POST /api/keys/auto-unlock/enable`
   answers 409 `role_server` and an existing `os_auto_unlock` slot still unlocks at start. *Plain servers unchanged while off:* a plain
   server that never used the checkbox, with and without `BMB_AUTO_UNLOCK_KEY_FILE` set to an existing file, starts as 2.5 does - the
   same start tasks in the same order, no boot store file, no key slot of type `server_auto_unlock`, no enable record, the key file is
   not opened (a file-access probe), `EvaluateAsync` answers *off*, the status says `server: false`. *Scope (never on by itself):* off,
   with no slot and no record, after: a fresh `init`, a join, the 2.6.0 migrations on a database that had none, "Encrypt this node" on
   a node that had none, a snapshot restore in each of the three modes and from a blind copy and an Android backup of a backup **made
   while it was on, with the key file still present** (the slot row is deleted in staging and the door retired), a baseline, a for-join
   archive, an offline re-key, a node reset (slot, record and node-created key file gone), a DEK rotation (initiator and an
   auto-accepting peer: slot gone, record gone, key file deleted if the node created it); `BMB_AUTO_UNLOCK_KEY_FILE` set with no
   slot, and a key file present with no slot -> inert, one info line, locked; no `appsettings` key, environment variable or flag
   exists that enables it (a test lists the configuration keys the node reads); an agent key and a `bmbrt_` token get 403.
   *File-level restore (the restic round trip, sealed and plain):* a server with the checkbox on and the key file **outside the data
   volume** (`BMB_AUTO_UNLOCK_KEY_FILE`) is stopped; its data directory is backed up with the documented excludes; the directory is
   wiped and restored; the node is started with the same key file still mounted -> it starts **locked**, `EvaluateAsync` answers
   *stale slot*, the boot door is retired before any unlock is tried (a probe on `TryUnlockWithDekAsync` sees no call), after the first
   password unlock the slot is gone and the audit entry is there, the status says `server: false`, and `enable` with the exact phrase
   is what turns it on again. The same with the node-created key file (the excluded copy is absent -> locked), with a pre-update set
   copied back, with a data directory copied to another machine, and with the record deleted by hand; and the *in place* case: the
   directory restored over the live one, record and key file left where they were, the slot byte-identical -> stays armed, the slot
   different (disabled and enabled again since the backup) -> stale and locked. A backup taken **without** the excludes is the
   documented plain-copy case: the test asserts the guide's sentence, not a lock. *Enable:* refuses without the exact phrase `START
   UNLOCKED` (also lower-case, trailing space, empty), with a wrong password (401) and without an unlocked session; with both it
   creates one slot, (sealed) one door, an owner-only key file (a Windows ACL check and a Unix mode check, each skipped on the other
   family; a key file that is not owner-only is refused at start) and, last, the enable record; a crash after any step before the
   record leaves it off; after enable a restart opens the vault without a password, on a plain node and on a sealed one; after
   disable it does not (the record goes first); an operator's key file is never written, moved or deleted. *Inventory:*
   `docs/design/key-slot-writers.golden.txt` equals the sites the source test finds.
9. Relay: a closed sealed node accepts sealed pushes into the inbox (boot file: no canary), serves them byte-identical to a caught-up
   peer with sequence numbers above its head, refuses `after` below the head with 503 `vault_sealed_range`, refuses clear content
   (deferred, not stored), suspends a revoked node at receipt, answers `blobs/check` and `blobs/get` from the inbox; at unlock every
   inbox event is applied exactly once in order, a node revoked before the close is refused, numbering continues above every relay
   sequence served, reports are merged, the inbox is empty.
10. Crash during import: restart, unlock, every event applied exactly once.
11. Pause for a file copy (6.8, 5.10): `PauseWritesAsync` is granted only when the gate is Closed and waits for a receipt in flight; while
    paused, `POST events`, `report-position` and `blobs` answer 503 `vault_sealed_busy` (nothing stored, the pusher's cursor does not
    move), the reads keep answering, the boot store file does not change (hash before and after); released, the same pushes are stored
    once.

### 15.3 Part 3

1. Plain node unchanged: the existing Core, Sync, Integration (by class filter) suites pass with the B composition registered and no
   boot store.
2. Start of a sealed node: no vault or chat connection before a door opens; `/health`, login page, unlock, status and the relay set
   answer; every other route 503 `vault_sealed` (a route table walk against `docs/full-node/ROUTES.golden.txt`); MCP gets the JSON-RPC
   error; hosted services do not touch the vault (counting factory).
3. Unlock paths on a sealed node: password, recovery key (recover-access leaves the session locked and the database closed), legacy
   password slot, agent auto-unlock (a revoked agent's stale door refused and the database closed again), OS auto-unlock (Windows
   test), server auto-unlock (through a fake caller of `TryUnlockWithDekAsync`), update hand-off, iPhone quick unlock (fake
   Keychain): each opens, a wrong one does not, every refusal looks the same. A boot store of another node (a foreign DBK) ->
   `key_mismatch` and the node stays closed; a database older than its boot store after a rotation -> `store_stale` in the log
   and in `last_refusal`, the caller sees the generic `false`; a database older than its boot store but with unchanged unlock
   material still unlocks and the rewrite repairs the boot store.
4. Login on a sealed node: wrong password, non-superadmin, unknown user and wrong user name for a right password all give the same
   401 and leave the database closed.
5. Lock closes: after `Lock()` the vault and chat factories throw `VaultSealedException`, no pooled handle is left (the file can be
   renamed on Windows), the page key buffer is zero; a lock during a long read finishes within the 30 s limit. **The locked node holds
   no key and no key-bearing entry** (3.3, 5.4): the DBK, the page keys and the master DEK buffers are zero (the `Cleared` seams),
   the metadata key ring has no open key (`IMetadataKeyRing.Current` is null and `DeriveKey` throws `MetadataKeyUnavailableException`
   after `Lock()`: Step A's `MetadataKeyRing.Clear` on `SessionService.Locked`), `ChatDataProtector`'s cache is wiped, and the
   reflective walk of 15.1.3 (c), started from the gate, both factories, the session, the key ring and the chat protector, finds
   none of the canary DEK, DBK, page keys, metadata key or connection strings in any field, cache, pool or closure; the state is
   published as Closed only after that holds (the test samples the state from a second thread during `CloseAsync`).
6. Open steps: per-process sweeps run once per process, per-open steps on every open; Step A's policy never reads the database before
   open.
7. Migration kill points: the process killed after every step of 10.4 and inside the swap of 10.6 (a fault hook per step, and a real
   process kill in the Integration suite for three of them): restart -> plain with the old files or sealed with the new, data equal to
   the source, the canary vault fully readable after unlock, no third state; after the **next start** no `*.seal-old*` file and no plain
   copy exists (the scanner and a raw-byte scan of every file of `<data>`), whatever step the process died at after `swapping`; the
   pre-encryption snapshot written before the first change decrypts with the DEK at every kill point; a wipe that fails (a locked file)
   leaves the journal at `swapping`, the node sealed and usable, `migration.wipe_pending` in the status, and the next start finishes it.
   *Leftovers without a usable journal (10.6):* the swapped sealed pair plus `beememorybank.db.seal-old`, its `-wal` and `chat.db.seal-old`
   with (i) no journal, (ii) a journal of garbage bytes, (iii) truncated JSON, (iv) valid JSON with an unknown phase, (v) the phase
   `building`: each start wipes every leftover before it serves anything (a raw scan of the directory finds no canary, the live files
   are untouched byte for byte), keeps the bad journal as `seal-journal.json.unreadable` until the wipe is done and then removes it, and
   with a leftover that cannot be wiped (locked) starts sealed and closed with a rebuilt `swapping` journal and `wipe_pending`; the
   same directory with the plain live `beememorybank.db` still in place (or no live database at all) and a `.seal-old` beside it ends in
   the refusal `seal_leftovers` that names the files, and **no file is deleted, moved, renamed or changed** (hashes before and after); a
   restored copy of a mid-swap directory behaves the same; `DetectMode` says Broken for a leftover with no journal file; a hard-linked
   leftover is unlinked, not overwritten, and named in `migration.linked_plain`.
8. Migration verification catches: a target with one row removed, a corrupted page, a wrong key in the boot target, a title left in
   clear (an injected plain table), a lowered `sqlite_sequence` of `tbl_event`, a pre-encryption snapshot that fails its self-check
   (code `snapshot`, nothing else touched). Preflight `plain_copies`: a plain copy planted in `updates/pre-update-x/`, in `tmp/` and as
   `*.rekey-*` blocks the start; "Erase these copies" removes exactly the listed files and nothing else (an unrelated planted file
   stays, a path outside `<data>` and a link are refused, a wrong password is 401), after which the migration starts. A plain copy
   planted in `snapshots/` (a tar.gz with `dbEncrypted: false`) and one in `media/` under another name block the same way. A hard
   link of the live `beememorybank.db` under `tmp/` blocks as `shares_identity`; a database above the snapshot limit (the limit injected
   as `EnsureDbSizeWithinEncryptableLimit`'s tests do) blocks with `snapshot_limit` until the tick, and with the tick the migration runs
   without step 3 and the journal's `snapshot` is empty; "Erase these copies" refuses it (answer: the kind), the
   live database is byte-identical afterwards and the node still opens; the same for a link made outside the data directory
   (`live_linked`). After the migration the same endpoint erases a plain copy planted later (mode `Sealed`).
9. After migration of the canary vault: a raw-byte scan of **every file of `<data>`, no exception**, finds no canary (database, WAL,
   SHM, chat, boot, tmp, snapshots, `pre-encryption/`, `updates/`, the journal); the same scan before the migration finds them
   (control); free-space remnants are not scanned and are the documented residual. A migration of a node that had
   `server_auto_unlock` on shows the `auto_unlock_on` warning and keeps the owner's choice.
10. Logs: an Api log capture of a whole scripted session on a sealed node (start, unlock, edits, rename, tag merge, search, snapshot,
    lock, relay, import) and the desktop node's `bmbd-*.log` contain no canary.
11. Phones: the start and open sequence of both apps lives in one platform-free class (`PhoneVaultLifecycle`, Vault, 5.8) that
    `FullIos.Tests` and `Core.Tests` drive: seal, background (lock closes), unlock with password and with quick unlock (fake Keychain),
    migration interrupted and resolved at the next start.
12. CLI: `bmb status` on a sealed directory without the password; `bmb article list` refuses without it and works with
    `--password-stdin`.
13. Reset of a sealed node ends plain, empty, startable, and leaves `snapshots/` and `pre-encryption/` (and removes the enable record
    and a node-created key file); the rollback of 10.7 end to end, three times: (a) seal, reset, set up again with the same password,
    `restore-standalone` of the pre-encryption snapshot gives a plain node whose canary vault is fully readable, with a new node id;
    (b) seal, **DEK rotation** (initiator, with the peers' rotation accepted), password change, restart, unlock, reset, set up again with the
    password of the snapshot's day, restore - the same result; (c) a wrong password: refused before anything is replaced, the new empty
    vault untouched; (d) the same as (a) with the text of the recovery key of that day used as the password of the new vault and of the
    restore (a recovery slot is an Argon2id slot like the others, so the string works wherever a password does).
14. `bmb vault verify` (8.7.2 step 4): `ok` on a good sealed set; the failing check named for a damaged page (flip one byte of the
    sealed file), a boot store with a missing door, a missing wrapper; needs the password; leaves the database closed afterwards.
15. Phones (12.4): `phone_no_copy` blocks an unsynced phone and passes when a peer reported the head or the owner ticked the box; a fake
    `IKeyStore` that throws on any 32-byte DBK or page key is never called; `PhoneEncryptionAvailable` is false until the release check of 12.4.
16. Hold (5.10): `HoldClosedAsync` throws unless the state is Closed and waits for an unlock in progress; while held, `OpenAsync`,
    `OpenTemporarilyAsync` and the unlock routes refuse (`busy` / 503 `vault_busy`) and write nothing; releasing it (also on an exception)
    lets the next unlock work. The product's pre-update path (15.1.10) is the integration of this with the relay pause.
17. The delete route (10.7): `DELETE /api/vault/pre-encryption` needs the internal key, a superadmin, a non-agent caller and the password,
    removes the snapshot and its `.sig` only (an unrelated file in the folder stays), writes `pre_encryption_deleted`, and afterwards the
    rollback of 13 is no longer possible; the confirmation of a password change and of a DEK rotation shows the sentence of 10.7 while
    the snapshot exists and not otherwise.
18. Start beside a plain companion (2.5, 5.2 step 2): a sealed directory plus (i) a plain `chat.db`, (ii) a real plain `chat.db-wal`,
    (iii) a real plain `beememorybank.db-journal`, one at a time: the node refuses to start with the reason `plain_companion` and the
    relative file name (in the start message, the log, `bmb status` and `GET /api/vault/status` of the Web start page), opens no
    connection to any of the files, writes nothing (hashes of every file of the folder, the boot store included, before and after),
    sweeps nothing and serves no route; after the owner moves the file out of the data folder the node starts closed as usual and
    unlocks. The control: the same directory with the real sealed `-wal` and `-journal` of a node killed while open (a real process
    kill, one of the three kill tests of item 7) starts, unlocks and replays the WAL without any refusal.

### 15.4 After the parts are combined (end to end)

End-to-end tests that need all three parts together, run in process (no Docker; the Docker and macOS parts are the checks of
15.5). Items 1-7:

1. The canary mesh of step-a-spec 16.5 with two sealed full nodes, one plain full node, a blind node (the Docker blind host run in
   process through `BlindNodeFactory`, as step-a-spec 16.3 does; the real container is step 9 of 15.5) and a blind phone:
   activation, sealing of both, a restart of one sealed node left locked while the others write (relay through it), unlock and import,
   a DEK rotation with one sealed peer locked, a restore from the blind node onto a sealed node, snapshots and baselines from
   sealed nodes restored on a plain node; every artefact scanned, and a raw-byte scan of every file of every data directory.
2. The edges and pairings of 14.5, each with the data compared before and after: (a) N0 -> N1 with the published v2.5.1 build, back to
   2.5.1 in place and up again (the ledger rows 037-040 dropped and re-applied), the pre-update backup restorable; (b) activation with
   an N0 peer: it is refused 426 both ways, its three database files are byte-identical before and after, it is updated and catches up;
   (c) activation while a peer is offline for under 14 days: it returns, pulls the activation, becomes N2; offline for over 14 days:
   the activation is blocked `stale`; (d) N2 + N3 closed + N1 peer: events flow, clear content is deferred by the closed node and
   delivered after unlock, no cursor moves backwards; (e) an old binary started on a sealed folder: error 26, the three files
   byte-identical, `VaultFiles.Inspect` of the 2.5.1 desktop code says "not a vault" and the create-storage dialog does not write into
   that folder.
3. Rollback: N3 -> plain by 10.7 (reset, set up again with the old password, restore-standalone of the pre-encryption snapshot through
   its key envelope), **after a DEK rotation and a password change on the sealed node and a restart**; devices join again, the canary
   vault is complete; N2 -> N1 by restoring the pre-activation backup with a network restore on all nodes; the lost-changes statement of 14.5
   is checked against what actually came back.
4. DEK rotation with a failing metadata-key publisher (14.4 item 7), on a plain and on a sealed initiator, with one peer left
   out of the rotation: the publisher throws once after the DEK commit -> authoring is refused with `metadata_key_publish_pending`,
   `dek_rotation_completed` is not yet written; the retry publishes; the left-out peer, holding only the old DEK, opens no event
   authored after the rotation reported complete (neither on the wire nor from a copy of the sealed node's disk with the old door); a
   crash between the commit and the publish resumes after unlock.
5. File-level backup and restore (8.7), all on sealed servers: (a) stop a sealed node, back up its data directory to a restic
   repository with the documented excludes, scan the repository's restored files for canaries, wipe the node's directory, restore,
   start, unlock, `bmb vault verify`; (b) interrupted restore (kill the copy halfway: the node refuses with a named reason); (c) a
   database from one set with the boot store of another set of the same node (unlock works after the rewrite when only unlock material
   differs; `store_stale` named when it does not); (d) a backup of a running node is not offered by any product path; (e) a set that
   holds a leftover `*.seal-old*` file next to a sealed pair is wiped at the start (10.6); (f) **the product's own path**:
   `UpdateService.TakePreUpdateBackupAsync` on a locked sealed server whose peers keep pushing during the copy (the canary mesh keeps
   writing on the other nodes); the folder it wrote is scanned, then the node's directory is emptied, the folder copied back, the node
   started and unlocked, `bmb vault verify` says ok, and the events the peers pushed during and after the hold are all there exactly
   once; (g) a server with the checkbox on and its key file outside the data volume (`BMB_AUTO_UNLOCK_KEY_FILE` still mounted) goes
   through (a) and (f), each of which empties the node's directory first (so the local enable record is gone): it **starts locked**
   after each restore, the slot is retired, and only the exact enable call turns it on again; and the in-place counterpart (the
   restored files over a live directory whose record and key file stay, the slot byte-identical) stays armed, the same with a changed
   slot starts locked (15.2.8).
6. Blind backup round trip, in process (step-a-spec 9.8, 12): the blind host's `BlindBackupService` `VACUUM INTO` staging copy plus
   `baselines/current/` hold no canary and no note count after the mesh above has run (a sealed full node among its peers changes
   nothing there); a new node restores from that folder (step-a-spec 12). The restic binary round trip is step 9 of 15.5.
7. The 2,500 and 50,000 note fixtures (`tools/BeeMemoryBank.SeedGen`): migration time and peak memory, first unlock time, snapshot time
   on sealed versus plain.

### 15.5 Linux, Docker and macOS checks

The Linux and macOS evidence needs containers and a Mac, so it is gathered by checks that run apart from the in-process tests, one
bounded command at a time, on a machine where that is allowed, and is recorded with the limits used. When a check cannot run, the
reason is recorded and the release notes name Linux/macOS sealed nodes as untested; the checks are not silently skipped.

**Resource limits (hard).** The checks run **one container at a time, started with `--cpus=2 --memory=3g`** (compose: `cpus: 2`,
`mem_limit: 3g`, `memswap_limit: 3g` in an override file kept outside the repository,
`docker compose -p bmbgate -f docker-compose.yml -f <override>`), each command bounded in time (15 minutes for a test run, 45 for
an image build), and never beside another heavy command. The limit is not raised: a command that the limit kills
(exit 137, an out-of-memory error) is a finding about the test, the fixture or the build, to be fixed by a smaller `-m`, a narrower
filter or a lighter fixture, or reported; it is never answered with a larger limit. The one step that needs two programs at once is
step 9 of the smoke, below, and it shares the same total.

Linux check (image `mcr.microsoft.com/dotnet/sdk:10.0`; limits `--cpus=2 --memory=3g`,
recorded with the exit code and the peak memory read from `docker stats --no-stream`):

```
docker run --rm --cpus=2 --memory=3g -v "<repo>:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c \
  'mkdir /work && tar -C /src --exclude=.git --exclude=bin --exclude=obj -cf - . | tar -C /work -xf - && cd /work &&
   export DOTNET_PROCESSOR_COUNT=2 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 &&
   dotnet build tests/BeeMemoryBank.Storage.Tests -m:1 -nr:false -p:UseSharedCompilation=false &&
   dotnet test tests/BeeMemoryBank.Storage.Tests --no-build --blame-hang-timeout 300s -- xUnit.MaxParallelThreads=2'
```

repeated for `tests/BeeMemoryBank.AppPaths.Tests`, `tests/BeeMemoryBank.Core.Tests` (filter `FullyQualifiedName~Sealed|Boot|AutoUnlock|HostRole`),
`tests/BeeMemoryBank.Cli.Tests`, and `tests/BeeMemoryBank.Integration.Tests` by the class filters of parts 2 and 3 (each run at most
15 minutes). What must pass on Linux that Windows cannot show: paths with spaces, `#`, `%`, `&`, `'` and non-ASCII through
`SqlCipherScheme.ToUriPath`; `OwnerOnlyFile` mode 0600 for `server-auto-unlock.key`; the Unix permission assertion of 15.2.8;
`File.Move` no-replace races; `SecureFileWipe` on a symlink and on a real hard link (`link(2)`); `FileIdentity` (device and inode, link
count) and the scanner's `shares_identity` / `live_linked` findings; case-sensitive file names in the resolver and the `*.seal-old*`
leftover rules; the SDK image running as root (an owner-only check that must not be fooled by root); the plain page test on real sealed and
plain WAL and journal files written by SQLite3MC on Linux (15.1.15), and the archive scan on tar and zip files made by GNU `tar` and
Info-ZIP `zip` (15.1.14).

Docker smoke (image built from the repository `Dockerfile` as `bmb-gate:local`, compose project `bmbgate`, data in a throw-away
bind directory, `docker compose -p bmbgate ...` with the limits `cpus: 2` and `mem_limit: 3g` of the override file above, one container
at a time, torn down afterwards; the image is built once with `DOCKER_BUILDKIT=0 docker build --memory=3g
--memory-swap=3g --cpuset-cpus=0-1 -t bmb-gate:local .`, because the legacy builder honours the limits - where only BuildKit exists
the image is built without these limits and that is recorded): `scripts/sealed-gate.sh`
(it uses only `curl` against the container's API with the internal key it reads from the
container environment, and `docker compose` itself) performs, and fails loudly at the first deviation: (1) start, initialise a vault
through the setup API, add the canary notes; (2) activate (Step A) with a single node; (3) `POST /api/vault/seal` and wait for `done`;
(4) `docker compose restart bmb`: `GET /health` 200, `GET /api/vault/status` says `sealed`/`Closed`, a content route answers 503
`vault_sealed`; (5) unlock with the master password: content readable; (6) `docker compose stop bmb`, scan the whole data volume
for canaries (none), start again; (7) enable the server checkbox through the API with the two confirmations, `restart`: the node comes
up `Open`; disable, `restart`: `Closed`; enable again with the key file mounted from **outside the volume** as a Docker secret
(`BMB_AUTO_UNLOCK_KEY_FILE`); (8) `docker compose stop bmb`, restic-style copy of the volume with `--exclude 'server-auto-unlock.*'`,
restore into a fresh directory, start with the secret still mounted: the node comes up **`Closed`** (stale slot, 11.2), unlock with
the password, `bmb vault verify` says `ok`, the status says the checkbox is off (the product's own pre-update path is covered in
process by 15.1.10 and 15.4.5 (f) through the test seams `UpdateService` already has; the container smoke does not trigger an update); (9) the blind container (the one step that needs two programs alive together, the full node and the blind node, so it runs after the
project of steps 1-8 has been stopped and with the same total as one container: the full node again from the step-3 data directory
with `cpus: 1`, `mem_limit: 1792m`, the blind node with `cpus: 1`, `mem_limit: 1280m` through a second override file, nothing else
running, both stopped when the step ends), following
`docker/blind/README.md` "First setup": `BMB_LAN_ADDR=127.0.0.1 docker compose -f docker/blind/compose.yaml up -d` (the image built before under the same builder limits, so no `--build`), then
`docker exec -i bmb-blind bmb blind init --repo-folder /backups/restic` (secrets on stdin, never as arguments), pair it with the
canary full node of step 3, let the full node reseed it in format 2, `docker exec bmb-blind bmb blind backup now`, `docker exec
bmb-blind bmb blind backup verify --full`, restore the latest snapshot of `/backups/restic` into a throw-away folder with the same
restic and scan every restored file for the canaries and for a note count (none), and restore a new node from that folder
(step-a-spec 12). The compose files and the entrypoints are not changed by the smoke.

macOS check (a Mac, `dotnet test` of `Storage.Tests` and `AppPaths.Tests` with the filters above, plus the phone release check of 12.4):
paths with spaces, the Keychain-backed OS auto-unlock on the desktop role, the server role with a key file, `SecureFileWipe`.

## 16. Interfaces between the parts

**One set of contract files, one author.** The six contract files of Appendix A (A.1-A.6) are created once, byte for byte from this
document, containing nothing else (no implementation of any interface, no test, no project-file change), and the solution builds
with them. From then on the files belong to the spec: nobody edits one on their own (a change is made in this document first).
Roles are fixed so that two parts never create or implement the same thing: **part 1 creates** the contract files; **part 3
implements** A.1 and the `IVaultKeySource` of A.2 (in `Sealed/VaultGate.cs`); **part 2 implements** A.3, A.4 (except
`ISealedVaultKeys`, part 3) and A.5, each in its own files, and consumes A.1/A.2 only once the contract files exist; A.6 is
implemented by its own file (part 1 owns its tests).

Parts 2 and 3 write code that names a contract only once the contract files exist (they can prepare tests and fakes from Appendix A
meanwhile). Where a part must compile before then, it adds the needed contract file(s) in a separate step, byte-identical to
Appendix A, implements nothing in it, and drops its copy as soon as the real files exist (identical content combines without a
conflict; if a copy differs, the contract files win and the code is fixed).

| Contract | File | Implemented by | Used by |
|---|---|---|---|
| A.1 `IVaultGate` (incl. `HoldClosedAsync`), `VaultState`, `VaultOpenResult`, `VaultOpenRefusals`, `VaultSealedException`, `VaultKeyMismatchException`, `IVaultOpenStep` | `libs/BeeMemoryBank.Core/Interfaces/IVaultGate.cs` | 3 | 1, 2, 3 |
| A.2 `VaultStoreMode`, `VaultStoreDiagnosis`, `VaultStoreFiles` (`Diagnose`, `DetectMode`, the plain page test), `DbPurposes`, `IVaultKeySource`, `SqlCipherScheme` | `libs/BeeMemoryBank.Storage/Sealed/SealedStore.cs` | key source: 3 | 1, 2, 3 |
| A.3 boot records and `IBootStore`, `IBootDoorStore`, `IBootDbKeyStore`, `IBootMirrorStore`, `IRelayInbox` | `libs/BeeMemoryBank.Storage/Boot/BootContracts.cs` | 2 | 2, 3 |
| A.4 `IUnlockMaterialMirror`, `PendingDoor`, `IDatabaseKeyService`, `ISealedVaultKeys`, `ISealedRelayLifecycle` (incl. `PauseWritesAsync`), `RelayImportResult` | `libs/BeeMemoryBank.Vault/Sealed/SealedContracts.cs` | mirror, key service, lifecycle: 2; vault keys: 3 | 1, 2, 3 |
| A.5 `ISealedRelay` | `server/BeeMemoryBank.Api/Services/Sealed/ISealedRelay.cs` | 2 | 3 |
| A.6 `SecureFileWipe` | `libs/BeeMemoryBank.AppPaths/SecureFileWipe.cs` | 1 (the file is the implementation) | 1 (phase 2), 3 |
| Appendix B `B001_boot_store.sql`, `040_sealed_unlock_material.sql` | `libs/BeeMemoryBank.Storage/BootMigrations/`, `libs/BeeMemoryBank.Storage/Migrations/` | 2 | 3 (tests) |
| `DbConnectionFactory(path, IVaultKeySource?, purpose)`, `OpenFile`, `CloseForSealAsync`, `CreateSealedForTests` | part 1 API (7.2) | 1 | 2, 3 |
| `PlainImageReader`, `SealedFileWriter`, `PlainImageImporter` | part 1 API (8.2) | 1 | 1 (phase 2), 3 (tests) |
| `FileIdentity.TryGet(path) -> (volume, index, linkCount)?` | part 1 API (10.2), `libs/BeeMemoryBank.AppPaths/FileIdentity.cs` | 1 | 3 (resolver, erase), 1 (scanner) |
| `PlainVaultCopyScanner.Scan(dataPath, mode)` | part 1 API (10.2), `libs/BeeMemoryBank.AppPaths/PlainVaultCopyScanner.cs` | 1 | 3 (preflight, erase, final scan) |
| `DatabaseKeyCrypto` (`Wrap(dbk, dek, nodeId, generation) -> BootDbKeyRow`, `TryUnwrap(row, dek, nodeId) -> byte[]?`, `DerivePageKey`) | part 2 API (3.3) | 2 | 3 (tests) |
| `SessionService.TryUnlockWithDekAsync(byte[], BootDoor?)` | part 3 API (4.7) | 3 | 2 (OS and server auto-unlock, hand-off), 3 |

## 17. File ownership and composition rules

### 17.1 Owners (phase 1: files no Step A part owns)

| Area | Part 1 (key seam, files, snapshots, platforms) | Part 2 (boot store, unlock material, relay) | Part 3 (lifecycle, migration, clients) |
|---|---|---|---|
| Core | `Interfaces/IDbConnectionFactory.cs` (doc), `Interfaces/IVaultGate.cs` (A.1, a contract file, frozen) | - | - |
| Storage | `Sqlite/DbConnectionFactory.cs`, `NodeDependencyInjection.cs`, new `Sealed/SealedStore.cs` (A.2, a contract file, frozen), `Sealed/SqlCipherPages.cs`, `Sealed/PlainImageImporter.cs`, `Sealed/SealedFiles.cs`, `docs/design/key-string-sites.golden.txt` | new `Boot/*` (the implementations; `Boot/BootContracts.cs` is A.3, a contract file, frozen), `BootMigrations/B001_boot_store.sql`, `Migrations/040_sealed_unlock_material.sql`, the csproj resource lines, `docs/migrations.golden.txt`, `docs/boot-migrations.golden.txt` | - |
| Vault | - | `Sealed/SealedContracts.cs` is A.4 (a contract file, frozen); new `Sealed/DatabaseKeyCrypto.cs`, `Sealed/DatabaseKeyService.cs`, `Sealed/UnlockMaterialMirror.cs`, `Sealed/UnlockMaterialWatcher.cs`, `Sealed/RelayInboxImporter.cs`, `Core/Services/UserService.cs`, `Core/Services/KeyManagementService.cs` (`Sync/DekRotation/DekRewrapper.cs` is a phase-2 row: Step A's change first, 17.2) | new `Sealed/VaultGate.cs`, `Sealed/SealMigrationService.cs`, `Sealed/SealVerifier.cs`, `Sealed/PhoneVaultLifecycle.cs`, `Core/Services/NodeResetService.cs` |
| Infrastructure | - | `OsAutoUnlock/OsAutoUnlockService.cs`, `OsAutoUnlock/UpdateUnlockHandoff.cs`, new `AutoUnlock/ServerAutoUnlockService.cs`, new `AutoUnlock/HostRole.cs`, `docs/design/key-slot-writers.golden.txt` | `Mdns/MdnsAnnouncer.cs` |
| Desktop | - | `desktop/BeeMemoryBank.Desktop/Services/NodeLifecycleService.cs` (the one line `BMB_HOST_ROLE=desktop`, 11.0) | - |
| AppPaths, Profiles | `VaultFiles.cs`, new `VaultStoreProbe.cs`, `Profiles/VaultCopier.cs`, `SecureFileWipe.cs` (A.6, a contract file, frozen), new `FileIdentity.cs`, new `PlainVaultCopyScanner.cs` | - | `VaultStartup.cs`, new `SealSwapResolver.cs`, `SealJournal.cs` |
| Rekey | `RekeyRunner.cs`, `Steps/ChatRekeyStep.cs`, `Steps/RekeyPreflight.cs` | - | - |
| Sync | - | - | `SyncScheduler.cs`, `CleanupService.cs`, `PendingEmbeddingProcessor.cs` |
| Embeddings, Vault hosted | - | - | `Embeddings/PendingEmbeddingProcessor.cs`, `Vault/Sync/PendingIndexProcessor.cs` |
| Api | `Services/ChatDbConnectionFactory.cs`, `Services/UpdateService.cs` | `Services/Sealed/ISealedRelay.cs` is A.5 (a contract file, frozen); new `Services/Sealed/SealedRelay.cs`, `Endpoints/AutoUnlockEndpoints.cs`, `Endpoints/AgentEndpoints.cs` | `Program.cs`, `Startup/ApiStartupTasks.cs`, `Startup/ApiPipeline.cs`, new `Startup/SealedVaultServices.cs`, new `Middleware/SealedVaultMiddleware.cs`, `Middleware/AgentAuthMiddleware.cs`, `Middleware/McpSessionGuardMiddleware.cs`, `Endpoints/SessionEndpoints.cs`, new `Endpoints/VaultEndpoints.cs`, `Helpers/ExceptionStatusMap.cs`, `Models/*` additions in a new `Models/VaultModels.cs`, the hosted services `Services/AuditLogPruningHostedService.cs`, `RemoteAccountSyncScheduler.cs`, `ChatHistoryBackfillProcessor.cs`, `Recovery/RecoveryReconcileWatcher.cs`, `Recovery/StateAnchorScheduler.cs`, `Services/ChatDbInitializer.cs` (open step) |
| Web | - | - | new `Pages/VaultEncryption.cshtml(.cs)`, `Pages/Login.cshtml(.cs)`, `Pages/Shared/Admin/_AdminSecurity.cshtml` (warning, link), new `Services/ApiClient.Vault.cs` |
| CLI | `Commands/SnapshotCommand.cs` | `Commands/AgentCommand.cs` | `CliServiceProvider.cs`, new `Commands/VaultCommand.cs` |
| Mobile | - | - | `Mobile/App.xaml.cs`, `Mobile/Pages/SecurityPage.xaml(.cs)`, `FullIos/Services/FullNodeServices.cs` (see 17.2), `FullIos/Services/QuickUnlock.cs`, new `FullIos/Pages/EncryptDatabasePage.xaml(.cs)` |
| Docker | - | `.env.example`, `docker-compose*.yml` comments | - |
| Docs and scripts | `docs/sqlite-engine.md` | `docs/deployment.md` | `README.md`, `docs/mcp.md`, `docs/full-node/IOS.md`, new `docs/sealed-database.md` (operator guide), new `scripts/sealed-gate.sh` (15.5) |
| Tests | new test files; an existing test file is edited only by the part that owns the production file it tests | same | same |

### 17.2 Phase 2: files a Step A part owns

Step A and Step B ship in one release; the Step A parts own these files (step-a-spec 15.1). Step B edits them on top of Step A's own
changes, in separate hunks:

| Step A owner | File | Step B owner | Edit |
|---|---|---|---|
| A part 1 | `libs/BeeMemoryBank.Vault/Core/Services/SessionService.cs` | 3 | 4.4, 4.7 (door source, open before checks, `TryUnlockWithDekAsync`, `CompleteOpenAsync` first in the chain) |
| A part 1 | the CLI command registration, `Commands/StatusCommand.cs` (`bmb status`) | 3 | `bmb vault` verbs; `bmb status` on a sealed directory (5.9) |
| A part 1 | `libs/BeeMemoryBank.Rekey/RekeyD1Check.cs` | 1 | the D1 check also over the old boot store (13.2) |
| A part 1 (14.4 item 7) | `libs/BeeMemoryBank.Vault/Sync/DekRotation/DekRewrapper.cs`, `server/BeeMemoryBank.Api/Services/DekRotationService.Accept.cs`, `libs/BeeMemoryBank.Vault/Metadata/IMetadataKeyRing.cs` and the authoring refusal | 2 for `DekRewrapper.cs` | Step A part 1 makes the metadata-key change first (the new generation inside the DEK transaction, the durable marker, the publish retry, the authoring refusal); Step B part 2 then adds the mirror calls of 3.5 and `'server_auto_unlock'` in the peer delete list, in separate hunks. Step B never edits the other two files |
| A part 1 (Web Admin card), possibly | `server/BeeMemoryBank.Web/Services/ProxyRouteTable.cs` | 3 | the routes of 18 |
| A part 1 | `SECURITY.md`, `docs/architecture.md`, `docs/encryption.md` | 3, 3, 2 | Step B sections |
| A part 3 | `docs/snapshot-restore.md`, `docs/account-recovery.md` | 1, 2 | Step B sections |
| A part 3 (mobile join services), possibly | `mobile/BeeMemoryBank.FullIos/Services/FullNodeServices.cs` | 3 | the start split (5.8) |
| A part 3 | `SnapshotService.Create.cs`, `.Maintenance.cs`, `.Restore.cs`, `.NetworkRestore.cs`, `.Crypto.cs`, `SessionSnapshotKeyOperations.cs`, `Recovery/RecoveryRestoreService.cs`, `Recovery/RestoreEvidence.cs`, `BlindPhone/AndroidBackupRestoreSource.cs`, `Blind/BaselineProducer.cs`, the join archive producer Step A part 3 writes (in its own files, never the lambda in `SyncEndpoints.cs`), `Blind/BlindAlarmService.cs` (the starter's wait) | 1 (`BlindAlarmService.cs`: 3) | 7.5, 8.3, 8.4; `CreateAsync(outputDirectory, wipeStaging)` and the `key-envelope.json` entry for the pre-encryption snapshot (10.4 step 3, 10.7); `restore-standalone` looking a name up in `snapshots/` and then in `pre-encryption/`, and opening a BMBDB2 database through the envelope when the session's key does not (10.7: `SessionSnapshotKeyOperations.cs`, `SnapshotService.Restore.cs`); deleting a `server_auto_unlock` key-slot row from the staging database before it replaces the live one, in every restore and import file of this row (11.2 item 2) |
| A part 3 | `mobile/BeeMemoryBank.FullIos/Pages/SettingsPage.xaml.cs` | 3 | one link to the new encryption page |

`SyncEndpoints.cs`, `EventApplier*.cs`, `EventLogRepository.cs`, `SyncProtocolPolicy.cs`, `ApiServices.cs`, `BlindRoleServices.cs` and
the Step A migrations are **not** edited by Step B: the relay is dispatched by middleware (6.1), the import calls the public
`ApplyAsync` (6.6), the policy is marked activated from outside (5.2), B's API registrations go through `Program.cs` right after
`AddBeeApiServices` (`AddSealedVault`), the sequence floor is kept by a timer (6.3) rather than an append hook.

### 17.3 Composition rules

1. A file is edited only by its owner in 17.1/17.2; the contract files of 16 are written once, byte for byte from Appendix A, and are frozen; the migration files of Appendix B are added byte-identical by part 2.
2. Goldens touched by several parts (`docs/full-node/ROUTES.golden.txt`, `docs/full-node/DI.golden.txt`, `docs/vault-split/*.txt`,
   `docs/migrations.golden.txt`) are regenerated once the parts are combined, not edited by hand.
3. Migration number 040 is Step B's; the Step A migrations are 037-039.
4. A part that changes a behaviour updates the product's own docs that describe it.
5. Order of dependence: Step A first, then Step B part 1, part 2, part 3.
6. Project references Step B adds: `BeeMemoryBank.Vault` -> `BeeMemoryBank.AppPaths` (part 3's `SealMigrationService` and `PhoneVaultLifecycle`
   use the scanner, the wipe and the identity check, which live in AppPaths so that the resolver, the CLI, Profiles and Rekey reach them
   too). AppPaths itself keeps no project reference. The blind composition already carries AppPaths; the `docs/vault-split` goldens are
   regenerated once the parts are combined (rule 2) and must still show no Vault type in the blind closure. No other new edge.

## 18. Golden files, docs, migrations

- Migrations: `040_sealed_unlock_material.sql` (part 2; triggers only, no table); boot migration `B001_boot_store.sql`; goldens
  `docs/migrations.golden.txt`, `docs/boot-migrations.golden.txt` (new). `RekeyTables` needs no change (no new vault table).
- `docs/design/boot-store.golden.txt` (new, part 2); `docs/design/key-slot-writers.golden.txt` (new, part 2: every site that inserts into
  `tbl_key_slot`, 11.2); `docs/design/key-string-sites.golden.txt` (new, part 1: every file that may touch a key-bearing connection
  string, 15.1.3).
- `docs/full-node/ROUTES.golden.txt`: + `GET /api/vault/status`, `GET /api/vault/seal/preflight`, `POST /api/vault/seal`,
  `GET /api/vault/seal/progress`, `POST /api/vault/seal/erase-plain-copies`, `DELETE /api/vault/pre-encryption`, `GET /api/keys/server-auto-unlock/status`,
  `POST /api/keys/server-auto-unlock/enable`, `POST /api/keys/server-auto-unlock/disable`. `docs/blind-node/ROUTES.golden.txt`
  unchanged.
- `docs/full-node/DI.golden.txt`, `docs/vault-split/vault-types.txt`, `shared-types.txt`: regenerated once the parts are combined;
  `docs/blind-node/DI.golden.txt` unchanged (the blind composition registers nothing of Step B).
- Docs per 17.1; the operator guide `docs/sealed-database.md` covers: what encryption protects and what it does not (2.1), the
  preflight (including `plain_copies`, what each finding kind means - a database inside an archive under any name, an archive this
  version cannot look into, a second name of a file - and what to do about each, and the erase), the
  pre-encryption snapshot, its key envelope (it opens with the old password) and the five-step rollback of 10.7, the edges and
  pairings of 14.5, file-level backup and restore (8.7: stopped node only, `server-auto-unlock.*` and `os-auto-unlock.dat` never in a
  backup, the quiesced set the update makes, `bmb vault verify`), the startup refusals `seal_leftovers` and Broken (with the
  reason codes of 2.5, `plain_companion` among them) with their recovery steps, lost boot store, auto-unlock by host role with the
  warnings (11) and what a restore does to it (11.2 item 3: a fresh, other-machine or changed-slot restore starts locked; an in-place
  restore of an identical slot over the same server keeps the owner's earlier choice),
  phones (12.4), the relay while locked, the Linux and Docker checks (15.5).

## 19. Open points

1. **New vaults sealed at creation** (a vault created by `init` or by joining an activated mesh). Left for a later release so that
   2.6.0 creates nothing new in an untested state; the owner encrypts each node once.
2. **Host role by environment variable** (11.0): a node counts as a desktop only when the desktop application started it with
   `BMB_HOST_ROLE=desktop`; every other start is a server. A desktop user who runs `bmbd` by hand from a terminal therefore sees the
   server checkbox (the stricter warnings) - the safe direction.
3. **Temporary store in memory** on sealed connections (7.1): large in-place `VACUUM`s cost memory (257 MiB peak for a 351 MiB vault).
   Measured again at 50,000 notes in the end-to-end run (15.4 item 7).
4. **Old DBK wrappers on auto-accepting peers** remain until every old-DEK slot is rewrapped (3.4); equal to today's chain-material
   exposure on such a peer.
5. **Inbox limits** (2 GiB, 1 GiB free) are a judgement.
6. **Paths on Linux and macOS**: `SqlCipherScheme.ToUriPath` was checked on Windows (spaces, `#`, `%`, `&`, `'`, non-ASCII);
   Linux and macOS are covered by the part tests and the Docker smoke; UNC paths are refused.
7. **A plaintext export verb** (`bmb vault export-plain`) is not in 2.6.0; the way back is the encrypted pre-encryption snapshot or an
   offline pre-cutover backup (10.7).
8. **Chat history is in no snapshot** (today as before), so a rollback by snapshot does not bring it back; it stays in the sealed
   `chat.db` until a reset. The owner may want chat history in snapshots later.
9. **No hot file-level backup of a running sealed node** (8.7.1): the supported running-node backup is the snapshots folder. A
   `bmb vault backup-set` verb that writes a consistent set of a running node is a later release.
10. **The metadata key rotation is a change in Step A's code** (14.4 item 7) and is a precondition of shipping 2.6.0 (the metadata
    key rotates with every key rotation). The end-to-end test 15.4.4 must be green, and a build without that change is red on purpose.
11. **One release for both steps.** The engine ships in 2.5.2, Step A and Step B together in 2.6.0, and there is no separate
    enforcement release; any older wording that spoke of another split is superseded.
12. **Plain servers get the checkbox** (11.2). Today's restart behaviour (locked until the master password is entered) is the same for
    every plain server, so the feature is kept on plain servers and the one exception to "nothing changes on an unencrypted node" is
    stated (14.1). If sealed servers only were wanted, the change is one condition in `ServerAutoUnlockService.EvaluateAsync` and the
    enable route plus the removal of the plain-node cases of 15.2.8; nothing else depends on it.
13. **A restore in place over the very server that enabled the checkbox, with a byte-identical slot, leaves it armed** (11.2 item 3
    (i)): the enable record and the key file never left that server, so the restore created no slot, no record and no key file; it
    keeps in force a choice the owner already made, which is preservation and not a new enable. Every other file-level restore (a fresh
    directory, another machine, a changed slot), copy or move leaves it off (item 3 (ii)). A rule "any restore switches it
    off" would need the product to tell an in-place restore of an identical slot from no restore, which it cannot do without a record
    that a backup could also carry.
14. **The pre-encryption snapshot opens with the old password** (10.7), also after the owner has changed it - the price of a way back
    that survives a DEK rotation and a reset. It is as exposed as the boot store's doors; the page says so and offers its deletion.
15. **`FileIdentity` on Linux and macOS** uses `fstat` through a native declaration whose structure layout differs per platform; it is
    verified by the Linux and macOS checks of 15.5, and an identity that cannot be read blocks the preflight (fail closed) rather than weakening the check.
16. **A blind package under construction on a sealed node** writes a plain `control.db` (control tables only, no metadata, 8.3) into
    its staging folder for the minutes the build takes; the `Sealed`-mode scan reports it like any plain SQLite file and the page shows
    the file's age, and the scan of the migration (10.4 step 11) runs in maintenance mode, when no build runs. The owner should not
    erase it during a build.
17. **Limits of the plain page test** (2.5, 10.2): the test recognises table and index b-tree pages and the SQLite magic. A WAL or
    journal that holds only overflow, free-list or pointer-map page images, and no table or index page at all, is not recognised as
    plain; so is a plain page that does not parse. A WAL SQLite wrote for ordinary commits holds a b-tree page in each transaction that
    wrote a row, so the limit is theoretical for the files the product and its users produce; the test is proven against real plain and
    real sealed WAL and journal files (15.1.15, 15.1.14) and a ciphertext page passes it with a probability of about 2^-32.
18. **Archives the scanner does not open** (10.2): a container that does not begin at offset 0 (a self-extracting archive, a file with
    an archive appended) is not opened, and an archive format this version cannot read (bzip2, xz, zstd, lz4, 7-Zip, RAR, an encrypted or
    exotic zip entry, a nested archive, anything over the 100,000-entry or 16 GiB limit) is a finding `unverified_archive`, not a pass.
    The price is a false alarm: a harmless `.7z` or `.zst` the owner keeps in the data folder blocks the preflight until it is moved
    out. A later release may add an end-of-central-directory search for zip files with a prefix and readers for more formats.

---

## Appendix A: contract files (verbatim)

Each block compiles on the base tree as it is (checked with a scratch project referencing `BeeMemoryBank.Core`,
`BeeMemoryBank.Storage`, `BeeMemoryBank.Vault` and `Microsoft.AspNetCore.App`: 0 errors, no warning from these files). A.6 was also run (17 checks): wipe with
a content callback, absent, locked, read-only, empty and symbolic-link cases, and - with a real hard link made by `CreateHardLinkW` -
the `sharedWithAnotherName` cases (only this name is removed, the other keeps every byte; the control without the callback overwrites both).
A.2 `Diagnose` was run against **real SQLite3MC 2.4.0 files** (44 checks, all PASS): the 13 directory cases
with their reason codes; a real sealed pair with the sealed `-wal` and `-journal` of both databases (plain headers, encrypted pages) taken
from connections left open, and with a sealed `chat.db`, an absent or empty chat database, and a WAL of 4,000 random frames - all Sealed;
a real plain `chat.db`, real plain WALs (with and without a page-1 frame) and real plain journals of both databases - all Broken
`plain_companion` with the names; a locked companion `companion_unreadable`; nothing written by `Diagnose`; the plain page test passes
513 of 513 b-tree pages of a real plain database (table, index, interior, page 1), none of the 27 pages of a real sealed one, and none
of 4,000,000 random 4096-byte pages (about 2^-32 per page expected).

### A.1 `libs/BeeMemoryBank.Core/Interfaces/IVaultGate.cs`

```csharp
namespace BeeMemoryBank.Core.Interfaces;

/// <summary>Where a full node's database stands (docs/design/step-b-spec.md 5.1).</summary>
public enum VaultState
{
    /// <summary>Not sealed: a plain database, opened at start as before Step B. Every blind host and every unmigrated node.</summary>
    Plain,
    /// <summary>Sealed and closed: no database key in memory; only the boot store and the relay inbox are used.</summary>
    Closed,
    /// <summary>A door produced the master DEK; the database is being opened, migrated and checked. Requests still see Closed.</summary>
    Opening,
    /// <summary>Open; events received while closed are being replayed. Content routes work; sync still goes to the inbox.</summary>
    Importing,
    /// <summary>Open: everything works as on a plain node.</summary>
    Open,
    /// <summary>A lock is closing it: no new connection, waiting for the open ones.</summary>
    Closing,
}

/// <summary>What <see cref="IVaultGate.OpenAsync"/> did. <see cref="Refusal"/> is a code, never key material or a name.</summary>
public sealed record VaultOpenResult(bool Opened, string? Refusal = null)
{
    public static readonly VaultOpenResult Done = new(true);
    public static VaultOpenResult Refused(string code) => new(false, code);
}

/// <summary>Refusal codes of <see cref="VaultOpenResult"/> (spec 4.4).</summary>
public static class VaultOpenRefusals
{
    public const string NoWrapper = "no_wrapper";
    public const string KeyMismatch = "key_mismatch";
    public const string Broken = "store_broken";
    public const string DoorNotAuthorized = "door_not_authorized";
    /// <summary>A diagnosis, never a refusal on its own: the database is older than its boot store (spec 4.4 step 5).</summary>
    public const string StoreStale = "store_stale";
    /// <summary>The gate is held closed for a file copy (spec 5.10); the caller may try again in a few seconds.</summary>
    public const string Busy = "busy";
}

/// <summary>
/// The sealed database's lifecycle (spec 5). One per host. On a plain node every member answers as if the database were always open:
/// <see cref="State"/> is <see cref="VaultState.Plain"/>, <see cref="OpenAsync"/> returns <see cref="VaultOpenResult.Done"/>, nothing closes.
/// </summary>
public interface IVaultGate
{
    VaultState State { get; }

    /// <summary>True when this node's database is sealed (it was migrated), in any state.</summary>
    bool IsSealed { get; }

    /// <summary>True when the vault database can be used: <see cref="VaultState.Plain"/>, <see cref="VaultState.Importing"/> or <see cref="VaultState.Open"/>.</summary>
    bool IsUsable { get; }

    /// <summary>True while a <see cref="HoldClosedAsync"/> lease lives. The unlock routes check it first and answer 503 <c>vault_busy</c>.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Opens a sealed database with a master DEK one door produced (spec 4.4 steps 3-6). The caller keeps <paramref name="masterDek"/>;
    /// the gate copies what it needs and clears its copies. Already open: returns <see cref="VaultOpenResult.Done"/>.
    /// </summary>
    Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>
    /// Opens the database for one maintenance action without unlocking the session (recover-access, a CLI verb, spec 5.6); disposing the
    /// handle closes it again only if this call opened it.
    /// </summary>
    Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>After the session holds the DEK: replays the inbox and switches to <see cref="VaultState.Open"/> (spec 5.3 step 9).</summary>
    Task CompleteOpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Keeps a <see cref="VaultState.Closed"/> database closed for the seconds a file copy takes (spec 5.10, 8.5): while the lease lives,
    /// <see cref="OpenAsync"/> returns <see cref="VaultOpenRefusals.Busy"/> and <see cref="OpenTemporarilyAsync"/> throws
    /// <see cref="VaultBusyException"/>. Waits up to 30 s for an unlock in progress; throws <see cref="InvalidOperationException"/> unless the
    /// state is then Closed (the caller takes a snapshot instead). On a plain node it always throws.
    /// </summary>
    Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default);

    /// <summary>Closes the database (spec 5.4). Called on SessionService.Locked and at shutdown. Plain: nothing.</summary>
    Task CloseAsync(string reason, CancellationToken ct = default);

    /// <summary>Completes when the database can be used; at once on a plain node.</summary>
    Task WaitUsableAsync(CancellationToken ct);

    event Action<VaultState>? StateChanged;
}

/// <summary>Thrown instead of opening a sealed database that is closed. Maps to 503 <c>vault_sealed</c>.</summary>
public sealed class VaultSealedException(string message = "This node's database is encrypted and closed: unlock it first.")
    : InvalidOperationException(message);

/// <summary>The gate is held closed for a file copy (spec 5.10). Maps to 503 <c>vault_busy</c> with Retry-After.</summary>
public sealed class VaultBusyException(string message = "This node is being backed up for a few seconds: try again shortly.")
    : InvalidOperationException(message);

/// <summary>The database key does not open the database file (wrong key, foreign boot store, damaged file). Never retried as plain.</summary>
public sealed class VaultKeyMismatchException(string message) : InvalidOperationException(message);

/// <summary>Work a host runs each time a sealed database opens, before requests may use it (spec 5.3).</summary>
public interface IVaultOpenStep
{
    /// <summary>Ascending; the steps of spec 5.3 table 2 use 100, 200, ...</summary>
    int Order { get; }

    /// <param name="firstOpenInProcess">True only for the first open of this process: crash sweeps run once.</param>
    Task RunAsync(bool firstOpenInProcess, CancellationToken ct);
}
```

### A.2 `libs/BeeMemoryBank.Storage/Sealed/SealedStore.cs`

```csharp
using System.Buffers.Binary;
using System.Text;

namespace BeeMemoryBank.Storage.Sealed;

/// <summary>What a data directory holds (docs/design/step-b-spec.md 2.1).</summary>
public enum VaultStoreMode
{
    /// <summary>No boot store: a plain database (or none yet). Today's behaviour, unchanged.</summary>
    Plain,
    /// <summary>A boot store and an encrypted database, and no plain companion: closed until a door opens it.</summary>
    Sealed,
    /// <summary>One half without the other, a boot store that is not ours, or a plain companion beside a sealed pair: the node refuses to start (spec 2.5).</summary>
    Broken,
}

/// <summary>
/// The answer of <see cref="VaultStoreFiles.Diagnose"/> (spec 2.5): the mode and, for Broken, a stable reason code
/// (<c>db_without_boot</c>, <c>boot_beside_plain_db</c>, <c>boot_not_ours</c>, <c>db_missing_or_empty</c>, <c>db_not_page_aligned</c>,
/// <c>seal_leftovers_no_journal</c>, <c>plain_companion</c>, <c>companion_unreadable</c>) and the names, relative to the data
/// directory, that the reason concerns. No content of any file.
/// </summary>
public sealed record VaultStoreDiagnosis(VaultStoreMode Mode, string? Reason, IReadOnlyList<string> Files)
{
    public static VaultStoreDiagnosis Of(VaultStoreMode mode) => new(mode, null, Array.Empty<string>());

    public static VaultStoreDiagnosis Broken(string reason, params string[] files) => new(VaultStoreMode.Broken, reason, files);
}

/// <summary>What a companion file of a sealed pair is (spec 2.5).</summary>
public enum CompanionState
{
    /// <summary>The file does not exist.</summary>
    Absent,
    /// <summary>Empty, or a file that holds no plain page image (a sealed file, a WAL or journal of encrypted pages).</summary>
    Fine,
    /// <summary>A plain SQLite file, or a WAL or rollback journal with at least one plain page image.</summary>
    Plain,
    /// <summary>The file exists and cannot be read: fail closed.</summary>
    Unreadable,
}

/// <summary>File names and the start-time classification of a data directory (spec 2.1, 2.5). No key, no SQLite connection.</summary>
public static class VaultStoreFiles
{
    public const string Database = "beememorybank.db";
    public const string Chat = "chat.db";
    public const string Boot = "beememorybank.boot.db";
    public const string SealJournal = "seal-journal.json";
    /// <summary>Suffix of a migration target next to its live file (spec 10.6).</summary>
    public const string SealNewSuffix = ".seal-new";
    /// <summary>Suffix a replaced plain file carries between the swap and its wipe (spec 10.6); never survives a start.</summary>
    public const string SealOldSuffix = ".seal-old";
    /// <summary>Folder of the pre-encryption snapshot (spec 10.4 step 3, 10.7): not read by the snapshot listing or its retention.</summary>
    public const string PreEncryptionDir = "pre-encryption";

    /// <summary><c>PRAGMA application_id</c> of every boot store: "BMBS".</summary>
    public const int BootApplicationId = 0x424D4253;

    /// <summary>The files beside a sealed pair that can still hold pages in clear (spec 2.5); a <c>-shm</c> holds only the WAL index.</summary>
    public static readonly IReadOnlyList<string> Companions =
        [Chat, Database + "-wal", Database + "-journal", Chat + "-wal", Chat + "-journal"];

    private const int MaxFramesPerEnd = 1024;

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    private static ReadOnlySpan<byte> JournalMagic => [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];

    /// <summary>The mode of <see cref="Diagnose"/>. Called after the seal journal is resolved (spec 10.6) and before anything opens the database.</summary>
    public static VaultStoreMode DetectMode(string dataPath) => Diagnose(dataPath).Mode;

    /// <summary>
    /// Classifies a data directory and, for Broken, says why and which files (spec 2.5). Reads at most the five companion files and the
    /// three store files; deletes, moves and changes nothing. Called after the seal journal is resolved (spec 10.6), at every start.
    /// </summary>
    public static VaultStoreDiagnosis Diagnose(string dataPath)
    {
        // A replaced plain file that is still there, with no journal to say that its wipe is pending: refuse (spec 10.6). The resolver
        // always decides this case first; this is the backstop for a start that skipped it.
        if (HasSealOldLeftovers(dataPath) && !File.Exists(Path.Combine(dataPath, SealJournal)))
            return VaultStoreDiagnosis.Broken("seal_leftovers_no_journal", new List<string>(SealOldLeftovers(dataPath)).ToArray());

        var db = Path.Combine(dataPath, Database);
        var boot = Path.Combine(dataPath, Boot);
        var dbExists = File.Exists(db) && new FileInfo(db).Length > 0;

        if (File.Exists(boot))
        {
            if (!HasSqliteMagic(boot) || ReadApplicationId(boot) != BootApplicationId) return VaultStoreDiagnosis.Broken("boot_not_ours", Boot);
            if (!dbExists) return VaultStoreDiagnosis.Broken("db_missing_or_empty", Database);
            if (HasSqliteMagic(db)) return VaultStoreDiagnosis.Broken("boot_beside_plain_db", Boot, Database);
            // A sealed file is whole 4096-byte pages: a length that is not a multiple means a copy cut inside a page (spec 8.7.2).
            if (new FileInfo(db).Length % SqlCipherScheme.PageSize != 0) return VaultStoreDiagnosis.Broken("db_not_page_aligned", Database);

            // A sealed pair is Sealed only when nothing beside it holds pages in clear (spec 2.5, every start).
            List<string> plain = [], unreadable = [];
            foreach (var name in Companions)
            {
                switch (InspectCompanion(Path.Combine(dataPath, name)))
                {
                    case CompanionState.Plain: plain.Add(name); break;
                    case CompanionState.Unreadable: unreadable.Add(name); break;
                }
            }
            if (plain.Count > 0) return VaultStoreDiagnosis.Broken("plain_companion", plain.ToArray());
            if (unreadable.Count > 0) return VaultStoreDiagnosis.Broken("companion_unreadable", unreadable.ToArray());
            return VaultStoreDiagnosis.Of(VaultStoreMode.Sealed);
        }

        // An encrypted database without its boot store cannot be opened by anyone: refuse rather than start empty beside it.
        return dbExists && !HasSqliteMagic(db)
            ? VaultStoreDiagnosis.Broken("db_without_boot", Database)
            : VaultStoreDiagnosis.Of(VaultStoreMode.Plain);
    }

    /// <summary>True when a file named <c>beememorybank.db.seal-old*</c> or <c>chat.db.seal-old*</c> (a replaced plain file or its sidecar) is in the directory.</summary>
    public static bool HasSealOldLeftovers(string dataPath)
    {
        foreach (var _ in SealOldLeftovers(dataPath)) return true;
        return false;
    }

    /// <summary>The names of those files, relative to the data directory.</summary>
    public static IEnumerable<string> SealOldLeftovers(string dataPath)
    {
        if (!Directory.Exists(dataPath)) yield break;
        foreach (var pattern in new[] { Database + SealOldSuffix + "*", Chat + SealOldSuffix + "*" })
            foreach (var file in Directory.EnumerateFiles(dataPath, pattern))
                yield return Path.GetFileName(file);
    }

    /// <summary>
    /// Whether a companion file of a sealed pair holds pages in clear (spec 2.5). A plain <c>chat.db</c> starts with the SQLite magic. A
    /// WAL or rollback journal of a SEALED file has a plain header and encrypted page images (spec Appendix C), so only its page images
    /// count: the file is Plain when any sampled frame (record) is a plain page image - the SQLite magic on page 1 or a valid b-tree page
    /// header (<see cref="LooksLikePlainPage"/>). Empty and unrecognised files are Fine (SQLite ignores them); an unreadable file is not.
    /// </summary>
    public static CompanionState InspectCompanion(string path)
    {
        try
        {
            if (!File.Exists(path)) return CompanionState.Absent;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return CompanionState.Fine;
            Span<byte> head = stackalloc byte[32];
            var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n >= 16 && head[..16].SequenceEqual(SqliteMagic)) return CompanionState.Plain;

            if (n >= 32 && IsWalHeader(head, out var walPageSize))
                return HoldsPlainPage(fs, first: 32, prefix: 24, pageSize: walPageSize, suffix: 0) ? CompanionState.Plain : CompanionState.Fine;

            if (n >= 28 && head[..8].SequenceEqual(JournalMagic))
            {
                var sector = BinaryPrimitives.ReadUInt32BigEndian(head[20..]);
                var page = BinaryPrimitives.ReadUInt32BigEndian(head[24..]);
                if (IsPageSize(sector) && IsPageSize(page))
                    return HoldsPlainPage(fs, first: sector, prefix: 4, pageSize: (int)page, suffix: 4) ? CompanionState.Plain : CompanionState.Fine;
            }
            return CompanionState.Fine;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return CompanionState.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CompanionState.Unreadable;
        }
    }

    private static bool IsPageSize(uint v) => v is >= 512 and <= 65536 && (v & (v - 1)) == 0;

    /// <summary>A WAL header: magic 0x377F0682 or 0x377F0683, version 3007000, a power-of-two page size from 512 to 65536.</summary>
    private static bool IsWalHeader(ReadOnlySpan<byte> h, out int pageSize)
    {
        var magic = BinaryPrimitives.ReadUInt32BigEndian(h);
        var page = BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
        pageSize = (int)page;
        return (magic == 0x377F0682 || magic == 0x377F0683) && BinaryPrimitives.ReadUInt32BigEndian(h[4..]) == 3007000 && IsPageSize(page);
    }

    /// <summary>
    /// Reads the complete frames (records) of a WAL (journal) - at most the first <see cref="MaxFramesPerEnd"/> and the last
    /// <see cref="MaxFramesPerEnd"/> - and reports whether any holds a plain page image. A record is <c>prefix + pageSize + suffix</c>
    /// bytes from <paramref name="first"/>; its first four bytes are the page number.
    /// </summary>
    private static bool HoldsPlainPage(FileStream fs, long first, int prefix, int pageSize, int suffix)
    {
        long record = (long)prefix + pageSize + suffix;
        var count = (fs.Length - first) / record;
        if (count <= 0) return false;
        var buffer = new byte[prefix + pageSize];
        for (var i = 0L; i < count; i++)
        {
            if (i == MaxFramesPerEnd && count - i > MaxFramesPerEnd) i = count - MaxFramesPerEnd;
            fs.Seek(first + i * record, SeekOrigin.Begin);
            if (fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length) break;
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (LooksLikePlainPage(buffer.AsSpan(prefix, pageSize), pageNumber == 1)) return true;
        }
        return false;
    }

    /// <summary>
    /// The plain page test (spec 2.5): a page image is plain when it starts with the SQLite magic (page 1) or has a valid b-tree page header -
    /// the type byte 2, 5, 10 or 13 at offset 0 (offset 100 on page 1), at most 60 fragmented bytes, the cell pointer array ending at or before
    /// the cell content start, the content start inside the page, the first free block 0 or inside the page, and every cell pointer between the
    /// end of the pointer array and the page size minus 4. Every table or index page passes; a ciphertext page passes with a probability of about 2^-32.
    /// </summary>
    public static bool LooksLikePlainPage(ReadOnlySpan<byte> page, bool firstPageOfDatabase)
    {
        if (firstPageOfDatabase && page.Length >= 16 && page[..16].SequenceEqual(SqliteMagic)) return true;
        var o = firstPageOfDatabase ? 100 : 0;
        if (page.Length < o + 14) return false;

        int header;
        switch (page[o])
        {
            case 0x0A or 0x0D: header = 8; break;
            case 0x02 or 0x05: header = 12; break;
            default: return false;
        }
        if (page[o + 7] > 60) return false;

        int freeBlock = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 1)..]);
        int cells = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 3)..]);
        int content = BinaryPrimitives.ReadUInt16BigEndian(page[(o + 5)..]);
        if (content == 0) content = 65536;

        var pointersEnd = o + header + 2 * cells;
        var last = page.Length - 4;
        if (pointersEnd > content || content > page.Length) return false;
        if (freeBlock != 0 && (freeBlock < pointersEnd || freeBlock > last)) return false;
        for (var i = 0; i < cells; i++)
        {
            int pointer = BinaryPrimitives.ReadUInt16BigEndian(page[(o + header + 2 * i)..]);
            if (pointer < pointersEnd || pointer > last) return false;
        }
        return true;
    }

    public static bool HasSqliteMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            return fs.Read(header) == header.Length && header.SequenceEqual(SqliteMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Header bytes 68-71 (big-endian) of a plain SQLite file, or 0 when unreadable.</summary>
    public static int ReadApplicationId(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[72];
            return fs.Read(header) == header.Length ? BinaryPrimitives.ReadInt32BigEndian(header[68..]) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>The two encrypted files of a sealed node, each with its own page key (spec 3.2).</summary>
public static class DbPurposes
{
    public const string Vault = "vault";
    public const string Chat = "chat";
}

/// <summary>
/// The page keys of an open sealed vault (spec 3.2, 7.1). Implemented by the vault gate, read by the connection factories.
/// Absent (null) on a blind host and in tests that do not seal: the factories then behave exactly as before Step B.
/// </summary>
public interface IVaultKeySource
{
    VaultStoreMode Mode { get; }

    /// <summary>True while the page keys are in memory.</summary>
    bool IsOpen { get; }

    /// <summary>Changes on every open; a factory keeps no pooled handle across a change.</summary>
    long OpenGeneration { get; }

    /// <summary>A copy of the 32-byte page key of <paramref name="purpose"/>; the caller clears it. Throws <c>VaultSealedException</c> when closed.</summary>
    byte[] GetPageKey(string purpose);
}

/// <summary>
/// The cipher recipe (spec 7.1), checked against SQLite3MC 2.4.0 (spec Appendix C): SQLCipher 4 page format chosen per connection
/// through the URI (sqlite3mc_config('default:...') only changes the connection it runs on), the key given as SQLCipher raw-key
/// syntax, never as a passphrase. Every string these helpers return that contains a key must never be logged or put in an exception.
/// </summary>
public static class SqlCipherScheme
{
    public const int PageSize = 4096;
    public const int ReservedBytes = 80;

    /// <summary>The Data Source of a keyed connection (no key in it: safe as a cache key or in a log).</summary>
    public static string DataSource(string path) => "file://" + ToUriPath(path) + "?cipher=sqlcipher&legacy=4";

    /// <summary>The Password of a keyed connection: <c>x'&lt;64 hex&gt;'</c>, which Microsoft.Data.Sqlite sends as <c>PRAGMA key</c>.</summary>
    public static string Password(ReadOnlySpan<byte> pageKey) => "x'" + Convert.ToHexStringLower(pageKey) + "'";

    /// <summary>
    /// The quoted SQL target of <c>VACUUM INTO</c> that writes a copy encrypted with <paramref name="pageKey"/> (spec 7.4). Contains the key.
    /// </summary>
    public static string VacuumIntoTarget(string path, ReadOnlySpan<byte> pageKey) =>
        "'file://" + ToUriPath(path).Replace("'", "''") + "?cipher=sqlcipher&legacy=4&key=x''" + Convert.ToHexStringLower(pageKey) + "'''";

    /// <summary>An absolute local path as the path part of a <c>file:</c> URI: forward slashes, a leading slash, %, ?, #, &amp; and space escaped.</summary>
    public static string ToUriPath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.StartsWith("//", StringComparison.Ordinal))
            throw new NotSupportedException("A sealed database cannot live on a network share (UNC path).");
        var sb = new StringBuilder(full.Length + 8);
        if (!full.StartsWith('/')) sb.Append('/');
        foreach (var c in full)
        {
            sb.Append(c switch
            {
                '%' => "%25",
                '?' => "%3F",
                '#' => "%23",
                '&' => "%26",
                ' ' => "%20",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
```

### A.3 `libs/BeeMemoryBank.Storage/Boot/BootContracts.cs`

```csharp
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>Keys of <c>tbl_boot_meta</c> (docs/design/step-b-spec.md Appendix B). Values are text.</summary>
public static class BootMetaKeys
{
    public const string Format = "format";                       // "1"
    public const string NodeId = "node_id";                      // this node's id, "D" format
    public const string DbkGeneration = "dbk_generation";        // generation of the database key in use (spec 3.1)
    public const string SealedAt = "sealed_at";                  // UTC "o" of the migration
    public const string Activated = "activated";                 // "1": Step A applied here (always on a sealed node)
    public const string SeqFloor = "seq_floor";                  // first relay sequence the inbox may use (spec 6.3)
    public const string SealedHead = "sealed_head";              // tbl_event head at the last clean close, or ""
    public const string CleanClose = "clean_close";              // "1" after a clean close, "0" while open
    public const string UnlockMaterialVersion = "unlock_material_version"; // last mirrored value (spec 4.3)
    public const string LastRefusal = "last_refusal";                       // code of the last refused unlock (spec 4.4), or ""
    public const string ServerAutoUnlock = "server_auto_unlock";           // "1" while the server checkbox is on (spec 11.2)
}

public static class BootDoorKinds
{
    public const string Slot = "slot";
    public const string Agent = "agent";
}

/// <summary>
/// One way to obtain the master DEK before unlock (spec 4.1): a copy of a key slot or of an auto-unlock agent's wrapped DEK. Never a
/// user name, an agent name or a description.
/// </summary>
public sealed record BootDoor(
    long DoorId,
    string Kind,
    long SourceId,
    string? SlotType,
    byte[] EncryptedMasterDek,
    byte[] Iv,
    byte[]? Salt,
    int? ArgonMemory,
    int? ArgonIterations,
    int? ArgonParallelism,
    string? KeyHash,
    int? KdfVersion,
    string ContentSha256,
    string? DekFingerprint,
    bool Pending);

/// <summary>The database key wrapped under one master DEK (spec 3.3).</summary>
public sealed record BootDbKeyRow(string DekFingerprint, int DbkGeneration, byte[] Wrapped, byte[] Iv, DateTime CreatedAt);

/// <summary>The public part of this node's identity, for the relay routes while closed (spec 6.1).</summary>
public sealed record BootIdentity(Guid NodeId, string DisplayName, byte[] Ed25519PublicKey);

/// <summary>An event received while the database was closed, as stored and served (spec 6.2).</summary>
public sealed record RelayEventRow(long RelaySeq, SyncEvent Event, Guid? OriginPeer, long? OriginSeq, DateTime ReceivedAt);

public sealed record RelayInboxSummary(long Events, long EventBytes, long Blobs, long BlobBytes, DateTime? OldestReceivedAt, long MaxRelaySeq);

/// <summary>The boot store file itself: created, migrated, its meta rows.</summary>
public interface IBootStore
{
    string FilePath { get; }

    bool Exists { get; }

    /// <summary>Creates the file if needed (application id, WAL) and runs the boot migrations (own ledger, spec 2.3).</summary>
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task<string?> GetMetaAsync(string key, CancellationToken ct = default);

    Task SetMetaAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>The doors. Read by the unlock, written only through the mirror (spec 4.3).</summary>
public interface IBootDoorStore
{
    Task<IReadOnlyList<BootDoor>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BootDoor>> FindAgentAsync(string keyHash, CancellationToken ct = default);

    /// <summary>Records which master DEK a door yielded (spec 3.4); called by the unlock that used it.</summary>
    Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default);
}

/// <summary>The DBK wrappers.</summary>
public interface IBootDbKeyStore
{
    Task<BootDbKeyRow?> FindAsync(string dekFingerprint, int dbkGeneration, CancellationToken ct = default);

    Task<IReadOnlyList<BootDbKeyRow>> ListAsync(CancellationToken ct = default);
}

/// <summary>The identity and whitelist mirrors the relay authenticates with while closed (spec 2.2, 6.4).</summary>
public interface IBootMirrorStore
{
    Task<BootIdentity?> GetIdentityAsync(CancellationToken ct = default);

    /// <summary>An active (status 'A') mirror row, or null.</summary>
    Task<(byte[] PublicKey, bool IsSuperadmin)?> GetActivePeerAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>Marks a mirror row 'S' (suspended until the next rewrite) after a verified revoke or update received while closed.</summary>
    Task SuspendAsync(Guid nodeId, CancellationToken ct = default);
}

/// <summary>Events, blobs and position reports received while the database is closed (spec 6).</summary>
public interface IRelayInbox
{
    /// <summary>Stores the event unless its id is already stored; returns its relay sequence (the stored one when it was there).</summary>
    Task<long> AppendAsync(SyncEvent evt, Guid? originPeer, long? originSeq, CancellationToken ct = default);

    Task<IReadOnlyList<RelayEventRow>> ReadAfterAsync(long afterRelaySeq, int limit, CancellationToken ct = default);

    Task DeleteAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default);

    Task<RelayInboxSummary> GetSummaryAsync(CancellationToken ct = default);

    Task StoreBlobAsync(string hash, byte[] ciphertext, CancellationToken ct = default);

    Task<byte[]?> GetBlobAsync(string hash, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListBlobHashesAsync(CancellationToken ct = default);

    Task DeleteBlobsAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default);

    Task RecordReportAsync(Guid peer, long sequence, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid Peer, long Sequence)>> ListReportsAsync(CancellationToken ct = default);

    Task ClearReportsAsync(CancellationToken ct = default);
}
```

### A.4 `libs/BeeMemoryBank.Vault/Sealed/SealedContracts.cs`

```csharp
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>A door about to be written to the sealed database (docs/design/step-b-spec.md 4.3 rule 1). Same fields as BootDoor.</summary>
public sealed record PendingDoor(
    string Kind,
    long SourceId,
    string? SlotType,
    byte[] EncryptedMasterDek,
    byte[] Iv,
    byte[]? Salt,
    int? ArgonMemory,
    int? ArgonIterations,
    int? ArgonParallelism,
    string? KeyHash,
    int? KdfVersion,
    string? DekFingerprint);

/// <summary>
/// Keeps the boot store's copies (doors, DBK wrappers, identity and whitelist mirrors) in step with the sealed database (spec 4.3).
/// Every member is a no-op on a plain node.
/// </summary>
public interface IUnlockMaterialMirror
{
    /// <summary>Rewrites every copy from the open database in one boot transaction and prunes DBK wrappers (spec 3.4, 4.3 rule 3).</summary>
    Task RewriteAsync(CancellationToken ct = default);

    /// <summary>Before a commit that adds or replaces a door (rule 1).</summary>
    Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default);

    /// <summary>Before a commit that removes a door (rule 2).</summary>
    Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default);

    /// <summary>Before a DEK rotation or a restore commits: a DBK wrapper under the master DEK that will be current (spec 3.5).</summary>
    Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>
    /// Writes a complete boot store for a database that is not the live one yet (migration target, re-key target, spec 10.4):
    /// identity, whitelist, doors read from <paramref name="vault"/>, the DBK wrapped under every key of <paramref name="masterDeks"/>.
    /// </summary>
    Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
        IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default);
}

/// <summary>The database key (spec 3).</summary>
public interface IDatabaseKeyService
{
    /// <summary>A fresh 32-byte DBK from the CSPRNG; the caller clears it.</summary>
    byte[] Create();

    /// <summary>The DBK of the current generation through the boot wrapper of <paramref name="masterDek"/>'s fingerprint; null when none opens.</summary>
    Task<byte[]?> TryUnwrapAsync(byte[] masterDek, CancellationToken ct = default);

    /// <summary>HKDF-SHA256 page key of one file (spec 3.2); the caller clears it.</summary>
    byte[] DerivePageKey(ReadOnlySpan<byte> databaseKey, string purpose);
}

/// <summary>The open vault's database key, for the mirror's wrappers (implemented by the gate).</summary>
public interface ISealedVaultKeys
{
    /// <summary>A copy of the DBK; throws VaultSealedException when closed. The caller clears it.</summary>
    byte[] GetDatabaseKey();

    int DbkGeneration { get; }
}

public sealed record RelayImportResult(int Applied, int Held, int AlreadyPresent, int Refused, int Waiting);

/// <summary>The relay inbox's part of the open and close sequences (spec 5.3, 5.4, 6.3, 6.6). Called by the gate.</summary>
public interface ISealedRelayLifecycle
{
    /// <summary>Raises tbl_event's sequence above every relay sequence and sets the open floor (spec 6.3). Before anything appends.</summary>
    Task OnOpenedAsync(CancellationToken ct = default);

    /// <summary>Replays the inbox in relay order through EventApplier (spec 6.6); safe to call again; removes what it applied.</summary>
    Task<RelayImportResult> ImportAsync(CancellationToken ct = default);

    /// <summary>Records the clean-close head and floor (spec 6.3), while the database can still be read.</summary>
    Task OnClosingAsync(CancellationToken ct = default);

    /// <summary>
    /// Pauses every write of the relay (receipts, position reports, blobs) for the seconds a file copy takes (spec 6.8): waits for the
    /// receipts in flight, then the write routes answer 503 <c>vault_sealed_busy</c> until the lease is disposed. Granted only while the
    /// gate is Closed; otherwise throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<IAsyncDisposable> PauseWritesAsync(CancellationToken ct = default);
}
```

### A.5 `server/BeeMemoryBank.Api/Services/Sealed/ISealedRelay.cs`

```csharp
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// The sync routes a sealed node answers from its boot store while the database is closed or importing (docs/design/step-b-spec.md 6.1).
/// The sealed-vault middleware hands such requests to it instead of the endpoints of SyncEndpoints.cs.
/// </summary>
public interface ISealedRelay
{
    /// <summary>True for a path and method of the relay set (spec 6.1 table).</summary>
    bool Handles(PathString path, string method);

    /// <summary>Answers the request completely (status, body). Never opens the sealed database.</summary>
    Task HandleAsync(HttpContext context);
}
```

### A.6 `libs/BeeMemoryBank.AppPaths/SecureFileWipe.cs`

```csharp
using System.Security.Cryptography;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// Best-effort removal of a file that holds plain vault data (docs/design/step-b-spec.md 10.6): overwrite in place with random
/// bytes, flush to disk, truncate, delete. SSDs with wear levelling and copy-on-write or journaling file systems may keep earlier
/// blocks; that remnant is documented (spec 2.1), this only removes the file and its readable bytes. A file that has another name
/// is never overwritten (spec 10.2): its bytes belong to that name too.
/// </summary>
public static class SecureFileWipe
{
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// Wipes <paramref name="path"/>. Returns true when the file is gone (or never existed), false when it could not be removed
    /// (locked, read-only volume, access denied): the caller keeps its journal and retries at the next start. A symbolic link or
    /// reparse point is removed without ever writing through it.
    /// </summary>
    /// <param name="afterOverwrite">Test seam: called after the content is overwritten and flushed, before the file is truncated.</param>
    /// <param name="sharedWithAnotherName">
    /// Asked once for a regular file before anything is written: true when the file has another name (a link count above one, or the
    /// identity of a live file). The file is then NOT overwritten - only this name is removed and true is returned; the bytes live on
    /// under the other name, which is the caller's to report. An exception thrown by the callback propagates (the caller decides what an
    /// unreadable identity means). Null: the file is overwritten.
    /// </param>
    public static bool TryWipe(string path, Action<string>? afterOverwrite = null, Func<string, bool>? sharedWithAnotherName = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                info.Delete();
                return true;
            }
            if (!info.Exists) return true;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
                return true;
            }
            if (sharedWithAnotherName?.Invoke(path) == true)
            {
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                return true;
            }
            if (info.IsReadOnly) info.IsReadOnly = false;

            // FileShare.Read: no other writer, but the test seam below can read the overwritten bytes.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
            {
                var length = stream.Length;
                var block = new byte[BlockSize];
                for (long written = 0; written < length; written += block.Length)
                {
                    RandomNumberGenerator.Fill(block);
                    stream.Write(block, 0, (int)Math.Min(block.Length, length - written));
                }
                stream.Flush(true);
                afterOverwrite?.Invoke(path);
                stream.SetLength(0);
                stream.Flush(true);
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
```

## Appendix B: boot store schema and migration 040 (verbatim)

### B.1 `libs/BeeMemoryBank.Storage/BootMigrations/B001_boot_store.sql`

```sql
-- The boot store of a sealed node (docs/design/step-b-spec.md 2.3): the one plain file a sealed node reads before unlock.
-- Every table and column is on the allow-list docs/design/boot-store.golden.txt. It holds wrapped keys exactly as exposed as
-- today's key slots, the database-key wrappers, the public identity, the whitelist (peer names, addresses and TLS pins: the
-- residual ADR 0009 accepts) and what arrived while the database was closed (sealed or control events, ciphertext blobs).
-- Never: a user name, a password hash, an agent name, a title, a path, a tag, a decrypted payload, a clear content event.

CREATE TABLE IF NOT EXISTS tbl_boot_meta (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

-- Doors (spec 4.1): copies of tbl_key_slot rows and of the wrapped DEK of auto-unlock agents. Derived; rewritten from the
-- sealed database (spec 4.3). Several rows per source while a replacement is pending.
CREATE TABLE IF NOT EXISTS tbl_boot_door (
    door_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    kind                 TEXT    NOT NULL CHECK (kind IN ('slot', 'agent')),
    source_id            INTEGER NOT NULL,
    slot_type            TEXT,
    encrypted_master_dek BLOB    NOT NULL,
    iv                   BLOB    NOT NULL,
    salt                 BLOB,
    argon_memory         INTEGER,
    argon_iterations     INTEGER,
    argon_parallelism    INTEGER,
    key_hash             TEXT,
    kdf_version          INTEGER,
    content_sha256       TEXT    NOT NULL,
    dek_fp               TEXT,
    pending              INTEGER NOT NULL DEFAULT 0,
    written_at           TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_boot_door ON tbl_boot_door (kind, source_id, content_sha256);
CREATE INDEX IF NOT EXISTS ix_boot_door_key_hash ON tbl_boot_door (key_hash) WHERE key_hash IS NOT NULL;

-- The database key wrapped under one master DEK per row (spec 3.3).
CREATE TABLE IF NOT EXISTS tbl_boot_db_key (
    dek_fp         TEXT    NOT NULL,
    dbk_generation INTEGER NOT NULL,
    wrapped        BLOB    NOT NULL,
    iv             BLOB    NOT NULL,
    created_at     TEXT    NOT NULL,
    PRIMARY KEY (dek_fp, dbk_generation)
);

-- The public part of this node's identity, for /api/sync/identity while closed (spec 6.1).
CREATE TABLE IF NOT EXISTS tbl_boot_identity (
    node_id            TEXT NOT NULL PRIMARY KEY,
    display_name       TEXT NOT NULL,
    ed25519_public_key BLOB NOT NULL,
    updated_at         TEXT NOT NULL
);

-- Mirror of the vault's tbl_whitelist, same columns (a test keeps them equal), so WhitelistRepository reads it unchanged.
-- Status 'S' exists only here: suspended by a revoke or update received while closed (spec 6.4).
CREATE TABLE IF NOT EXISTS tbl_whitelist (
    node_id                   TEXT PRIMARY KEY,
    display_name              TEXT NOT NULL,
    ed25519_public_key        BLOB NOT NULL,
    api_address               TEXT,
    can_generate_embeddings   INTEGER NOT NULL DEFAULT 0,
    status                    TEXT NOT NULL DEFAULT 'A',
    auto_accept_restore       INTEGER NOT NULL DEFAULT 0,
    auto_accept_dek_rotation  INTEGER NOT NULL DEFAULT 0,
    is_superadmin             INTEGER NOT NULL DEFAULT 0,
    created_at                TEXT NOT NULL,
    updated_at                TEXT NOT NULL,
    deleted_at                TEXT,
    lamport_ts                INTEGER NOT NULL DEFAULT 0,
    source_node_id            TEXT,
    last_protocol_version     INTEGER,
    last_protocol_seen_at     TEXT,
    tls_spki                  TEXT,
    tls_trust                 TEXT,
    last_capabilities         TEXT,
    last_app_version          TEXT,
    last_capabilities_seen_at TEXT
);

-- Events received while the database was closed (spec 6.2), served with this node's sequence numbers (spec 6.3) and replayed
-- at open (spec 6.6). Only 'sealed' and control events: a clear content event is refused before it gets here.
CREATE TABLE IF NOT EXISTS tbl_relay_event (
    relay_seq        INTEGER PRIMARY KEY,
    event_id         TEXT    NOT NULL UNIQUE,
    node_id          TEXT    NOT NULL,
    lamport_ts       INTEGER NOT NULL,
    event_type       TEXT    NOT NULL,
    article_id       TEXT,
    payload          TEXT    NOT NULL,
    signature        BLOB    NOT NULL,
    protocol_version INTEGER NOT NULL,
    created_at       TEXT    NOT NULL,
    origin_peer      TEXT,
    origin_seq       INTEGER,
    received_at      TEXT    NOT NULL,
    bytes            INTEGER NOT NULL
);

-- Ciphertext blobs received while closed (spec 6.5), moved into tbl_blob at open.
CREATE TABLE IF NOT EXISTS tbl_relay_blob (
    hash        TEXT    NOT NULL PRIMARY KEY,
    data        BLOB    NOT NULL,
    size        INTEGER NOT NULL,
    received_at TEXT    NOT NULL
);

-- Positions peers reported on this node's log while closed (spec 6.1), merged at open.
CREATE TABLE IF NOT EXISTS tbl_relay_report (
    remote_node_id TEXT    NOT NULL PRIMARY KEY,
    reported_seq   INTEGER NOT NULL,
    updated_at     TEXT    NOT NULL
);
```

The boot runner creates its ledger itself before the first migration:
`CREATE TABLE IF NOT EXISTS tbl_boot_migration (version INTEGER PRIMARY KEY, filename TEXT NOT NULL, applied_at TEXT NOT NULL)`,
and sets `PRAGMA application_id = 1112359507` (`0x424D4253`) and `PRAGMA journal_mode = WAL` when it creates the file.

### B.2 `libs/BeeMemoryBank.Storage/Migrations/040_sealed_unlock_material.sql`

```sql
-- Step B (docs/design/step-b-spec.md 4.3): a version number of everything the boot store of a sealed node copies - the unlock
-- material (key slots, the wrapped DEK of agents, the users that may unlock), the whitelist columns the relay authenticates
-- with, the public identity. Each change bumps it inside the writing transaction, so the boot store's mirror sees that it is
-- behind even when a writer forgot to tell it. No new table: the counter is a row of tbl_migration_marker. Plain nodes and
-- blind hosts carry the triggers unused.

INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
VALUES ('unlock_material_version', '0', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_insert AFTER INSERT ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_update AFTER UPDATE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_delete AFTER DELETE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_agent_insert AFTER INSERT ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Not last_accessed_at or request_count: those change on every agent request.
CREATE TRIGGER IF NOT EXISTS trg_umv_agent_update
AFTER UPDATE OF encrypted_dek, dek_iv, salt, kdf_version, key_hash, status, owner_user_id ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_agent_delete AFTER DELETE ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Who may unlock depends on the owner's role and state (the superadmin rule of the unlock).
CREATE TRIGGER IF NOT EXISTS trg_umv_user_update AFTER UPDATE OF role, is_active, key_slot_id ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_user_delete AFTER DELETE ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_insert AFTER INSERT ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Not the protocol, capability and version observations: those change on every authentication.
CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_update
AFTER UPDATE OF display_name, ed25519_public_key, api_address, status, is_superadmin, deleted_at, tls_spki, tls_trust ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_delete AFTER DELETE ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_identity_insert AFTER INSERT ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_identity_update AFTER UPDATE OF node_id, display_name, ed25519_public_key ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;
```

## Appendix C: the SQLCipher 4 page format as SQLite3MC writes it (verified 2026-10-09)

Checked with `Microsoft.Data.Sqlite.Core` 10.0.11, `SQLite3MC.PCLRaw.bundle` 2.4.0 (SQLite 3.53.4), `SQLitePCLRaw.core` 3.0.2,
in a throwaway console program: a C# reader decrypted every page of
SQLite3MC-written files with zero HMAC failures (header fields page size 4096, reserved bytes 80), the image passed
`integrity_check` and FTS5 queries after `sqlite3_deserialize`; a C# writer produced a file SQLite3MC opened, checked and wrote to.

For a database written with `cipher=sqlcipher&legacy=4` and a raw 32-byte key K:

- page size 4096; reserved bytes per page 80 = IV (16) + HMAC (64);
- file bytes 0-15: a random salt S (the plaintext `SQLite format 3\0` is not stored);
- HMAC key `Kh = PBKDF2-HMAC-SHA512(password = K, salt = S XOR 0x3a (each byte), iterations = 2, length = 32)`; no KDF for K itself;
- page p (1-based), byte range `[o, 4016)` with `o = 16` for page 1 and `o = 0` otherwise: `C = AES-256-CBC(K, IV, plaintext)`, no
  padding; bytes `[4016, 4032)` = IV (random per write); bytes `[4032, 4096)` = `HMAC-SHA512(Kh, C || IV || uint32_le(p))`;
- a plain image is rebuilt as: page 1 bytes 0-15 = `SQLite format 3\0`, decrypted bytes in `[o, 4016)`, the reserved 80 bytes zeroed;
  header bytes 18 and 19 set to 1 before an in-memory open (an image cannot be in WAL mode); header byte 20 (reserved bytes) stays 80,
  which stock SQLite accepts;
- the per-connection cipher choice: `sqlite3mc_config('default:cipher', ...)` run as SQL changes only that connection; the URI
  parameters `cipher=sqlcipher&legacy=4` with `Password=x'<hex>'` select it for every new physical connection (pooled reuse keeps it);
  a wrong key fails in `Open()` with SQLite error 26;
- `VACUUM INTO 'file:...?cipher=sqlcipher&legacy=4&key=x''<hex>'''` writes a copy under another raw key; `hexkey=` is a hex
  passphrase (the KDF is applied), not a raw key; `VACUUM INTO` without key parameters from a keyed connection writes a copy under the
  same key, which `ATTACH` without `KEY` opens in a keyed connection; `ATTACH ... KEY ''` opens a plain file; a plain file attached
  without `KEY` fails with error 26;
- `PRAGMA compile_options` of the bundle: `SECURE_DELETE`, `TEMP_STORE=2`, `USE_URI`; no `ENABLE_DBPAGE_VTAB` (so the page codec, not
  `sqlite_dbpage`, produces plain images); a keyed connection reports `temp_store = 0` (default, i.e. memory); `secure_delete = 1`;
- `SQLitePCLRaw.core` 3.0.2 exposes `raw.sqlite3_serialize(db, schema, out size, flags)` and
  `raw.sqlite3_deserialize(db, schema, ptr, size, maxSize, flags)`; a `VACUUM INTO` of a keyed database into a `memdb` target yields a
  plain image (reserved bytes 80) that `sqlite3_serialize` returns.
- **The WAL and the rollback journal of a sealed file** (checked with the same packages, in a throwaway console program,
  with connections left open so the files exist): a sealed
  `-wal` begins with the **plain** 32-byte WAL header (`37 7F 06 82`, version 3007000 = `00 2D E2 18`, page size `00 00 10 00`, salts,
  checksums) and every frame has a **plain** 24-byte frame header (page number, database size, salts, checksums) followed by the 4096-byte
  page image **encrypted** (the first frame's page starts with random bytes, the WAL of 45 frames, 185,432 bytes, contains neither
  `SQLite format 3` nor any inserted text); a sealed rollback journal (DELETE mode, a transaction left open) begins with the plain
  journal magic `D9 D5 05 F9 20 A1 63 D7`, sector size 512, page size 4096, and holds its page images encrypted (55,840 bytes, no
  inserted text). A plain WAL and a plain journal of the same shape hold the pages in clear (page 1 starts with `SQLite format 3`,
  the others with a b-tree type byte `0D`, `05`, ...). So the **magic of a WAL or journal does not tell sealed from plain**; the page
  images do (spec 2.5, the plain page test). `-shm` files hold only the WAL index.
