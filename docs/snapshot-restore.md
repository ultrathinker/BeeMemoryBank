# Snapshots and restore

A **snapshot** is a copy of this node: its database (articles, folders, tags, comments, versions, users, key
slots, the node's identity, the trusted nodes) and the encrypted media, packed in one `.tar.gz` and encrypted under
the master key. Admin → Snapshots makes, uploads, downloads and deletes them.

**Restoring a snapshot does not simply put the old state back.** There are two kinds of restore and they
treat the node's *identity* differently. Pick the one you mean in the Restore dialog (Admin → Snapshots → the
restore button of a snapshot).

| | Restore this node only (default) | Restore the whole network |
|---|---|---|
| What it is | This node leaves the network and becomes a **new node** | Every trusted node is asked to restore the same snapshot |
| Node id and signing key | **New** (the name stays) | Kept |
| Trusted nodes, blind copies, sync history | **Forgotten** | Kept |
| Articles, folders, tags, media, comments | Replaced by the snapshot's | Replaced by the snapshot's, here and on every node that accepts it |
| Users, key slots, agents | The snapshot's | Kept (not replicated) |
| Other devices | Must be **joined to this node again**; blind copies **paired again** | Apply it when they next sync (see below) |
| Who can start it | A superadmin, with the master password | A superadmin, with the master password |
| Safety backup | "Create backup before restore", ticked by default | Always taken first |

The server is unavailable while a restore runs. When it ends the vault is locked and you sign in again.

**A restore never produces an unencrypted node (2.6.2).** Every restore of every kind ends in a node whose database is
encrypted:

- a restore **into** an existing node (this node only, the whole network, the safety-backup put-back) is refused, before
  anything is changed, when that node's database is not encrypted yet: "This node's database is not encrypted, and a
  restore never produces an unencrypted node. Encrypt this node first (Admin, Database encryption), then restore.";
- a snapshot, a join package or a blind copy that holds an **unencrypted database** is refused, whatever it is called and
  wherever it is restored (an archive from 2.5.x, a plain snapshot, a package with a plain SQLite file in it); there is
  no exception and no option to allow it;
- the restore wizard of a **new** node (Setup, "Restore from a blind node or a backup") restores into the new vault and
  then encrypts it under the master password you gave, with metadata encryption on, before it reports "done"; if it
  cannot encrypt it, it takes the node back and says so (the blind copy or the backup is untouched, so you can run it
  again).

Restoring an encrypted backup into an encrypted node, and a network restore into an encrypted node, work as before.

While a snapshot is being made (by hand, before an update, for compaction, for a joining device or a blind copy), its
working copy of the database is made in the node's data folder (`tmp/snapshot-*.tmp`, owner-only), not in the
operating system's temp folder, and is removed when the archive is written, with the SQLite files beside it (`-journal`,
`-wal`, `-shm`). A copy that cannot be removed fails the snapshot (repair required): it is not reported as made, and its
archive is removed too. Until the snapshot filters it, that copy is
the whole database; if the process is killed in between, the node removes the leftover at its next start. A snapshot
therefore needs free space for about twice the database on the data drive, and on the drive of the snapshots folder.

A join or a restore works the same way in the other direction: the archive it downloads and the folder it extracts it to
(the vault database in clear: key slots, password hashes, titles, paths) are made in the same `tmp/` folder of the data
folder, as `work-*`, owner-only (mode 0600 files and 0700 folders on Linux and macOS, an owner-only ACL on Windows),
not in the operating system's temp folder, and are removed when the join or restore ends. If the process is killed in
between, the leftover is removed at the next start of the node, of the phone app, or of `bmb join`.

The `tmp/` folder itself is owner-only too, and so are the SQLite files that SQLite creates beside a working copy: on
Windows they take the folder's ACL. A restore, a join or a snapshot whose folder or file cannot be removed fails with
repair required; it is never reported as done.

## Restore this node only

A backup is restored only if this version can open its database: one made by 2.6.0, 2.6.1 or this version is (a 2.6.x backup is adopted at
the next start, with nothing re-run); one made by a **release before 2.6** is refused before anything is replaced, with the advice to
restore it on a node that runs 2.6.1, update that node, and make a new backup.

