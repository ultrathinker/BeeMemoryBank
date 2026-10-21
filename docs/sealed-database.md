# The encrypted database ("Encrypt this node")

Bee Memory Bank 2.6.0 can encrypt the database files of a node, so that a stolen disk, a copied data folder or a
backup of it shows nothing. This guide is for the person who runs the node: what encryption protects, how to turn
it on, how to take it back, how to back up and restore an encrypted node, and what to do when a node refuses to
start. The design behind it is `docs/design/step-b-spec.md`; the file-level details of the engine are in
`docs/sqlite-engine.md`, the key hierarchy in `docs/encryption.md`.

**A node that is set up is encrypted from its first day** (2.6.2). The Web setup, `bmb init`, a Docker start set up
through either, `bmb join` and the restore wizard all end with an encrypted database under the master password you
gave, with metadata encryption already on (sync protocol 4). What follows about "Encrypt this node" is for a node that
was set up by an earlier version and is still open: **no update or start encrypts it for you.** You do it, once per
node, on purpose. A restore never produces an open node, and a new node joins only a node that is on protocol 4.

**A phone is not a node of that kind.** Everything above is about a server or desktop node. Setting up or joining on a
phone (the Android app, the iPhone app) does **not** encrypt the phone's database: a fresh or joined phone is **open**
until its owner runs "Encrypt this phone's database" (Security page on Android, Settings on iPhone; see "Phones").
Until then the notes the phone holds, and the names of its folders, tags and peers, are in a readable database file in
the app's private storage. The setup and join of a phone are deliberately unchanged by this guide; whether they should
end encrypted is the owner's decision, not something an update makes for you.

## What it protects, and what it does not

After "Encrypt this node":

- `beememorybank.db` (with its `-wal` and `-shm`) and `chat.db` are encrypted page by page (SQLCipher 4 format, the
  SQLite3 Multiple Ciphers engine). No table name, title, path, tag, user name, agent name, audit line, activity
  row, remote account or chat title is readable in any file the node writes: not in the database, not in a staging
  copy, not in a snapshot.
- A node that is **locked** holds no key in memory: the master key, the database key and the page keys exist only
  while the database is open. The node starts locked and stays closed until a door opens it (see "Unlocking").
- **No plain copy of the database is left behind.** The two plain files the migration replaced are overwritten and
  deleted in the same pass, and the migration refuses to start while another plain copy of your vault lies in the
  data folder (see "Before you start").

What encryption does **not** protect (the residual, so that nobody is surprised):

- File sizes and counts. The media `*.enc` files and the search-index segments were already encrypted; their sizes
  are visible.
- The `beememorybank.boot.db` boot store is a plain file by design. It holds only what a locked node needs to answer
  its peers: wrapped keys (as exposed as the key slots always were: Argon2id-wrapped), agent key hashes, the node id,
  display name and public key, the list of peers (their display names, addresses and TLS pins), and the sealed events
  and blobs received while locked. No note, title or path is in it.
- **Free-space remnants.** The plain files from before the migration are overwritten before they are deleted, but
  an SSD with wear levelling, a copy-on-write or journalling file system may keep old blocks. Encrypt the volume as
  well (BitLocker, LUKS, FileVault) if that matters to you.
- Anything you turn on that puts a key next to the data: **auto-unlock** (see below). Then a stolen disk opens
  everything.
- Process memory, swap, hibernation files and crash dumps of an **unlocked** node.
- Plaintext copies you made or asked for: a ZIP export, a download, a cold backup kept outside the data folder, a
  hard link or copy of the database under another name outside the data folder (the node sees the link count of its
  own files and refuses to start the migration while it is above one, but it cannot see where the other name is).
- **There is no way back from the node.** Encrypting keeps no copy of the unencrypted database, in the data folder or
  anywhere else (there is no "way-back snapshot" since 2.6.2): a copy that opens with a password you may later change
  is a copy that must be looked after, and the node does not keep one for you. Take your own backup first if you may
  want to go back.

## Before you start: the preflight

