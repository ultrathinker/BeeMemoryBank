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

While a snapshot is being made (by hand, before an update, for compaction, for a joining device or a blind copy), its
working copy of the database is made in the node's data folder (`tmp/snapshot-*.tmp`, owner-only), not in the
operating system's temp folder, and is removed when the archive is written. Until the snapshot filters it, that copy is
the whole database; if the process is killed in between, the node removes the leftover at its next start. A snapshot
therefore needs free space for about twice the database on the data drive, and on the drive of the snapshots folder.

A join or a restore works the same way in the other direction: the archive it downloads and the folder it extracts it to
(the vault database in clear: key slots, password hashes, titles, paths) are made in the same `tmp/` folder of the data
folder, as `work-*`, owner-only (mode 0600 files and 0700 folders on Linux and macOS, an owner-only ACL on Windows),
not in the operating system's temp folder, and are removed when the join or restore ends. If the process is killed in
between, the leftover is removed at the next start of the node, of the phone app, or of `bmb join`.

## Restore this node only

Use it to bring **this** node back from one of its own snapshots, or from a copy made by another node in the
same network, without touching the rest of the network — for example after a damaged database, or to start a
fresh copy of a vault on a spare machine. Both cases require the same vault master key. A snapshot from a
different vault cannot be opened here, regardless of the password entered.

What happens, in order:

1. The master password is checked and the vault is unlocked for the work. If "Create backup before restore" is
   ticked, a complete snapshot of the current state is made first (it is encrypted, and it is the way back).
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
5. The staged database replaces the live one, the media are swapped in, and the vault is locked.

**What you have to do afterwards**

- Sign in with the master password the snapshot was made under. The sign-in page tells you this node is now a
  new node and shows its name and id.
- **Join your other devices again.** They still know the *old* node, which this one no longer is: on each of
  them join this node (new node id) with the master password, as for any new device.
- **Pair the blind copies again** (Admin → Blind nodes, and on the phone). Their trust in the old node ended with
  it.
- Devices that were never joined again stay on the old network. Nothing is deleted there.

From the command line: `bmb snapshot restore-standalone <file>` asks for the master password and does the same.

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
   switched on for this node (Admin → Nodes). It then downloads the copy from this node (or from another node
   that already has it), checks the hash, and applies it. A node without Auto-restore keeps the event pending and
   does **not** restore. (Applying a pending restore by hand is not offered in the Admin UI: switch
   Auto-restore on for the originator and the node picks the pending event up the next time it syncs or is
   unlocked. There is no UI prompt, notification, or manual approval path.)
3. Blind copies are not restored: they are flagged for a reseed.
4. Keep this node running and reachable until the others have fetched the copy; they download it from here.

What stays as it is on every node: its identity, users, key slots and the nodes it trusts.

From the command line: `bmb snapshot restore-network <file name or id>`; the API is
`POST /api/snapshots/restore-network` (superadmin, not an agent key; the Admin page also sends the master
password, which the server then re-checks).

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
