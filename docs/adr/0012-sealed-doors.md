# ADR 0012: Sealed doors (door format v2)

## Status

Accepted (2026-11-16) for release 2.12.0 ("People leave"). This ADR covers the door format, its upgrade, the boot-store
wrapper pruning rule, the shape of a device recovery box, and the quiet rotation that re-seals the doors (with the bridge for
stragglers), section "The quiet rotation".

## Context

Every way into a vault - a superadmin's password, a recovery key, the OS or server auto-unlock secret, an auto-unlock
agent's key - is a *door*: a row (`tbl_key_slot`, or the wrapped DEK of a `tbl_agent` row) that holds the master DEK
wrapped with AES-256-GCM under a KEK derived from the door's secret. Only somebody holding the secret can ever write such
a door again. That is why a DEK rotation today deletes the doors of everybody but its initiator: the new DEK cannot be
put behind a door whose secret is not in the room. People who stay must then re-register, recovery keys and auto-unlock
agents are lost, and the old DEK has to stay reachable through a local chain (NEW under OLD) so that a straggler's door
still leads somewhere (H9 and H10 of the release plan).

A rotation that keeps everybody's door needs a door that can be written *to* without its secret.

## Decision

1. **A door is a sealed box (format v2).** The KEK is derived exactly as before. From it the door derives an X25519 key
   pair, and the master DEK is sealed to the public key:

   ```
   door_sk = clamp(HKDF-SHA256(ikm = KEK, salt = door salt or empty, info = "bmb-door-x25519-v1"));  door_pk = X25519(door_sk, 9)
   seal    : ephemeral pair; z = X25519(eph_sk, door_pk), an all-zero z is refused;
             key = HKDF-SHA256(ikm = z, salt = door_pk || eph_pk, info = "bmb-door-v2\0" || kind)
             box = 0x02 || eph_pk(32) || nonce(12) || AES-256-GCM(key, dek, aad = 0x02 || door_pk || eph_pk)       -- 93 bytes
   open    : KEK -> door_sk -> z = X25519(door_sk, eph_pk) -> key -> AES-GCM -> VerifySentinel
   ```

   The kind is the slot type (`user`, `recovery`, `os_auto_unlock`, `server_auto_unlock`, `quick_unlock`) or `agent`, so
   a box cannot be moved from one kind of door to another. The door salt is the Argon2 salt of a password door, the
   agent's salt, or none for a secret-keyed door. `DoorBox` (`libs/BeeMemoryBank.Crypto`) is the primitive, with fixed
   test vectors computed independently; `Doors` (Vault) is the one place that opens either format and writes v2.

2. **Storage.** The box lives where the wrap lived (`encrypted_master_dek`, `tbl_agent.encrypted_dek`); `iv` keeps a copy
   of the box nonce because the column is NOT NULL. Migration 046 adds `door_version` (1 or 2), `door_public_key`,
   `dek_fp`, `owner_user_id`, `created_by_user_id` and `label` to `tbl_key_slot`, and `door_public_key`, `dek_fp`,
   `created_by_user_id` to `tbl_agent` (`kdf_version = 2` marks a sealed agent door; its KEK is the HKDF of version 1).
   The boot store copies `door_version` and `door_public_key` (boot migration B003). The content hash of a v2 door
   appends its format and public key; a v1 door's hash is unchanged, so boot stores and server enable records written
   before stay valid.

3. **New doors are always v2; a v1 door is opened for as long as it exists.** The legacy single `password` slot stays v1
   (its type changes when it is migrated to a `user` slot, and a box is bound to its kind); it is upgraded after that.