Open **Admin > Database encryption** (`/VaultEncryption`), or run `bmb vault seal` (it calls the running node and
asks for the master password). Once the database is encrypted the same card and page are called **Storage protection**:
they show the status ("encrypted since ...", open or closed) and, on a server, the permanent *Start unlocked after a
restart* switch, and offer no encryption any more. The page runs a preflight and lists what is in the way. Each blocker has a code and a
sentence; the migration starts only when none is left.

| Blocker | What to do |
|---|---|
| `vault_locked` | Unlock the node first: the master key is needed. |
| `not_superadmin` | Only a superadmin (not an agent) can encrypt a node. |
| `not_activated` | Activate the new sync format first (Admin > Metadata encryption; the page links to that page of the Admin area). A locked node can only relay sealed events, so the order is "activate, then encrypt". |
| `events_held` | Some received changes are still waiting for their keys. Let them finish. |
| `rotation_pending` | A key rotation is not finished. Let it finish. |
| `restore_running` | A restore or another maintenance task is running. |
| `identity_v0` | Lock and unlock the node once; the identity key is then upgraded. |
| `integrity` | `PRAGMA integrity_check` failed on the vault or on `chat.db`. Repair or restore it first. |
| `free_space` | The data volume needs about 3.5 times the size of the database and `chat.db` free (two encrypted copies and headroom). |
| `engine` | The database engine of this program cannot encrypt. Install a build that has the SQLite3 Multiple Ciphers engine. |
| `unc_path` | The data folder is on a network share. Use a local disk. |
| `plain_copies` | A plain copy of your vault lies in the data folder (below). |
| `mobile_foreground`, `phone_no_copy` | Phones only (see "Phones"). |
| `already_sealed`, `not_full_node` | Nothing to do: the node is already encrypted, or it is a blind node (blind nodes are never encrypted). |

### `plain_copies`: unencrypted copies in the data folder

The scan reads **every file of the data folder by its content**, whatever it is called and whichever folder it is in
(`media/`, `snapshots/`, `tmp/`, `updates/`, a `*.rekey-*` folder, a copy with another extension), and refuses to
start while it finds a plain image of the vault or of the chat history. It follows no link and opens no database. The
findings, by kind:

The only runtime exception is a runtime lock the running node can prove it owns: its held handle's file identity must
match the regular, non-link file at the path, and the file must be empty both before and after the failed read. The
desktop host passes its `node.lock` identity to its Api child; a running Api registers its own `.instance.lock` handle.
A name alone is never enough: a readable or nonempty lock, a renamed, hard-linked or replaced file, and every other
unreadable file are still scanned or block the preflight normally.

| Kind | Meaning | What to do |
|---|---|---|
| `plain_sqlite` | A file that starts with the SQLite header, under any name. | Move it out of the data folder, or **Erase these copies**. |
| `plain_journal` | A WAL or rollback journal that holds a plain page. | The same. |
| `plain_archive` | A `.gz`, `.tar`, `.tar.gz` or `.zip` that holds a plain database as any member, under any name. | The same. |
| `unverified_archive` | An archive this version cannot look into: another format (bzip2, xz, zstd, lz4, 7-Zip, RAR), an archive inside an archive, an encrypted or truncated one, one over the limits (100,000 entries, 16 GiB inflated). It is never silently accepted. | Move it out of the data folder yourself. The product does not erase it. |
| `shares_identity` | A second name for your live database (a hard link). Erasing through that name would overwrite the live database. | Remove that name yourself. |
| `live_linked` | Your live database has more than one name; another name exists somewhere, in the folder or outside it. | Find and remove the other name. |
| `link` | A symbolic link or reparse point. Its target is not scanned. | Remove it. |
| `unreadable`, `identity_unavailable` | A file or folder could not be opened or listed, or the identity of the live file cannot be read on this platform. The check fails closed apart from a proven empty runtime lock described above. | Close the program that holds the file and try again. |

**Erase these copies** (the button, or `POST /api/vault/seal/erase-plain-copies` with the master password) scans
again and overwrites and deletes exactly the paths you ticked that are still findings of kind `plain_sqlite`,
`plain_journal` or `plain_archive`. It never touches another path, a path outside the data folder, a link, or a file
with a second name; for each refused path it answers with the kind. It writes an audit entry (a count, no names). It
works on an encrypted node too, for a copy you find later. If you would rather keep a copy, **move it out of the
data folder**: an offline backup kept elsewhere is yours to look after.

