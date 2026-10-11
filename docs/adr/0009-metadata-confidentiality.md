# ADR 0009: Metadata is encrypted in sync, on blind copies and in every package (Step A)

## Status

Accepted (2026-10-09; Step A and Step B ship in one release). Implemented in
2.6.0 as "Step A", **switched off** until the owner activates it once every node runs 2.6.0; dropping the protocol-3 code
goes into a later regular release, not a separate enforcement release. Supersedes in part [ADR 0005](0005-plaintext-metadata.md):
what ADR 0005 says about the full node's own database stays true until the owner encrypts that node with Step B (also 2.6.0,
[ADR 0010](0010-database-closed-until-unlock.md)); what it
implies for the sync stream, blind copies, join snapshots, replica packages and blind backups does not.
The contracts the implementation shares are in [docs/design/step-a-spec.md](../design/step-a-spec.md).

## Context

Bodies, versions and media bytes are envelope-encrypted, but every protocol-3 event carries its metadata in clear
JSON: titles, tree paths, folder names, tag names, file names and types, protection hints, timestamps
(`libs/BeeMemoryBank.Sync/EventPayloads.cs:212-240, 331-393`; built by `libs/BeeMemoryBank.Vault/Sync/EventLogger.cs:41-330`).
The signature covers the payload as sent (`libs/BeeMemoryBank.Sync/EventSignature.cs:12-24`), so whoever holds the
log can read it. That includes every peer that only relays:

- a blind node (Docker on a foreign VPS) and the blind copies on phones and desktops apply every event into the
  same tables a full node has (`EventApplier.cs:221-299`; `docs/blind-node/COMPOSITION.md`), so their databases,
  their restic backups and the phone backup files hold the whole tree in clear;
- the blind package (seed, reseed, replica, restore) is a plain SQLite copy of those tables
  (`server/BeeMemoryBank.Api/Services/Blind/BlindPackageBuilder.cs:66-70`, `encryptDb: false`);
- the join snapshot is sent as a plain SQLite file (`server/BeeMemoryBank.Api/Endpoints/SyncEndpoints.cs:327-331`);
- a TLS-terminating proxy in front of a hub sees the same events and packages.

A blind program contains no code that can open a body (ADR-0002 of the blind node), but no key is needed to read
the metadata. The goal is that a blind copy, its disk, its backups and anything on the wire hold no
structure: not the names, and not the shape of the tree either.

Two designs were compared. Deterministic per-field tokens would have let
blind nodes keep applying events, but they leave the tree shape, equal names and shared tags visible. The opaque
design was chosen.

## Decision

1. **A vault-wide metadata key (MEK).** 32 random bytes, created by an unlocked superadmin at activation, stored
   only wrapped under the master DEK (AES-256-GCM, AAD bound to key id, generation and DEK fingerprint) in a
   replicated row that travels as a clear control event (`metadata_key_set`). A blind node stores and relays the
   row and cannot open it. **A new MEK is created with every DEK rotation** and wrapped under
   the new DEK only, so a peer that the rotation left out (ADR 0006) cannot read metadata written afterwards. Old
   MEKs stay usable through the existing chain of retired DEKs and recovery boxes. The MEK is held only in memory
   while the vault is unlocked; **no open copy is ever written to disk** (a stolen disk must be useless).

2. **Sync protocol 4: one sealed envelope per content event.** Every event that can carry human-authored
   metadata (article, comment, folder, media, concept-tag and hard-delete events) is written as one outer event of
   type `sealed`, protocol 4, with no article id: the inner type, the article id and the complete protocol-3 payload
   are encrypted together under the MEK with a random 96-bit nonce (AES-256-GCM, padded to 64 bytes). The clear
   part is limited to the event id, author node id, Lamport time, creation time, the key id, the nonce and the list
   of ciphertext blob hashes the event needs. The existing Ed25519 signature covers the whole envelope unchanged;
   the clear fields are also the AEAD's additional data. Control events (whitelist, DEK rotation, recovery
   material, anchors, checkpoints, restore, the MEK record and the activation) stay in clear: blind nodes need them
   to authenticate, route and recover, and they hold no human-authored metadata except peer display names.

3. **Blind copies hold no structure.** After activation a blind node, a blind phone and a
   blind desktop copy keep: a small operational database (identity, whitelist, positions, the clear control
   tables, the ciphertext blob store), the original signed protocol-4 events as received ("deltas"), and the
   newest **baseline**: a filtered copy of the replicated tables built by an unlocked superadmin full node,
   encrypted as a whole in a chunked authenticated container under a key derived from the MEK, and signed by its
   producer. The content tables of a blind database are emptied at the first adoption and stay empty. A blind
   node never opens, merges or rebuilds a baseline; it trims deltas only when a newer baseline from a superadmin
   covers them, and frees blobs no baseline or delta references.

4. **Full nodes keep plaintext tables in Step A.** An unlocked full node opens each sealed event and applies the
   inner event exactly as today, so search, tree, ACLs and every SQL query stay as they are (ADR 0005's reasons
   still hold for the live database until Step B). **A locked full node keeps receiving, storing and relaying
   sealed events** and applies them, in order, after the next unlock: it keeps a received position per peer and an
   applied position for itself, applies clear control events at once, and remembers for each waiting event whether
   its author was an active (super)admin when it arrived, so a later revocation cannot make the mesh diverge.

