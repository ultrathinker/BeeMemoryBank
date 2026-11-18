# ADR 0013: Node identity re-key

## Status

Accepted (2026-11-17) for release 2.12.0 ("People leave"), plan WS-D. Migration 049 (`tbl_node_key_history`,
`tbl_node_key_conflict`), capability `identity-rekey-v1`.

## Context

Every node signs what it sends (events, packages, anchors, the sync handshake) with its Ed25519 identity key. The seed is
stored in `tbl_node_identity`, wrapped under the master DEK, and so it is inside every full backup of the node. Somebody who
took such a backup while they were an administrator, and still knows an old master password, can open it and sign as the
node. A DEK rotation does not help: it changes the master key, not the identity key. A node needs a way to get a new identity
key in place - same node id, same data, same peers - after which the old key signs nothing the network accepts.

## Decision

1. **The event.** `node_key_rotated` is a control event (never sealed, superadmin-only, `EventClasses` Control). Payload:
   `node_id, old_public_key, new_public_key, counter, rotated_at, reason, old_key_proof, new_key_proof,
   checkpoint_lamport_ts, own_event_ids`. It is signed by the OLD key (it is the node's last event under it). Both keys sign
   the transition statement

   ```
   "bmb/node-key-rotation/v1\0" || node_id (RFC 4122, big-endian) || old_pk || new_pk || counter (int64, big-endian)
   ```

   The domain tag and `old_key_proof` are additions to the plan: the tag keeps the statement from ever being a valid
   signature over anything else, and the old key's proof makes every link of the chain (point 4) verifiable on its own,
   without the event that carried it.

2. **The counter** is the number of the node's rotations: `MAX(counter)` of its rows in `tbl_node_key_history` + 1, so no
   column is added to the whitelist and the boot whitelist mirror is unchanged. A receiver accepts counter = held + 1 only;
   a gap is deferred (`NodeKeyRotationGapException`), an older or equal counter with the same event is a re-delivery.
   The offline vault re-key keeps the node's own rows (its identity seed is kept, so the chain is still true) and clears
   those of the revoked peers: the next rotation continues the counter the old peers hold, and a node that joins later
   gets the rows in the join archive.

3. **Receivers.** The rotation is applied only to an active whitelist row whose key is the rotation's old key: the row's key
   becomes the new one and a history row is written (`valid_until_lamport` = the rotation's Lamport time). The peer's sync
   tokens, channel rows and cached contact state are dropped, as for a revoked peer; it stays a peer and authenticates with
   its new key. `tbl_node_key_history` is replicated (join snapshots, blind packages as a control table, network restore as a
   MERGED monotonic table) and compaction keeps the newest 16 rows of each node.

4. **ONE helper verifies every signature of a node** (`NodeEventVerifier`): the current key, or a retired key under a rule.
   *Live* (sync, relays, quarantine replays): a retired key signs an event only when the event is above the rotation's
   checkpoint, at or below the rotation's own Lamport time, AND its id is on `own_event_ids` (the node's own events above its
   checkpoint, at most 50 000, listed by the node itself in the rotation). *Package* (a signed package, a baseline, a
   snapshot manifest, the node's own echo): a retired key signs what is at or below its rotation's Lamport time. History that
   arrives with untrusted data is used only when its links lead, signed by both keys, to the key this receiver trusts
   (`ChainedTo`). A guard test lists the few files allowed to call Ed25519 verification directly.
   A node that holds events of a re-keyed node signed by the retired key, above the checkpoint and not on the list, raises
   the `retired_key_unacknowledged_events` attention flag.

5. **Competing rotations.** A second valid rotation of the same (old key, counter) with another new key means somebody else
   holds the old seed. The receiver records an identity conflict (`tbl_node_key_conflict`, node-local) and refuses the node's
   events until a superadmin's device settles it with `node_key_confirm` (naming one of the two rotations; a node never
   confirms its own key). Admin > Node identity key lists open conflicts and offers the confirmation.
   While the conflict is open, only the competing rotations of the conflicted counter are taken (a re-delivery, or one more
   contender recorded); every other event of the node, its later rotations included, is deferred and retried after the confirm.
   The confirm keeps what this node holds when the confirmed key is the one it already took for that counter (the rows after
   it chain from the genuine key, and the node's key stays its newest, never rolled back). Otherwise every row from that
   counter on came from the wrong side - a later row chains from the wrong key - so those rows are deleted, the confirmed
   rotation's row replaces them and its key becomes the node's key; the genuine node's deferred later rotations then apply.
   This rule never re-arms a thief (no row of the wrong side survives a confirm against it, and none can be added while the
   conflict is open) and never splits the genuine node (its later rotations either stay or wait and apply).

6. **Pins.** `/api/sync/identity` returns the node's newest 16 links (`KeyChain`). `PeerKey.Follow` is the one check that a
   declared key is the pinned one or that the signed chain leads from the pin to it. It is used by the sync client's
   identity check, the channel probes (server, iOS, Android, `PeerDialer`), `PeerAddressService`, the blind invitation and
   join probes, and by `/api/sync/authenticate`, which lets in a caller whose re-key it has not applied yet when the chain the
   caller sends leads from the key on record to the key that signed (never for a node in an open conflict; nothing is
   written from it). A blind copy moves its pin of the hub key (`BlindPhoneState.FollowHubKey`, after the round that applied the hub's rotation, from its own whitelist row and key history: no request of its own); the
   code as paired keeps its MAC. A device that was away across more than 16 re-keys is paired or joined again.

7. **The operation.** `NodeIdentityRekeyService.RekeyAsync` (page, `POST /api/node/identity/rekey`, `bmb node rekey`; also
   callable by the removal process): unlocked, a full node whose seed is in the database (a key held by a device key store is
   changed on that device), every active peer advertising `identity-rekey-v1`, the per-vault key-operation lock
   ("an identity re-key"). One transaction writes the event, the new identity row (new seed wrapped under the current master
   key) and the history row, with `secure_delete` on so the old wrapped seed is overwritten; the same transaction writes a
   node-local follow-up marker. Then: the phone pairing records naming this node are signed again with the new key and
   re-sealed; an audit entry; `node_state` with the new fingerprint and a state anchor signed by the new key. The marker is
   removed when all of these held; while it stands (a crash or a failure right after the commit), every unlock runs them
   again - the audit entry only if it is missing, a pairing record only if it is not yet signed with the new key - under
   the vault's key-operation lock, with the identity read under it, so they never sign with a key another re-key has just
   retired; while an offline re-key or another key operation holds the vault they wait for the next unlock. A peer that cannot open a DEK rotation envelope sealed to its retired key asks the
   rotation's author for a fresh one (`POST /api/sync/dek-envelope/{commitEventId}`).

8. **Restores.** The keep-identity restore mode is removed: it would bring back a retired key. A standalone restore makes a
   new node; a network restore keeps the node's current identity and merges the key history.

## What a holder of an old seed can and cannot do

- **Peers that applied the rotation:** nothing new. The retired key opens no session, its events are refused unless they are
  the node's own listed events from before the rotation, and a chain it forges (old key to the thief's key) never leads to the
  genuine key, so no pin follows it. A second rotation it signs is an identity conflict, refused until a superadmin confirms.
- **Peers that have not applied it yet:** what it could do before the re-key, until they apply it - they still trust the old
  key. What it pushed in that time is recognised afterwards (not on the list: the attention flag). A forged rotation that
  reaches such a peer first wins there until the genuine one arrives, which makes it a conflict.
- **A blind copy that has not followed yet:** the copy also checks the hub's TLS pin, which the identity seed does not give.

## Consequences

- Every device must run 2.12 or newer before a node can re-key (the page names the ones that hold it back).
- A device offline across more than 16 re-keys must be paired or joined again.
- A sealed relay with its database closed checks the current key only.