## Encrypting

The page and `bmb vault seal` say first, in a warning, that **going back is not possible from the node**: encrypting it
keeps no copy of the unencrypted database. Press **Encrypt this node** and enter the master password again (or run
`bmb vault seal --yes`; the password comes from `--password-stdin` or a prompt). The node goes into maintenance mode
and the page follows the progress:

1. the preflight again, the sync loop off, no event write in flight, the databases folded into their main files;
2. a journal (`seal-journal.json`) is written;
3. new keys are made, and both databases are copied into encrypted files next to the live ones (`*.seal-new`);
4. the copies are **verified**: integrity, every table's row count, the event log's highest number and sequence, the
   sentinel, sample bodies, the folder tree, a scan of the copies for any readable title, and the boot store opens
   the copy with the key it holds. Only a copy that passes is swapped in;
5. the journal says `verified`, then `swapping`; the old plain files are renamed aside, the new ones renamed into
   place, and the plain files are **overwritten with random bytes and deleted**;
6. the journal is deleted, and a last scan of the whole data folder looks for any plain copy (the replaced files
   included). A copy the erase may take is erased; **only when the scan finds nothing does the node open the new
   database and the run end in `done`**. A run that cannot prove it (a replaced file that stays locked, a copy that
   cannot be erased) ends `failed` with the code `wipe` or `plain_copies`: the node is encrypted and **locked**, it is
   not opened over a readable plain file, and the page shows "Encrypted, but kept locked".

If anything fails before step 5, nothing is changed: the node stays plain with its old files, and the page names the
failing check (`plain_copies`, `verify_integrity`, `verify_counts`, `verify_sentinel`, `verify_bodies`,
`verify_tree`, `verify_scan`, `verify_boot`, `wipe`, `io`, `cancelled`).

**If the process is killed half-way** (power cut, crash, `kill -9`): at the next start the node finishes or undoes the
migration by itself, from what is on disk first and the journal second. After the step that wrote `verified` it goes
forward; before it, the node is plain with its old files, exactly as they were. No third state exists. If a plain file
cannot be wiped (a locked file, a read-only volume), the node starts encrypted and closed, `GET /api/vault/status`
lists the file under `migration.wipe_pending`, and **no unlock opens the node while it is there** (the refusal is
`repair_required`). Every unlock and every start tries the wipe again, so nothing is asked of you except the unlock
once the file is no longer in use. A file that had another name (a hard
link) is only unlinked, never overwritten, and is named in `migration.linked_plain`: the bytes live on under the other
name, and removing that name is yours to do.

