# ADR 0010: A full node's database is encrypted and closed until unlock (Step B)

## Status

Accepted (2026-10-09; Step A and Step B ship together in 2.6.0).
Implemented in 2.6.0 as "Step B": dormant until the owner presses "Encrypt this node" on a full node, which requires
Step A to be activated there first. Supersedes in part [ADR 0005](0005-plaintext-metadata.md) for the raw file of an
encrypted node. The contracts the implementation shares are in [docs/design/step-b-spec.md](../design/step-b-spec.md).

## Context

After Step A ([ADR 0009](0009-metadata-confidentiality.md)) no metadata leaves a full node in clear, but the node's own
`beememorybank.db` still holds every title, path, tag, user name, agent name, audit line and the whole plaintext event
history in an ordinary SQLite file (`libs/BeeMemoryBank.Storage/Sqlite/DbConnectionFactory.cs:27`), and so do its WAL,
`chat.db`, the staging copies of every snapshot and any file-level backup of the data folder. The requirements: a stolen
disk must be useless; the database is closed until unlock; the database key exists on disk only wrapped under the
master-key chain; a locked node keeps no open key, still receives, stores and relays opaque traffic and applies it after
unlock; agents' unlock material stays readable before unlock; the login on such a node is password-only first, the user
name is checked after unlock. The engine is SQLite3 Multiple Ciphers (SQLCipher 4 page format, raw
256-bit key), swapped in 2.5.2 with databases still plain. The desktops keep their opt-in OS auto-unlock with a
warning, and a server gets a checkbox "start unlocked after a restart", off by default, never turned on by an upgrade.

## Decision

1. **Page encryption of the whole database, not of columns.** On an encrypted ("sealed") node `beememorybank.db` and
   `chat.db` are SQLCipher 4 files under raw page keys derived from a random 32-byte database key (DBK) per node.
   Inside the open database nothing changes: the same schema, the same SQL, the same single-file transactions (the
   reasons of ADR 0005 and ADR 0003 hold).
2. **A small plain boot store** (`beememorybank.boot.db`) is the only file read before unlock. It holds copies, not
   originals: the key slots and the wrapped DEK of auto-unlock agents ("doors"), the DBK wrapped under each master DEK a
   door may yield, the node's public identity, a mirror of the whitelist, and what arrives while the database is closed
   (sealed and control events, ciphertext blobs, position reports). A golden allow-list fixes its columns. No user name,
   no agent name, no title, no clear content event ever goes there.
3. **The DBK exists on disk only wrapped under the master-key chain** (door -> master DEK -> DBK). Password changes and
   DEK rotations change doors and wrappers, never pages; a rotation's initiator ends with one wrapper, under the new
   DEK, so a node the rotation left out cannot open the file from a stolen disk.
4. **Closed until unlock.** A sealed node starts with no key: only health, the login, the unlock and the sync relay
   answer; every other route answers 503 `vault_sealed`. A door (password, recovery key, agent key, OS or server
   auto-unlock) yields the master DEK, which unwraps the DBK, which opens the database; locking closes it, clears the
   pools and the keys.
5. **A closed node is a relay.** Received sealed and control events are stored in the boot store, served onward with
   sequence numbers above every row of the closed log, and replayed in order through the ordinary applier when the
   database opens; revocations take effect at receipt. Clear content is refused until unlock.
6. **No plaintext image of the vault on disk, ever.** Staging copies are encrypted with the vault key; containers that
   carry a SQLite file (snapshots, baselines, join archives) get its plain image from memory or a stream through a page
   codec, so their formats stay what 2.5 and 2.6 readers understand.
7. **Owner-started migration, per node** ("Encrypt this node"): preflight (it refuses while a plain copy of the vault lies
   in the data directory; the scan reads every file by its content and skips no folder, opens every member of every tar, tar.gz and zip whatever
   the member is called, treats an archive it cannot read to the end as a finding, and it refuses a live file that has a second
   name), an encrypted pre-encryption snapshot as the way back, a crash-safe journal, an encrypted copy built
   with `VACUUM INTO`, verification (integrity, counts, log positions, sentinel, sample decryption, tree and search, a raw-byte
   scan), and a swap resolved at every start that **wipes and deletes the plain files it replaced** in the same pass - also when
   the journal is lost or unreadable (a leftover replaced file is a repair state; a leftover the resolver cannot place stops the
   start with a named refusal and changes nothing). No plaintext copy of the database survives the switch anywhere in the data
   directory (a stolen disk must be useless), and a sealed pair is not started - at any start, by name - while a plain `chat.db` or a plain WAL or
   journal of either database lies beside it; rollback restores the encrypted snapshot. The snapshot carries a **key envelope** (the
   password and recovery key slots of that day), so that it still opens after a DEK rotation, a password change or the reset the
   rollback starts with.
8. **Auto-unlock is the owner's explicit choice, split by what the node is:** a desktop (started by the desktop application)
   keeps the OS auto-unlock with the warning that the stolen-disk protection then rests on the OS account password; every
   other node is a server and gets "Start unlocked after a restart" on every operating system, plain or sealed, off by default,
   behind two warnings and a typed phrase, created by nothing but the owner's explicit call (no upgrade, migration, restore, import,
   join, re-key, default or configuration), removed by a DEK rotation and by a reset. It needs three parts that agree - the key
   slot (data), the secret in an owner-only file, and an **enable record** that is local to the server and in no backup, snapshot or
   package - so a file-level restore, which brings the slot back but never the record, starts the server locked even when the
   operator's key file is still mounted (a fresh directory, another machine or a changed slot); the stale slot is retired at the first
   start. The one exception is an in-place restore over the very server that enabled it, with a byte-identical slot: its own record and
   key file are still there, so the earlier explicit choice stays in force - preservation, not a new enable.