Use it to bring **this** node back from one of its own snapshots, or from a copy made by another node in the
same network, without touching the rest of the network — for example after a damaged database, or to start a
fresh copy of a vault on a spare machine. Both cases require the same vault master key. A snapshot from a
different vault cannot be opened here, regardless of the password entered.

What happens, in order:

1. The master password is checked and the vault is unlocked for the work. If "Create backup before restore" is
   ticked, a complete snapshot of the current state is made first (it is encrypted, and it is the way back from this
   restore).
2. The snapshot is unpacked and checked (manifest hashes, and it must contain the key slots, the users and the
   node identity — a package built for a joining peer is refused, because without key slots the content could
   never be opened again).
3. In a staging copy of the database the node gets a **new identity**: a new node id and a new Ed25519 key pair.
   The trusted nodes (`tbl_whitelist`), the sync positions, the replay shield, the event log and the sync
   quarantine are emptied. Blind copies — a blind node on a PC or a Mac, a phone — are rows of the trusted
   nodes, so they vanish from the list with it.
4. The new private key is stored the way a freshly initialized node stores it: wrapped under the master key of the
   restored database, bound to the new node id. (Before 2.3.0 it was written bare into a row that said "wrapped",
   and the restored node could not sign: sync authentication, pairing and every signed event failed. A node
   restored that way has to be restored again.) If no key that opens the snapshot's database can be found — the master
   password does not open its key slots — the restore is refused before anything is replaced. An encrypted
   snapshot from a different vault is likewise refused before anything is replaced: its key slots cannot be
   read until the database has already been decrypted with this vault's master key.
5. The staged database replaces the live one, the media are swapped in, and the vault is locked. The live file is
   replaced behind the node's connection barrier, on a plain and on an encrypted node alike (a restore, and the
   put-back of a failed network restore): new database connections wait, the ones in use are waited for (30 seconds
   at most), and only then does the file change, so no connection can go on writing the old file. If work of the node
   does not finish in time, the restore says so and nothing has been changed; try again in a minute.

**What you have to do afterwards**

- Sign in with the master password the snapshot was made under. The sign-in page tells you this node is now a
  new node and shows its name and id.
- **Join your other devices again.** They still know the *old* node, which this one no longer is: on each of
  them join this node (new node id) with the master password, as for any new device.
- **Pair the blind copies again** (Admin → Blind nodes, and on the phone). Their trust in the old node ended with
  it.
- Devices that were never joined again stay on the old network. Nothing is deleted there.

From the command line: `bmb snapshot restore-standalone <file>` asks for the master password and does the same. On a node
that is not encrypted it refuses (see above); on a fresh folder `bmb init` already made an encrypted node, so the
restore goes into that.

## Restore the whole network

Use it to roll **every** node back to one snapshot — after something went wrong everywhere (a bad import, a
mass delete that was already synchronized). It is the more destructive of the two: content that was written
after the snapshot disappears on every node that applies it.

How it works:

1. This node makes a safety backup, builds a **filtered copy** of the snapshot (the identity, key slots, users and
   sync state are stripped, so nothing secret reaches the other nodes), applies its replicated content
   (articles, folders, tags, media, comments, versions) to itself, and signs a `restore_network` event that
   says where to fetch the copy and its hash. The offer is open for 30 days.
2. Each other node learns of it when it next syncs. It applies the restore **only if** it trusts this node as a
   superadmin (every device that joined with the master password starts that way) **and** has *Auto-restore*
   switched on for this node (Admin → Trusted Nodes). It then downloads the copy from this node (or from another node
   that already has it), checks the hash, and applies it. A node without Auto-restore keeps the event pending and
   does **not** restore. (Applying a pending restore by hand is not offered in the Admin UI: switch
   Auto-restore on for the originator and the node picks the pending event up the next time it syncs or is
   unlocked. There is no UI prompt, notification, or manual approval path.)
3. Blind copies are not restored: they are flagged for a reseed.
4. Keep this node running and reachable until the others have fetched the copy; they download it from here.

What stays as it is on every node: its identity, users, key slots and the nodes it trusts.