**A setup, a join or a restore that is cut short.** A new node writes in the open for a moment: the setup writes the
node's identity and the first user, the join and the restore write the notes they bring, and only then is the database
encrypted. So that a cut in that moment (power, crash, `kill -9`) never leaves a node serving readable data, each of
the three writes a small marker, `seal-intent.json`, **before its first write**, and removes it when the node is
encrypted or has been taken back to the empty node. The marker names the kind (`setup`, `join` or `restore`), the time
it began, an operation id, a folder id (kept in the folder itself, `seal-intent.id`) and what the folder held of the
operation's own file names when it began, each with the volume and the file index of the file it found (not a path
or a key); it is written only over an **empty** node (no identity and no key slot; a
restore may also begin over the rows of an earlier restore that stopped halfway through its bootstrap). Only one
operation runs at a time, across processes: a second one is refused while another holds the operation lease
(`<data folder>.seal-intent.lease`) or a marker is pending. Every start reads the marker first, before anything opens a
database (and after the resolver of an interrupted migration, which has the last word over the files of a migration).
An encrypted pair only loses the marker. Anything else is wiped (the plain databases, their `-wal`, `-shm` and
`-journal`, the half-made `*.seal-new` files and the working folder `tmp/`, by overwriting, not by unlinking), and the
node is the empty node it was before; run the setup, join or restore again - **but only what is proven to be the
operation's**: a database is the operation's only if it is the very file the operation found when it began (same
volume, same file index; the operation writes it in place), and its write time proves nothing, because a node copied or
restored into the folder later is newer than any marker. A copy, a replacement, a database that appeared after the
operation began, or one whose identity cannot be read is somebody's node, and so is everything when the marker is
damaged, of another version, copied alone into another folder, or names a time that cannot be right. The one thing
this cannot see is a copy tool that overwrites the very file the cut operation was writing, in place (same file object,
new content). Nothing is touched then, and nothing is followed through a link (a data folder, a database,
`tmp/`, the marker, its folder id or their temp files, or anything in them that is a symbolic link or a junction): the start is refused by name
(`seal_intent_untrusted`, `seal_intent_foreign`, `seal_intent_unsafe`). A file that cannot be wiped ends the start with
`seal_intent_pending`, naming the file; the node opens nothing and tries again at the next start. A process that starts
while another process of the same node is running (so it cannot settle anything: the operation may be the other's)
is refused with `seal_intent_busy` and wipes nothing; the desktop node (`bmbd`) refuses before it launches its Api, and
`bmb` commands refuse by name too. A node that was set up in an earlier version and is simply not encrypted yet has no
marker and is never touched. A setup, join or restore that **fails** while running takes its own writes back at once,
without asking for a password, and ends the marker; it takes back only what it began itself, never what another
operation is writing. A restore **refuses a plain copy** (an archive or a file set made
before the encrypted database) on every host: there is no setting that lets one through.

**Links are refused first, at every start.** Before a start asks whether anything exists, reads a journal or a marker, probes the files for an encrypted
pair, or resolves, moves or wipes anything, it looks at the directory entries themselves (not at what they point to): a data folder that is a symbolic
link or a junction, or any of these names inside it that is one - the databases (`beememorybank.db`, `chat.db`), the boot store, each of their `-wal`, `-shm`
and `-journal` files and their `.seal-new` twins, the marker and its folder id with their `.tmp` files, the seal journal with its `.tmp` and
`.unreadable` names, and the working folder `tmp` - refuses the start by name (`seal_intent_unsafe`, listing the names). A link to a file or folder that
no longer exists counts as one, and so does a link on a node that has nothing pending: a link is never opened, read, followed or removed by the start, by
the resolvers of an interrupted encryption or re-key, by the setup, join and restore, or by the probe of an encrypted pair. The node then opens nothing and
says why; remove the link yourself (not what it points to), or point the data path at the real folder, and start again. This also means a data folder or a
database file that is itself a link is no supported layout. The same rule is applied by every entry point on its own (`VaultStartup.Enter`,
`SealIntent.Begin` and `Resolve`, both swap resolvers, `VaultStoreProbe`), from one list of names (`LinkGate`).

## Going back

There is none from the node. The migration keeps no copy of the unencrypted database (a `pre-encryption/` folder and
a "way-back snapshot" existed in 2.6.0 and 2.6.1; 2.6.2 neither makes nor looks for one), and **a restore never
produces an open node**: a backup, a snapshot or a blind copy that holds an unencrypted database is refused when it is
restored, whatever it is called. If you may want an unencrypted copy of your notes, take your own backup before you
press "Encrypt this node" and keep it outside the data folder; it stays yours, and the node will not restore it.

A folder an older version left (`pre-encryption/`, with a snapshot in it) is not read by the product; it is yours to
keep or to remove. A journal of an interrupted migration that names such a snapshot is still read, and resolved as
before.

What this means in practice: everything a peer received is still on the peer, the encrypted snapshots in `snapshots/`
restore into an encrypted node, and the chat history (`chat.db`) is encrypted with the vault. Going back further than
the activation (to a 2.5.x binary) needs the offline backup you took when you pressed "activate" in the sync dialog.

There is no plain export of an encrypted node in 2.6.0.

## Unlocking

A locked node answers `/health`, the login page, the unlock routes, `GET /api/session/status`, `GET /api/vault/status` and
`GET /api/snapshots/restore/progress` (a restore of this node ends with the vault locked, and the page that started it asks how it
went); the restore wizard of a new node is answered by `GET /api/restore/progress` while its run is still in memory, without names.
Every other route answers `503 vault_sealed`. The doors that open it:

- the master password (Web login, `POST /api/session/unlock`), a superadmin's password, or a recovery key (the recovery
  page resets a password, and leaves the node locked);