9. **The metadata key rotates with every DEK rotation as part of the rotation:** Step B's promise to a
   node a rotation left out needs it; the change is Step A's (spec 14.4 item 7).

## Consequences

- A stolen disk, a copied data folder, a snapshot or a file-level backup of an encrypted node shows no metadata without
  a door. Still visible: file sizes; the boot store's allow-listed columns (wrapped keys as exposed as today's key
  slots, agent key hashes, the node's name and key, the peers' names, addresses and pins, opaque events received while
  locked); free-space remnants of the plain files from before the migration (wiped in place, but SSDs and copy-on-write file
  systems may keep old blocks); the OS or server auto-unlock secret when the owner turned it on.
- A restarted encrypted server stays inert for content until someone unlocks it (the owner or a superadmin agent), by
  design; it keeps relaying sealed traffic meanwhile, but cannot serve log rows older than its last open head.
- The only file-level backup of a sealed node, the operator's and the product's own pre-update copy alike, is a **quiesced set**:
  the three files copied while nothing can write any of them (a stopped node, or a closed node held shut with the relay writes
  paused). A copy of the files of a running node is not supported.
- Losing the boot store loses the database file: it must be backed up with the database (every product path does);
  snapshots, peers and blind copies remain ways back.
- A stolen disk shows no plain page of a sealed node's own files, and a plain copy planted or left beside them is found: by content
  in every file and archive member before and after the switch, and at every start by a cheap check of the chat database and the WAL
  and journal files (a sealed file's WAL has a plain header and encrypted pages, so the check reads the pages, not the magic).
- Bulk work is 1.5 to 3 times slower on the encrypted file; interactive paths are not
  measurably slower; temporary SQLite data stays in memory on encrypted connections.
- New vaults stay plain in 2.6.0; the owner encrypts each full node after activation. Rollback = the encrypted
  pre-encryption snapshot (reset, set the node up again with the old password, restore it through its key envelope) or the owner's
  offline pre-cutover backup; the chat history is in no snapshot. The snapshot opens with the password of the day it
  was taken, also after the owner changed it, until the owner deletes it.

## Alternatives rejected

- **Moving the event log, the blobs or the key slots into the plain file** (three stores): transactions that write
  content, blob and event together, or rewrap every key in one rotation, would span two WAL files, which SQLite does not
  commit atomically.
- **Usernames and password hashes before unlock** (a plain bootstrap store): not needed, the unlock is password-only.
- **The DBK sealed by the OS key store so the node opens at boot** : protects copies but not a stolen
  machine; offered only as the owner's explicit auto-unlock choice.
- **Encrypt-then-delete staging files:** deleted plaintext survives in free space; the page codec avoids writing it.
- **Keeping the old plain database aside as the rollback copy:** a complete plaintext copy on the
  disk contradicts "a stolen disk must be useless"; the rollback is an encrypted snapshot instead.
- **A snapshot that opens only with the node's current DEK as the way back:** a DEK rotation (every rotation makes a new DEK) or the
  reset the rollback starts with would leave no key; the key envelope is what makes the way back real.
- **Treating every WAL or journal header as a plain copy:** a sealed file's own WAL and journal have the same plain header as a
  plain one (only their page images are encrypted), so that rule would refuse every crashed or open sealed node; the check reads
  the page images instead.
- **Detecting a file-level restore to switch the server checkbox off:** a restored directory looks like a restart, so the product
  cannot tell them apart; the enable record that no backup carries makes the question unnecessary.
- **A home-made encrypted VFS or column encryption:** rejected for the engine and by ADR 0005.

## Amendment (2.6.2, BMB-306): encrypted from the first day, and no way back

The owner's rule for 2.6.2 replaces three decisions above:

- **New nodes are encrypted at setup.** The Web setup, `bmb init`, a Docker start set up through either, `bmb join` and the restore wizard
  end with an encrypted database under the setup password and metadata encryption on (protocol 4, readiness activated). One routine does it
  (`FreshNodeSealing`: it opens the new vault, activates when the node starts a network, and runs the ordinary migration over what is a few
  kilobytes of identity, key slots and schema). A node that cannot be encrypted is reset, never left open. Auto-unlock stays off.
- **A new node joins only a node on protocol 4**, with a clear refusal and no compatibility mode.
- **No way back.** The pre-encryption snapshot, its key envelope and the five-step rollback are gone, and so is the Admin block that managed
  them: the "Encrypt this node" warning says that going back is not possible from the node. **No restore produces an open node:** a node whose
  database is not encrypted refuses every restore into it, and an archive that holds an unencrypted database is refused wherever it goes. The
  rejected alternative "keeping an encrypted snapshot as the rollback copy" is now the decision: the rollback copy is the owner's own backup, kept
  outside the data folder, and the node does not restore it.

Nodes that were set up before 2.6.2 stay open until the owner presses "Encrypt this node" (an update never does it for them).