From the command line: `bmb snapshot restore-network <file name or id>`; the API is
`POST /api/snapshots/restore-network` (superadmin, not an agent key; the Admin page also sends the master
password, which the server then re-checks). It is refused (409) while this node holds events it received but has not
applied yet ([sync.md](sync.md#held-events-received-and-applied)): unlock it and wait until they are applied. On a
receiving node, held events that arrived after a restore wait until that restore has finished, then apply.

## Joining a device after sealed metadata is turned on

Once the owner has turned on sealed metadata ([encryption.md](encryption.md), the activation in Admin → Metadata encryption), a node no longer hands out
a plain archive to a device that joins:

- **The join archive's database is sealed.** `GET /api/sync/snapshot/for-join` returns the archive with its database entry as a
  `BMBCH1` container (purpose `join`) under the master key; `manifest.json` says `"dbEncrypted": true, "dbCipher": "bmbch1"`. It is not
  a SQLite file and holds no title, path, tag or comment in the clear. The cache of join archives is kept per master key and is dropped
  when sealed metadata is turned on and when the master key rotates. A blind copy asking for it gets 403 (it joins by being paired).
- **Only a 2.6 device joins.** `POST /api/join` also needs the joiner to say it is on protocol 4, or on protocol 3 with the
  `metadata-envelope-v1` capability. A device on 2.5 sends neither: it gets 426 ("update this device to 2.6 or newer") before any row
  is written, on the producer or anywhere else.
- **A new node joins only a node that is on protocol 4 (2.6.2).** The joining side checks what the host declares in its answer and
  refuses a host on protocol 3 before it writes anything: "Cannot join: the other computer does not encrypt its metadata yet ... Switch
  metadata encryption on there first (Admin → Metadata encryption), then join again." There is no workaround and no
  compatibility mode: `bmb join`, the Setup page and both phone apps stop at the same sentence, and the host is told that the attempt
  was abandoned. A joined node is then encrypted (database and metadata) under the master password it was given, the same as a node
  set up from nothing.
- **The joiner opens it with the key it was just given.** `bmb join`, the Setup page and the Android and iPhone full apps decrypt the
  archive with the master key received from the join and only then import it. A 2.6 joiner that sees protocol 4 in the producer's identity and
  is handed a plain archive refuses it (it fails closed), so a node that merely claims it has not activated cannot make a joiner take a plain
  copy.
- **The joiner starts activated.** The imported tables include the metadata keys and the activation, so the new device declares protocol 4 from
  its first start and opens the metadata key after its first unlock.
- **Refusals that stay.** A locked producer answers 423; a producer that holds events it received but has not applied yet answers 409
  ([sync.md](sync.md#held-events-received-and-applied)) until they are applied.

Before sealed metadata is turned on a 2.6.2 node is not joined at all (see above); the plain join archive is only what an older
host still builds for an older device.

## Restore from a baseline and the deltas after it

After sealed metadata is turned on, what a blind node, an Android blind copy or a Docker blind node's backup holds is **a baseline**
(a sealed copy of the vault's state at one moment, made by a full node that held the master key) **and the sealed changes after it**
(deltas). Restoring from them needs the master password and nothing else; the file, the blind node or the folder cannot open anything.

The sources are the same as before:

| Source | What it holds |
|---|---|
| A blind node, with its restore code | a `full` package over the restore routes: the baseline, the control tables (keys, recovery boxes, anchors), the deltas and the blobs |
| An Android blind copy's backup file | the same package, the phone's own events and the pairing record sealed under the master key |
| A Docker blind node's backup folder (or restic repository) | the node's database copy (control tables, signed log, blobs, no metadata), `baselines/current/` and the recovery set beside it |

What the restore does, in this order, and what it refuses:

1. **Checks the source.** A package is verified by the format-2 reader against the key the restore code (or the phone's pairing record)
   names: both signatures, every entry's hash, the baseline's producer (a superadmin of the package's own whitelist) and its signature. A folder's
   baseline must match the signed record in its database and be signed by a superadmin the copy knows.
2. **Recovers the keys from the password** (recovery boxes and the chain of retired master keys, as for any restore) and opens every metadata
   key with the master key of its fingerprint. A baseline whose key no recovered key opens is refused before anything is written.
3. **Imports** the baseline (decrypted into an owner-only staging folder), the control tables and the blobs, then makes the identity, the
   slot and the admin.
4. **Replays the deltas** through the ordinary event applier, with the package's whitelist deciding who may have written what. Progress is
   kept (`restore_replay_seq` in `tbl_migration_marker`), so a restore that was stopped runs again and finishes; a delta already applied is a
   no-op. A delta whose signature does not verify is skipped and counted, never applied.
5. **Checks the anchor after the replay.** The newest integrity anchor under the master key is compared with the restored state; a blind node
   that held a delta back, or a copy that was changed, shows as a mismatch. Restored peers keep the status the anchor vouches for.
6. **Sets the positions**: the pull positions of the signed baseline, and the blind node it came from at the package's last position.

If the blind node holds more deltas than one restore takes (200 000), the restore replays that many, says **"Incomplete: sync with the
blind node to finish"** (the `replay` part of `/api/restore/progress`), and leaves the rest to the first sync from the position it reached.

Two things a baseline cannot do, which the restore reports instead of hiding: a change that only a delta older than the baseline's cut
carried (an anchor from before it) is not in the package, and a newer row backed only by a sealed delta cannot be vouched for by the
signed log in the clear. Both make the anchor result "partially confirmed" rather than "confirmed", with the rows named.

## On an encrypted node (2.6.0)

When the owner has chosen "Encrypt this node", the database files on the disk are encrypted (see `docs/sqlite-engine.md`). A snapshot of such a
node and a restore onto it work as described above, with these differences. Since 2.6.2 every node that is set up is encrypted, and a restore is
refused by a node that is not.

**Making a snapshot.** The working copy in `tmp/snapshot-*.tmp` is made with `VACUUM INTO` from the open, encrypted vault, so it is encrypted with the
node's own key at every moment; it is filtered and compacted through encrypted connections, and the database entry of the archive is built from it in
memory and encrypted under the master key, exactly as before. A plain database file never exists, not even for a moment. Two consequences: a
snapshot can only be made while the vault is open (a locked encrypted node makes none and leaves no working copy), and a **plain** snapshot
(`encryptDb: false`, what a node before 2.6.0 offered for a join) is refused. The join snapshot of an activated node is built the same way
(a `BMBCH1` container streamed from the working copy).

**Memory.** The database entry of a snapshot made for a backup or a restore (a `BMBDB2` container, the format every 2.5 and 2.6 node reads) is one
AES-GCM message, so the node holds the database image in memory while it packs it and while it opens one. Since 2.6.2 it holds it **once**: the image
is read into the buffer its ciphertext takes and encrypted where it lies, and a container is decrypted where it was read (before, two or three copies
were alive at the same time, and a 645 MB database in a container limited to 3 GB ended in an `OutOfMemoryException`). The node also checks first, and
there is one owner of that check (`MemoryBudget`), which every whole-image allocation goes through: the managed buffer of a container, the copy of an image
that a caller asks for, and the native copy the importer needs for an image with no page reserve. It measures against what really governs the failure, two
limits at once: the **heap limit the runtime enforces** (in a container or a Windows job a fraction of the limit, 75% by default, not the limit itself) and the
**limit of the process** (the cgroup `memory.max` of a container, v1 or v2 and any parent that limits it; the job object limit on Windows; else the memory of
the machine), less what the process already holds (the managed memory alive after a collection, and its native memory: the database caches, the embedding
model). What the limit is charged for is the **usage of the limiting cgroup** (`memory.current`, or `memory.usage_in_bytes` on v1, less the inactive file cache the
kernel gives back first): it counts the siblings of the process in the container (the Node, the Api and the Web are three processes), tmpfs and the kernel's share,
which the resident set of one process does not; the larger of the two numbers is taken (a run in a real memory-limited container is `CgroupContainerRunTests`). The smaller room is the free memory. When it is smaller than the database image plus 64 MB, nothing is read, nothing is created and the answer names
what is missing; the same words answer when the runtime then cannot give the one buffer. The image is the live pages of the database (what `VACUUM INTO`
will hold), not the length of the file, which also counts the pages a big delete left free. The limit that is recommended comes from the same two
conditions, so a 645 MB database in a container is never promised a raw 1024 MB (that container gives the heap 768 MB): "Making the snapshot of this vault needs about 709 MB of
free memory in one piece, and this node has 668 MB free. Raise the memory limit of the node (for a container: its memory limit) to at least 1280 MB and try again.
Nothing was changed." Where the heap limit is set by configuration (`DOTNET_GCHeapHardLimit`), raising the container does not raise it, and the answer
names that setting too. On the web route a node with too little memory answers **507** with the code `not_enough_memory`; a container that does not
authenticate (damaged, or another vault's) is a 400 with a fixed sentence and changes nothing, and so is one that authenticates but whose database cannot be read (not a SQLite
image, a schema that does not open): the same fixed sentence with the code `bad_snapshot`, no word of the parser, nothing staged left. The join snapshot (`BMBCH1`) and the verification of a sealed database (`bmb vault verify`, the
self-check after sealing) work page by page and do not have this need; the hash checks of the verifier read their rows as a stream, and the check of the
search index compares the ids of both databases one row at a time. The verification fails closed: a table that cannot be counted, or a search that cannot run, on
either copy fails it (two copies whose indexes nobody could read are not two copies that agree). The 2 GiB limit of the format stays.

**A snapshot of 2.5 (BMBDB1) holds one image too.** Its database has no page reserve, so it cannot be copied page by page into the encrypted file. The
container is opened in one buffer, and SQLite reads that buffer where it lies (it is pinned, read only) and writes the encrypted staging file with `VACUUM INTO`:
the restore does not hold a managed image and a native copy of it. Only a stream of such an image (a join archive, a package) takes one native copy, through
the same budget.

**What is left when a write fails.** The container of a snapshot database is rewritten through an owner-only temp file in the same folder, flushed and checked
for its length, and then renamed over the old name: a full volume or a cancellation leaves the file as it was and removes the temp file (wiped, when it holds a
plain image). The archive of a snapshot is written the same way, under a temp name, and takes its `*.tar.gz` name only when it is whole, so a failed or
cancelled snapshot (the service and `bmb snapshot create`) leaves no archive; the finished archive is owner-only. The sealed staging containers of a snapshot (`BMBDB2`) and of a
join (`BMBCH1`), and the sealed copy a restore makes of an opened container, take their name by one rename of a finished file and honour the cancellation token.

**Restoring a snapshot onto an encrypted node** (this node only, in place, or as part of a network restore). The archive is checked before anything is
extracted: a snapshot whose database is *not* encrypted is refused ("restore it on a node that is not encrypted, then encrypt that node"), because
extracting it would put a plain copy of a vault on the disk. (From 2.6.2 the same refusal holds for every restore, on every node: see the rule at the top of this page.) An encrypted database is opened in memory and written into a staging copy that is encrypted
with the node's own key; the staging edits (new identity for a restore of this node only), the checks and the table import work on that copy through
encrypted connections. The node also keeps a small unlock record outside the database, so that a password can be tried before the database is open:
the restore writes the snapshot's key slots into that record before it replaces the database and rewrites it afterwards, so the passwords of the
restored snapshot open the node after a restart, wherever a crash falls. A node that cannot update that record refuses the restore before anything
is replaced.

**No way-back snapshot (2.6.2).** Encrypting a node used to take a snapshot of the unencrypted node first and keep it in `pre-encryption/`, with a key
envelope that opened it under the password of that day. 2.6.2 neither makes that snapshot nor looks for one: **going back to an unencrypted node is not possible
from the node**, and a restore never produces one. `bmb snapshot restore-standalone <file name>` looks in `snapshots/` only. A snapshot an older version
wrote with a key envelope (`key-envelope.json`) is an ordinary snapshot to this version: it opens only with the master key of the node it is restored into,
the envelope is never read, and its database is refused if it is not encrypted. A `pre-encryption/` folder an older version left is not read, listed or
removed by the product.

**Restoring from a blind copy or an Android backup** onto an encrypted node (a fresh vault): the baseline of the package is opened with the recovered
keys and written, through a pipe, into an encrypted staging copy; the package's control tables are plain by design and are attached to the node's
connection with an empty key. Nothing plain is written to `tmp/`.

## Restoring from a blind copy (2.6.0, after metadata encryption is switched on)

A blind copy no longer holds your tree — it holds an encrypted **baseline** (a filtered copy of the
replicated tables, built by an unlocked superadmin full node), the signed events that came after the
baseline's cut, and the encrypted media. Restoring a full node from it needs **only the master
password**, on any machine: the password recovers the key chain, the keys open the metadata-key rows,
the baseline is decrypted and imported, and the deltas are replayed through the ordinary event
applier (a very long backlog is capped at 200,000 changes and can resume).

Only then is the newest state anchor checked — so a blind copy whose deltas were lost or withheld is
*detected*, not prevented. When the replay could not reach the anchor, the node tells you plainly:
**"Incomplete: sync with the blind node to finish"** — the fastest fix is to let the restored node
sync with the copy it was restored from, or with any full node that is current.

What a restore cannot do: the package a blind copy serves is the new format 2, so a full node of
2.5 or older cannot read it; and a blind copy that has itself adopted the activated state refuses
the old format 1 outright. And as everywhere else, this reaches only copies made *after* activation
— older backups still contain readable metadata and are the owner's to retire.

## On an encrypted (sealed) node

"Encrypt this node" (2.6.0) page-encrypts the database files themselves; the operator's guide for that is
[sealed-database.md](sealed-database.md). What it changes on this page:

- **Snapshots work unchanged and stay the primary backup.** A snapshot of a sealed node is made while the node is
  open, holds the same content as any snapshot — encrypted under your master key, restorable on any machine with the
  password — and holds no boot store; the node rebuilds one when the restore ends. A snapshot restore never
  encrypts or decrypts the node you restore it *on*: a sealed node stays sealed, and a node that is not encrypted refuses the restore.
- **Restore needs the vault open.** A sealed, locked node serves sync only; sign in as an administrator first
  (that unlocks the vault), then restore. The safety backup before the restore is taken as usual.
- **There is no pre-encryption snapshot.** 2.6.2 keeps no copy of the node as it was before the encryption, and nothing on the node goes back to
  an unencrypted state ([sealed-database.md](sealed-database.md#going-back)). `bmb snapshot restore-standalone <name>` looks in `snapshots/`
  only.
- **The server's "Start unlocked after a restart" checkbox survives a restore in one case only** — an in-place
  restore over the very server that enabled it, with a byte-identical key slot. Every other restore starts locked
  and retires the stale slot ([sealed-database.md](sealed-database.md#restore) has the rule and the notice text).

File-level backups of a sealed node are a different animal — three files that form one set, copied only while the
node is stopped; see [sealed-database.md](sealed-database.md#backups).

## On the record

Every restore is written to the audit log (`tbl_audit_log`):

| Action | When | Details |
|---|---|---|
| `snapshot_restore_started` | Before anything is touched | `Mode=standalone`, `keep-identity` or `network`, whether a backup is taken first, who started it |
| `snapshot_restored` | After a restore of this node, in the restored database | Mode, the new node id (standalone), the backup file, who |
| `snapshot_restore_refused` | A mode the route does not serve or nobody defined, a wrong master password on the network route, or a snapshot that belongs to another vault | Mode and the reason |

The *started* entry is written before the swap, so it lives on in the safety backup (the restore replaces the
audit log together with the rest of the database).

## For API clients

`POST /api/snapshots/restore` takes `{ fileName, masterPassword, createBackupFirst, mode }` with `mode` one of:

- `standalone` — this node only, new identity (what the dialog sends);
- `keep-identity` — the snapshot's database is put in place **as it is**, identity and trusted nodes included. Only a
  snapshot of this very node leaves the network coherent; with another node's snapshot two nodes would share
  one identity. Not offered in the dialog.

Older clients that send only `standaloneMode` get `standalone` for `true` and `keep-identity` for `false`. `mode: "network"`
is refused here — use `POST /api/snapshots/restore-network` (`{ mode: "NetworkWide", fileName | snapshotFileId, masterPassword }`).
A caller that is not a superadmin, or that is an agent key, is refused on both routes.