- a superadmin's agent key (the first request opens the database; an agent that was revoked is refused and the database
  closes again);
- on a desktop, OS auto-unlock; on a server, "Start unlocked after a restart" (both below);
- the update hand-off, which opens the new version after an update without a password.

A wrong password, a deactivated user, an unknown user and a wrong user name for a right password all give the same
401 and leave the database closed. The node locks when you lock it, when the app goes to the background (phones), and
at shutdown; locking closes both database files and clears every key from memory.

### What a locked node does for its peers

While locked, a node keeps relaying: events that peers push (sealed or control events) are stored in the inbox in the
boot store and applied, in the order received, at the next unlock. A revoke received while locked takes effect at its
place in that order. Events that carry clear content are not stored: the pusher is told to retry after the unlock.
The inbox refuses new events (507) when it holds more than 2 GiB or the disk has less than 1 GiB free
(`BMB_SEALED_INBOX_MAX_MB` changes the first). `bmb status` and `GET /api/vault/status` show the inbox summary.

## Auto-unlock

Both forms put the key that opens the database **on the same machine as the data**. They are off by default, nothing
turns them on but you, and an encrypted node with either on is protected against a stolen *disk image* only as far as
that machine's own protections go. Which form you see depends on the node's **role**, not on the operating system: a
node counts as a *desktop* only when the desktop application started it (`BMB_HOST_ROLE=desktop`); every other start,
a service, a headless `bmbd` or a container, is a *server* and gets the stricter checkbox.

**Desktop: OS auto-unlock** (Windows DPAPI, macOS Keychain; Admin > OS Auto-Unlock). On an encrypted node the page and its
confirmation say:

> Your notes are encrypted on this computer's disk, but with auto-unlock on, this computer opens them at start without
> your password. If someone gets this computer or its disk, their protection rests only on your Windows/macOS account
> password (and on disk encryption, if you use it).

**Server: "Start unlocked after a restart"** (`/VaultEncryption`, or `bmb vault auto-unlock enable|disable|status`;
plain servers get it too). Turning it on takes two steps: the warning

> Strongly not recommended on a server. With this on, the key that opens this server's database is kept on the server
> itself, so anyone who gets this server's disk, a copy of its data folder or a backup of it can read the whole vault:
> the database encryption no longer protects it.

and a separate confirmation with the master password and the phrase `START UNLOCKED`:

> Last check: after a restart this server will open the vault by itself, with nobody signed in. A stolen disk then
> opens everything.

Three parts must all be there and agree: a key slot of type `server_auto_unlock` (data: it lives in the database), a
secret (64 hex characters in `<data>/server-auto-unlock.key`, or in the file named by `BMB_AUTO_UNLOCK_KEY_FILE`, for
example a Docker secret mounted from **outside** the data volume; the node reads such a file and never writes or
deletes it), and an enable record `<data>/server-auto-unlock.enabled` (local state of this server: no snapshot,
package or product-made backup contains it). On a desktop role the server routes answer `role_desktop`; on a server
the OS card is not offered for turning on.

**A restore never turns it on.** The files carry the slot but never the enable record, so the rule is: a restore
starts the server **locked**, unless the local record of this very server still matches the restored slot.

- An **in-place restore over the same server** (its own record and key file are still where they were) whose restored
  slot is byte-identical to the one the record names keeps the earlier choice in force: that is preservation, not a new
  enable.
- **Every other restore**: a fresh directory, another machine, a copy, a slot that differs from the one the record names
  (it was enabled, disabled or replaced since the backup). The slot is *stale*: nothing unlocks, even with the key file
  still mounted, the door is retired, the slot is deleted at the first unlock, and the status says "Start unlocked after
  a restart was switched off because this data came from a backup or a copy; turn it on again if you want it".