5. **Package format 2.** Seed, reseed, replica, restore packages and blind backups carry the baseline (container,
   signed manifest, blob list), a clear control database (MEK rows, activation, recovery boxes, retired links,
   anchors, sealed secrets), the deltas after the baseline's cut and, where needed, the ciphertext blobs; the
   package as a whole is signed by the node that serves it. 2.5 readers refuse it by its format string. The join
   snapshot after activation is encrypted with the same container under a key derived from the master DEK the
   joiner has just received; a producer that is locked cannot serve a join.

6. **Activation is an owner action on 2.6.0, not a release.** 2.6.0 declares protocol 3,
   writes protocol 3 and package format 1, and can read protocol 4. Each 2.6.0 node advertises the capability
   `metadata-envelope-v1`. A readiness report lists every active peer with what it last declared, directly or as
   seen by a peer; activation is refused while any active peer has not shown the capability. Activation creates
   the MEK and a signed `metadata_activation` event; every node that applies it declares protocol 4, writes sealed
   events and package format 2, and requires protocol 4 or the capability from its peers, so 2.5 nodes are cut off
   in both directions. Every blind copy is reseeded through the existing reseed paths. iPhones need 2.6.0 builds
   first or are revoked and paired again.

7. **Protocol-3 events written just before activation are applied, never forwarded to a blind peer.** A node
   that authored a content event before it heard of the activation wrote it in clear; full nodes apply it as
   today, every full node filters such events out of what it serves or pushes to a blind peer, an activated blind
   node drops one it is offered, and the full node that applied it makes its next baseline early, so blind copies
   get the change in sealed form.

8. **Hard delete on blind copies is delayed until the next baseline.** A sealed hard delete is
   relayed like any event; its ciphertext stays on blind copies until a newer baseline covers it and the trim and
   blob clean-up remove it. A full node that applies a hard delete requests an early baseline.

9. **Blind copies show no note count**: baseline age, number and size of deltas, stored bytes.
   This applies from 2.6.0 on, activated or not.

10. **Rollback after activation is a restore of the pre-activation backup.** There is no
    deactivation event and no dual sending: a protocol-3 twin of a sealed event would undo the protection.

## Consequences

- **What a blind operator, a relay, a proxy or a stolen blind disk still sees** (accepted): the
  number, size and timing of events; which node wrote each event and its Lamport time; that an event is a content
  event (but not which kind) or a named control event; ciphertext blob hashes and sizes; peer display names,
  addresses, TLS pins, positions and the trust graph; baseline sizes and dates. Not visible: titles, paths, folder
  and tag names, the tree shape, which events concern the same note, file names and types, hints, authors' names,
  hard-delete targets.
- **What Step A does not protect:** the full node's own database, WAL, file-level copies and the owner's own
  backups of a full node (Step B); anything an authorised user or agent asks an unlocked node for; the TLS
  terminator of the Web UI and MCP; all historical copies made before activation (old blind restic snapshots, old
  phone backups, old packages, the old database file a reseed keeps aside). The product lists them; the owner
  retires them.
- **Locked full nodes no longer apply content while locked.** A restarted server that nobody unlocks keeps
  relaying and storing, but its own tables lag until the next unlock (a stolen disk must be useless). Joins, baselines,
  compaction, network restore and DEK rotation are refused while events wait.
- **Restore from a blind copy** decrypts the baseline on the restoring full node, replays the deltas through the
  ordinary applier and only then checks the newest state anchor; a blind copy that withholds deltas is detected
  there, not prevented.
- **Mesh upgrade order matters:** every node must run 2.6.0 before activation; afterwards a node that was offline
  on 2.5 is upgraded before it reconnects, or revoked and joined again.
- **Cost:** interactive paths are unchanged; each sealed event costs one AES-GCM operation on a few hundred
  bytes; a baseline is a filtered database copy plus one streaming encryption.
- Golden files that change on purpose: `docs/migrations.golden.txt` (migrations 037-039),
  `docs/full-node/ROUTES.golden.txt` (readiness and activation routes), both `DI.golden.txt`,
  `docs/vault-split/vault-types.txt` and `shared-types.txt`. `docs/blind-node/ROUTES.golden.txt` does not change.

## Alternatives rejected

- **Deterministic tokens per field** (blind nodes keep applying events on tokens): cheaper, but the tree shape,
  equal names and shared tags stay visible to the blind operator, which the owner did not accept.
- **Encrypting the metadata columns of the live database** (ADR 0005 option 1): every tree, search and ACL query
  would become decrypt-then-filter. Step B encrypts the whole database file instead.
- **A per-blind-peer copy of the MEK, or a plaintext twin for old peers:** either defeats "blind" (the same
  argument as ADR 0006 against shipping a legacy wrapper beside a confidential one).
- **An open copy of the MEK on a full node's disk so a locked node can keep applying:** contradicts
  "a stolen disk must be useless".
- **A blind node that compacts by building its own baseline:** it would need to open the vault.