4. **Upgrade at first use.** A v1 password or recovery door becomes v2 at the first successful use of its secret (an
   unlock, a password check, a recovery-key reset, a superadmin's sign-in while the node is open). The OS and server
   auto-unlock doors upgrade themselves after an unlock, as soon as their secret is readable; the server rewrites its
   enable record in the same step (the record names the old and the new door while the door changes, so a crash never
   leaves the slot "stale"). An auto-unlock agent upgrades at its first authenticated request. The lazy rewrap after a
   peer's rotation writes a v2 door. Every replacement follows the door-replacement order of
   [encryption.md](../encryption.md#an-encrypted-node-the-database-key-and-the-boot-store): the new door is written to
   the boot store as pending before the commit, the row is replaced in place only while it still holds the door that was
   opened, and the boot store is rewritten afterwards.

5. **Every door names its DEK** (`dek_fp`): written whenever a door is written with the DEK in hand, filled for a v1 door
   when it opens. Unknown (`NULL`) for a v1 door nobody has used since the upgrade.

6. **Wrapper pruning rule.** The boot store keeps a database-key wrapper for the current DEK and for every DEK some door
   names; while any door's DEK is unknown it keeps a wrapper for every DEK the session holds, as before; with no DEK in
   the session (locked, closing) it prunes nothing. A sealed node opens its file before the database (and before any lazy
   rewrap can run), so it must never lose the wrapper a remaining door needs; this rule keeps it, and drops the wrapper
   of an old DEK as soon as no door names it.

7. **Device recovery box of a v2 door.** A `device` box of a v2 `user` door is `wrapped = 0x02 || door_pk(32) || box(93)`
   beside the slot's unchanged salt and Argon2 preset; `RecoveryBoxKdf.IsWellFormed` and `RecoveryBoxCrypto` accept and
   open both shapes (only a device box may have the new one). One active box per (author, kind) is unchanged. Nodes
   advertise the capability `door-box-v1`.

8. **A new door kind, `quick_unlock`**: a secret-keyed door like the auto-unlock ones, for a phone that keeps only a
   32-byte secret (create, open, remove; `QuickUnlockDoors`).

## The quiet rotation

9. **A rotation re-seals; it never deletes a door of somebody who stays.** On the initiator and on every peer, inside the
   rotation transaction, every v2 door (`user`, `recovery`, `os_auto_unlock`, `server_auto_unlock`, `quick_unlock`) and every
   v2 auto-unlock agent is sealed to the new DEK from its public key (`Doors.SealTo`) and names it; each goes to the boot store
   as a pending door before the commit. The initiator's own door is sealed for its password as before. Nobody's
   `tbl_user.key_slot_id` is cleared. Before the transaction the node seals a v1 OS or server door whose secret it can read
   (`IUnlockedDoorUpgrade`). A v1 password or recovery door is kept; a v1 secret-keyed door and a v1 (kdf 0/1) agent are deleted
   as before (stated exception to "nobody redoes anything": an agent key never used since the update is re-issued).
10. **The server checkbox stays on.** The enable record names the old and the re-sealed door and both DEKs before the commit
    (`PrepareRotationAsync`), the new ones after it (`OnDekRotatedAsync`); a crash leaves it armed and the next unlock settles
    it. Only a deleted v1 server door still switches the checkbox off.
11. **The bridge ends by itself.** The chain row (NEW under OLD) is written only while the node has a v1 door; without one the
    rotation clears every chain row. The change that removes the last v1 door clears them in the same transaction
    (`DekBridge`). The Master key page shows a remaining bridge (whose doors, since which rotation) and removes it on request
    (the v1 doors are deleted; their owners set a password again). `tbl_dek_retired_link` and the retired-DEK store are never
    purged.
12. **The rotation writes its own retired link** (OLD under NEW) in the transaction, with a marker; the reconciler publishes it
    as `retired_link_set` and drops the marker. A network restore merges `tbl_dek_retired_link` (`SnapshotTables.Monotonic`).
13. **Removal rotations apply by themselves.** `reason = offboarding:<process id>` on the proposal and the commit; from a node
    seen as a superadmin, every unlocked full node applies it whatever `auto_accept_dek_rotation` says (a locked one at its next
    unlock), with a warning and an audit entry.
14. **Device boxes.** A v2 device box only when every active peer shows `door-box-v1`; until then the password's KEK publishes
    the old shape (sign-up, password change, sign-in once per rotation). At a rotation and when a door is deleted the node
    publishes its box again from the door its latest box copied, else from the sealed door of the active superadmin who signed
    in last. `RecoveryKeyResolver` tries the newest epoch first and unwinds links after each opened box.
15. **One key-operation lock** per node (`KeyOperationLock`, keyed by the vault's database id) for rotation and identity re-key; a rotation is refused while an
    offline re-key holds its lock, and the offline re-key refuses while a rotation is in flight or not completed.

**What somebody with the OLD DEK gains.** A database copy from before the rotation: what it held (data already given). A copy
from after it, on a node without v1 doors: nothing — no row yields the NEW DEK (H10 closed; tested by scanning every row of such
a copy). On a node with a bridge the chain row does: visible on the Master key page and removable. Commit events of pre-ADR-0006
rotations in an uncompacted log keep NEW under OLD for those rotations.

## Why a box and not a wrap

A wrap can only be written by whoever holds the KEK. A box can be written by anybody who knows the door's public key, and
opened only with the secret. That is the property a quiet rotation needs (it re-seals every door to the new DEK without
anybody's password), and it is what a shared node needs to hold a door for a person whose password it never sees. The
cost is one X25519 agreement and two HKDF calls per open, next to an Argon2id derivation that dominates.

## What an attacker with the database file gains

Nothing new. The public key reveals nothing about the secret beyond what the box already allows to be checked: a guess of
a password still costs one Argon2id derivation with the slot's parameters, then the door key, then the AES-GCM tag, exactly
as a guess against a v1 wrap. A secret-keyed door (auto-unlock, quick unlock, agent) is as strong as its 128- or 256-bit
secret. The `dek_fp` column names a DEK by a domain-separated SHA-256, as recovery boxes and chain links already do; it
does not help to find the DEK. The owner column says whose door a row is, which `tbl_user.key_slot_id` already said. On a
sealed node all of this is inside the encrypted file; the boot store holds the same public keys and boxes as the doors.

## What this does not change

- The KEK derivations, the Argon2 parameters, the sentinel check after every open, the per-peer envelopes of a rotation
  (ADR 0006) and the DBK chain (ADR 0010).
- The join protocol: a host whose door is v2 answers with a v1 wrap of the same DEK under the same password-derived KEK,
  salt and parameters, so a joiner of any version opens it.
- The iPhone's quick unlock outside the vault (`QuickUnlock`) is not moved to the new door by this part.

## Consequences

- No downgrade after a v2 door exists: an older build cannot open it.
- A device box of a v2 door cannot be opened by a node without `door-box-v1`; it is published only once every peer shows
  the capability (decision 14).
- The upgrade happens by itself and invisibly; a door that is never used stays v1 and keeps every candidate wrapper.