- A backup taken **without** the excludes below carries the key file and the record too. That backup is a plain copy
  by definition, and restoring it is equivalent to the stolen disk the warning describes.

## Backing up and restoring an encrypted node

There are two contracts; do not mix them up.

### A full node (server, desktop, phone)

| What | Supported | Why |
|---|---|---|
| The **data folder of a stopped node** (Docker: `docker compose stop bmb` first; service stopped; process exited cleanly), the whole folder in one run | **yes** | Locking and shutdown fold both databases into their main files, so `beememorybank.db`, `chat.db` and `beememorybank.boot.db` belong to the same moment. A `-wal`/`-shm` left by a crash is part of the set and is copied with it. |
| `snapshots/` of a running node | yes, any time | Complete, atomic files, encrypted under the master key. Restorable by the snapshot restore, which ends in an encrypted node. |
| `media/`, `search-index-segments/`, `baselines/` | yes (already encrypted) | Copy them in the same run as the databases when you restore a full set. |
| The database files, `-wal`, `-shm` or the boot store of a **running** node | **no** | The three files change at different moments: a copy can show a state no crash could produce. Stop, copy, start. |
| `server-auto-unlock.*`, `os-auto-unlock.dat` | **never in a backup** | A backup that carries the key that opens it is a plain copy. Exclude them. |
| `seal-journal.json`, `*.seal-new`, `*.seal-old*` | part of the set when present | A set taken mid-migration restores into the resolver's hands, which is deterministic. |

A recipe (restic; rsync and volume snapshots are the same with the same excludes):

```
docker compose stop bmb
restic backup /path/to/data --exclude 'server-auto-unlock.*' --exclude os-auto-unlock.dat
docker compose start bmb
```

The product's own pre-update copy is taken under a hold (the node is locked and nothing can write any of the three
files), and the "Back up now" snapshot is a snapshot of the running node and needs no stop.

### Restoring a file set

Stop the node; put **all** files of one backup set into the data folder (never a database from one set and a boot
store from another); start the node. It comes up **closed**. In this order it checks, and every check fails closed with
a named reason (nothing is opened, nothing is created beside it):

1. a set with a journal or `*.seal-new` / `*.seal-old*` files is resolved as after a crash (a leftover that is
   provably a replaced plain file is wiped; one the node cannot place ends the start with `seal_leftovers`);
2. the folder is diagnosed (the reasons below);
3. the first unlock opens the database with the key the boot store holds (a boot store of another node or lineage ends
   in `key_mismatch`; a database older than its boot store is `store_stale`, named in the log and in `last_refusal`);
4. run **`bmb vault verify`** (it asks for the master password, or `--password-stdin`): it opens the database
   temporarily, runs `PRAGMA integrity_check` on the vault and on `chat.db` (a damaged or cut-off page is an error,
   not silent garbage) and on the boot store, and checks that every key slot and auto-unlock agent has a door and that
   the wrapper of the current key unwraps. It prints `ok` or the failing check, and leaves the database closed. Make it
   the step before the node meets its peers again;
5. server auto-unlock follows the rule above: a restore starts the server locked;
6. a restored folder brings the node back **as it was at the backup moment, with its node id and log head**. If a peer
   has pulled newer rows from this node since, the restored node is behind the mesh: use a join or a network restore
   instead (`docs/snapshot-restore.md`).

A restore never reintroduces a plain copy: a set taken from an encrypted node holds only encrypted files. A set taken
before the migration is a plain set; copied back into the data folder it is simply the open node it was (the node
never encrypts itself: press "Encrypt this node"). The product's own restores (the snapshot restore, a network restore,
the restore wizard of a new node) are different: **they refuse a plain database**, in an archive or in a file set, and
they end in an encrypted node.

### A blind node

A blind node is never encrypted. Its backups keep their own contract (the `VACUUM INTO` staging copy of its database
plus `baselines/current/` through restic, no note count anywhere); Step B changes nothing there.

## When a node will not start

A node that cannot start names the reason, the files, and the ways back, in the start message, the log, `bmb status`,
`bmb vault verify` and the Web start page. It opens nothing, creates nothing, and **deletes, moves, renames and changes
nothing**. The reasons:

| Reason | What it means | What to do |
|---|---|---|
| `plain_companion` | The folder is encrypted, but `chat.db` (a plain SQLite file), or a `-wal` or `-journal` of either database holds unencrypted data of this node: a set assembled from a plain backup and an encrypted one, or a half-done restore. | Move the named file out of the data folder (keep it elsewhere if you need its content), or restore the folder from one backup set. Then start again. |
| `companion_unreadable` | One of those files exists but cannot be read (a lock, a permission). | Close the program that holds it and start again. |
| `db_without_boot` | An encrypted database without its boot store. Nothing can open it. | Restore `beememorybank.boot.db` from the **same backup** as the database, or restore the vault from a snapshot, a peer or a blind copy. |
| `boot_beside_plain_db` | A boot store lies beside a database that is not encrypted: the two do not belong together. | Restore the folder from one backup set. |
| `boot_not_ours` | The boot store is not a boot store of this program. | Restore the boot store from the same backup as the database. |
| `db_missing_or_empty` | The boot store is there, the database is missing or empty. | Restore the folder from one backup set. |
| `db_not_page_aligned` | The database is cut off inside a page, as a copy interrupted halfway is. | Restore the folder from one backup set. |
| `seal_leftovers_no_journal` | Files of an interrupted "Encrypt this node" lie in the folder without their journal. | See `seal_leftovers`. |
| `seal_intent_pending` | A setup, join or restore of this node was cut short and the start could not wipe what it wrote in the open (a locked file, a read-only volume). The start names the file. | Close the program that holds it and start again; the wipe is retried at every start. Nothing of the unfinished node is served meanwhile. |
| `seal_leftovers` | A `*.seal-old*` file is beside a live pair that cannot be proven encrypted (a plain or missing live database, a missing or foreign boot store). The start is refused and **nothing is deleted**: an automatic guess could destroy the only copy. | Restore the folder from one backup set. **Do not delete any `*.seal-old*` file before you have checked which of them is your database.** |

**A lost boot store** is the case `db_without_boot`: the boot store holds the wrapped database key, so an encrypted
database cannot be opened without it. Restore it from the same backup as the database. If there is no such backup,
restore the vault content from a snapshot (`bmb snapshot restore-standalone`), a peer (join again), or a blind copy.
The master password alone cannot rebuild it.

An **old program** (2.5.x or any build without the encrypted database) pointed at an encrypted folder fails on its first
statement (SQLite error 26) and stops; no file is written or changed. A 2.5 desktop application does not see a vault in
that folder and refuses to create one there.

## Edges and pairings in a mixed mesh

A full node is in one of four states: **N0** a 2.5.x binary; **N1** 2.6.0, sync format not activated, plain (on the wire
exactly 2.5.1); **N2** 2.6.0, activated, plain database; **N3** 2.6.0, activated, encrypted (open or closed).

| Edge | Way back |
|---|---|
| N0 to N1 (the update) | In place is supported while N1: the 2.5 binary opens the same database, or restore the pre-update backup. |
| N1 to N2 (activation) | Never in place: restore the offline backup you took when you pressed "activate", on every node. What was written after it is lost mesh-wide. |
| N2 to N3 (encrypt) | None from the node (see "Going back"). Your own offline backup, if you took one, is a set of plain files the node will not restore. |
| N3 closed and restarted | Unlock. Received events wait in the inbox and are applied in order. |
| N3 put back from a file backup | The next backup, or a network restore or join from a peer for what the backup lacks. |

| A talks to B | Result |
|---|---|
| N0 with N1 | As 2.5.1: protocol 3 both ways. |
| N0 with N2 or N3 | The N0 node gets 426 and never pulls from a node declaring 4; the activated node never pulls from N0 either. The N0 node's data is untouched. Update it to 2.6.0 (it becomes N1, pulls the activation, becomes N2), or join it again as a new device. |
| N1 with N2 or N3 | Accepted; N1 pulls the activation and becomes N2. |
| N2 with N3 (open) | Identical on the wire. |
| any node with N3 (closed) | Sealed and control events are stored in the inbox and relayed; a clear-content event is answered "retry after unlock"; `GET events` below the last open head answers `503 vault_sealed_range` and the puller's cursor stays. |
| a full node with a blind node | The blind host is reseeded in format 2; an encrypted node never pushes clear content to it. |

No Step B code path lowers, reuses or truncates a log position. A refusal (426, `503 vault_sealed`, `503
vault_sealed_range`, error 26 at start) happens before anything is written.

## Phones

**A fresh or joined phone is open.** Unlike a server or a desktop node (which end setup and join encrypted), the phone's
setup and join create a plain database, and it stays plain until the owner runs "Encrypt this phone's database" below.
Nothing starts that for you, and the phone does not ask.

An Android or iPhone app runs the same migration in process, from the **Security page** (Android) or **Settings**
(iPhone), in the foreground, with a progress bar that keeps the screen on. The preflight adds `mobile_foreground`
(the app is in the foreground, battery at least 30 % or charging). A phone keeps no copy of the plain database, so
the migration starts only if some full peer has reported a position at or above this phone's log head (the phone
holds nothing a peer has not got), or you tick "This phone holds the only copy of these notes; encrypting cannot be
undone" (`phone_no_copy`). Going back on a phone is to remove the vault and join again from a peer (the join gives the
phone the notes again; it joins only a node that is on protocol 4).

An encrypted phone locks **and closes its database** every time the app goes to the background. The master password
unlocks it; so does the fingerprint (Android: the master password stored under a Keystore key that needs the
fingerprint) or Face ID / Touch ID (iPhone: a Keychain key unwraps the master key; the database key and the page keys
are never put in the Keystore or the Keychain). Android excludes the app data, the boot store included, from OS
backups; the iPhone data folder is excluded from iCloud backup. A phone killed in the middle of the encryption resolves
it at the next start, as a server does.

The full Android app (2.6.x) **offers** "Encrypt this phone's database" on the Security page
(`PhoneVaultLifecycle.PhoneEncryptionAvailable` is true in this build, so that the native release gate's device run - a build of
the app, then join, encrypt, background, unlock, kill mid-encryption and restart on a real device - can be made;
`PhoneVaultLifecycleTests` and `AndroidFullAppWiringTests` keep the switch on). That device run is still the release gate of the phone's encryption.
Whether a phone is encrypted or not, it syncs and opens encrypted metadata the same way (see "The full Android app" in [sync.md](sync.md)).

## Linux and Docker

The encrypted database works the same on Linux, in Docker and on macOS. Before a release, the gate
`scripts/sealed-gate.sh` runs the Docker smoke: initialise a vault through the setup API (it is encrypted and activated from
its first request: the gate checks the file header, the boot store and that no pre-encryption folder exists), restart
(closed, 503), unlock, scan the data volume for canaries after a stop, enable and disable the server checkbox (with the
key file mounted from outside the volume), restore a restic-style copy made with the excludes into a fresh directory
(the node comes up **closed**) and `bmb vault verify`, then the blind container. See the header of the script for its
limits (`--cpus 2 --memory 3g`, one container at a time).

For an image you run yourself: mount the data volume, keep `BMB_AUTO_UNLOCK_KEY_FILE` (if you use the checkbox) on a
secret **outside** the volume, back up with `docker compose stop` first, and use the excludes of the recipe above.

## Command reference

| Command | What it does |
|---|---|
| `bmb status` | Is the database encrypted, open or closed, what waits for the unlock. Never asks for the password. |
| `bmb vault status` | The same, and what an interrupted encryption left to finish. |
| `bmb vault seal --yes` | Encrypt this node (calls the running node; password from `--password-stdin` or a prompt). |
| `bmb vault verify` | Check the encrypted database, the chat database and the boot store; leaves the database closed. |
| `bmb vault auto-unlock status\|enable\|disable` | The server checkbox (calls the running node; `enable` prints both warnings and asks for the phrase). |
| `bmb article ...`, `bmb agent ...`, `bmb user ...`, `bmb init reset`, `bmb snapshot create` | On an encrypted folder they ask for the master password (`--password-stdin`, or typed when asked) and open the database for the one command; without it they say so and do nothing. |
