# ADR 0014: People and shared nodes, and removing a superadmin

## Status

Accepted (2026-11-17) for release 2.12.0 ("People leave"). One ADR for the two halves of the release that decide who may open
which node: the accounts node with its replicated superadmin doors (part A), and the removal of a superadmin as a process
(part B), whose steps S1 and S2 run on the node identity re-key (ADR 0013) and on part A. Builds on ADR 0012 (sealed v2
doors, the quiet rotation) and ADR 0013 (node identity re-key). The file `0014-removing-a-superadmin.md` only points here;
the number 0014 is not used for anything else.

## Context

### Shared nodes

In a multi-person BeeMemoryBank deployment, users have two kinds of nodes:

1. **Shared server nodes** (e.g., team VPS, shared home server, standby shared replica), where any current superadmin should be able to sign in, unlock the vault, and accept new devices via `/api/join` with their own current password.
2. **Personal devices** (laptop, phone) and **blind copies**, which belong to a single owner (or hold no DEK at all).

Previously, every user and key slot was purely node-local: changing a password or removing a superadmin on one node left that person's old password, `password_hash`, and `user` key slot alive on every other shared server node. Removing a superadmin required touching every server by hand.

### People who leave

Before 2.12 taking a superadmin's rights away was one call: `PUT /api/users/{id}` with another role, or a delete. It took
the person's door on that node and nothing else. Whatever the person had learnt or planted while they were a superadmin
stayed useful: the master DEK they could read, a copy of the database, the identity seeds of the nodes, agents and
recovery keys they issued, devices they joined, invitations, other superadmins they created. A full copy of the bank that
the person still held, or an old snapshot, brought them back. An honest removal therefore has to change things on every
node, some of which may be offline, and it must survive crashes and restarts in between.

## Decision

### Part A. The accounts node and replicated doors

1. **One designated accounts node (`accounts_home`), never offered to a one-person bank.**
   - `tbl_accounts_home` (migration `050`, replicated in snapshots/joins via `SnapshotTables.Replicated`) holds `{ node_id, lamport_ts, source_node_id, event_id, updated_at }` written by the superadmin-only control event `accounts_home_set`. A replicated `accounts_home_set` is accepted only if authored by the current accounts node (hand-over), or when no accounts node exists yet, or after the current accounts node is revoked (take-over); the target must already be an active, non-blind, whitelisted `shared` full node, and every accepted change raises an `accounts_home_changed` `node_state` attention flag and an audit entry on every node. Network restore merges `tbl_accounts_home` by newest `RowVersion`.
   - The UI (`Admin → Devices` and `GET /api/shared-doors/status`) offers selecting or moving the accounts node when the network has at least two active superadmins **OR** at least one active non-blind `shared` full peer (or when this node is shared or an `accounts_home` is already set) (`ShouldOfferAccountsHome`). A single-person bank (even with personal laptops or blind nodes) never has a shared-doors offer.
   - `tbl_shared_doors_state` tracks node-local `self_ownership` (`'personal'` / `'shared'`), applied/received versions, `primary_person_id`, pending payload, and `shared_doors_error`. An offline vault re-key keeps the table (its last issued list version is a counter, not key material) and resets only what the peers sent: a re-keyed accounts node continues its list versions instead of starting at 1, which every shared node would drop as old.
   - **Only a node with a superadmin account of its own becomes the accounts node**. A replicated `accounts_home_set` that is not the current accounts node's own hand-over (a take-over, or a first designation) is refused when its target reports, in its `node_state`, no superadmin door of its own (`local_superadmin_door_count` = 0: it holds mirrors only, and would publish lists of nobody, which every shared node ignores, so the doors it holds stay live); a target that has not reported yet is deferred, not refused. A node naming itself announces its `node_state` first. The current accounts node may hand over to any shared node: it knows what it hands over.
   - **A node naming itself needs a shared mark, except at bootstrap**. A node cannot publish its own ownership, so a receiver accepts a self-designation from an active, non-blind, whitelisted superadmin full node that nobody there marked as somebody's personal device - but only while no accounts node was ever designated on that receiver (bootstrap), or when the author is already the accounts node of record there. In the **home-revoked window** (the accounts node of record is revoked on the receiver) the self-designating node must already be marked `shared` there; otherwise any superadmin full node could crown itself, for example a leaver who revokes the honest accounts node from a surviving peer and then names a node of theirs. A take-over node is a shared node, so a take-over keeps working. The window that remains: in it a node that IS marked shared may still name itself, and a receiver with no accounts node yet (a node updated from 2.10 before its replay applies the genuine designation) takes the first designation it sees. Every accepted change raises `accounts_home_changed`, which blocks a removal's S5 until a superadmin acknowledges it: acknowledge it only after checking that the new accounts node is an organization server.
   - **The attention flag can be acknowledged**. `accounts_home_changed` is raised on the nodes that receive a change; a superadmin acknowledges it on the Devices page (`POST /api/shared-doors/accounts-home-changed/acknowledge`, audited); a designation made on the node itself clears it there. A list that is still pending after its automatic retries were spent (a key operation kept the key busy) records a plain error and raises `shared_doors_apply_stuck`.

2. **Encrypted public door list (`superadmin_doors_set`) behind `shared-doors-v1`.**
   - On the accounts node, `SuperadminDoorsPublisher` builds the ordered list of active superadmins holding a v2 `user` door: `{ person_id, username, display_name, password_hint, salt, argon_memory, argon_iterations, argon_parallelism, door_public_key }`. No password, no Argon2 KEK/door private key, and no Master DEK ever appears in the event. The list carries every superadmin the accounts node holds a sealed door for, its mirrors included (a node that became the accounts node after holding another's list keeps the people who stay on the shared nodes), and never anybody a removal holds.
   - Before publishing, it checks all active non-blind `shared` peers for the `shared-doors-v1` capability. If any lack it, publishing is withheld and the status endpoint / Devices page warns naming the blocking peers.
   - The JSON document is encrypted under a fresh random AES-256-GCM content key wrapped with `DekEnvelope.Build` to each shared full peer's Ed25519 identity key (never blind peers, never personal peers). `EventInvariants` rejects any blind peer listed in `list_envelopes.peers` at ingress.

3. **Door-replacement apply on shared non-home nodes (`SharedDoorsApplier`).**
   - Unlocked shared non-home nodes (both immediately on apply and via `SessionService.TriggerPostUnlockCatchUp` on unlock) unwrap the envelope with their own Ed25519 identity seed, seal their *own* Master DEK to each descriptor's `door_public_key` (`Doors.SealTo` v2 `user` door), write pending boot doors before commit, commit mirror-row upserts (`replicated = 1`, `password_hash = ""`), removal of dropped mirror rows, removal of non-replicated local superadmins and legacy `password` slots, and `_2`/`_3` suffix disambiguation for colliding local non-superadmin usernames in a single transaction, then rewrite the boot store and clear caller/ACL caches. The same transaction writes a node-local marker; after the commit the device box is published again from the doors that are left and the marker goes. A crash in between leaves the marker, and the next apply (every unlock asks) publishes the box first, so it never stays a copy of a door that is gone.
   - If zero usable doors exist in the list, nothing local is removed (preventing accidental lockout).

4. **Door-based authentication and read-only guardrails.**
   - Replicated superadmin rows have no `password_hash` and authenticate exclusively by opening their specific v2 `user` slot (`TryOpenUserDoorAsync`, or reusing `openedSlotId` from `OpenSessionForLoginAsync` on a closed sealed node), preserving 1 Argon2id derivation parity across unknown/wrong/foreign/valid logins and validating `/api/join` against the active door list.
   - All local mutations against a replicated row (edit, delete, demote, change password, change password hint, or create local superadmin on a shared non-home node) fail closed with `ReplicatedUserException` (HTTP 409 / CLI exit 1) naming the accounts node. Recovery-key resets verify the recovery key first and only return the accounts-node refusal afterward so no username oracle is exposed.

### Part B. Removing a superadmin is a process

5. **A person is an id, and an account has a state.** `tbl_user.person_id` names the person behind an account on
   every node. Migration 051 adds `tbl_user.account_state` (`active` | `offboarding`). An account that is `offboarding`
   ("being removed") cannot sign in, has no rights (`CallerStateCache` gives it no role), gets no new door
   (`TryAssignKeySlotAsync`/`TryReplacePasswordAsync` require `active`), cannot reset its password with a recovery key,
   and cannot be edited, demoted, deleted or promoted by anything but the process. The Users page shows "being removed,
   step k of n" and links to the process; never "user" or "deleted" while a step is still open.

6. **Removing a superadmin is a process with fixed steps** (`OffboardingService`, `tbl_offboarding`,
   `tbl_offboarding_node`). It is the only way the API, the Users page and the CLI demote or delete a superadmin; it starts
   only with the acting administrator's own password and an explicit end (deleted, or demoted to a role and folders
   chosen now and stored). Preconditions (plan E3) are checked before anything changes: not oneself, not the last
   superadmin, another superadmin holds a sealed (v2) password door here, no device waits for review, no offline re-key
   lock, no key operation or rotation in flight. No step can be skipped; there is no cancel after S0.

   | Step | Where | What it closes | Still open while it waits |
   |------|-------|----------------|---------------------------|
   | S0 Lock out | accounts node: one transaction, then the work after it in the same call | in the transaction: sign-in, every browser session (stamp), the person's password door, recovery keys, auto-unlock agents and agents, remote tokens, invitations, the process and tombstone rows. Right after the commit (`FinishLockOutAsync`, same call, nothing in between): the person's personal devices revoked, the `person_removed` and `offboarding_state` events, the boxes. A crash between the two is finished at the next pass of the driver (every 20 s on an open node; on an encrypted node restarted closed, once it is opened): in that window the person's personal full device is still an active peer and can pull what was written after S0. Revoking devices before the transaction would break "nothing changes when a check fails" (a revoke is a signed event peers apply) | the current master DEK and any database copy the person already has; identity seeds they could read; their doors on shared nodes; what they planted |
   | S1 Identity re-key | accounts node and every shared full node | a stolen identity seed of those nodes: on a node that applied the re-key, the old seed opens no sync session and an event it signs is refused (ADR 0013). The driver re-keys itself with reason `offboarding:<process id>` and asks the shared full nodes with a signed `node_rekey_request` (superadmin-only, accepted only from the node that drives the process, idempotent per process: a node re-keys while its step-1 row still shows the key it had when the step began). An asked node serves it from the removal's background pass when it is open. A node is confirmed when the key this node trusts for it moves (an applied rotation), never by its `node_state` report alone, which can lag. A node whose seed is kept outside its database cannot re-key (ADR 0013 refuses it): its `node_state` says so (`identity_rekey = external`) and the page names it with what to do - pair it again as a new node, or revoke it; other refusals (a key operation running) are named the same way. In a removal a node re-keys even while some device cannot follow a new key (it lacks `identity-rekey-v1`): waiting first would keep the old seed - the one a stolen backup holds - the current key everywhere. Each such device is an S1 row ("cannot follow the new identity key: update it, or revoke it") until it advertises the capability or is revoked; the ordinary re-key from the identity page keeps the gate | the seeds until each node re-keyed, and every device that cannot follow until it is updated or revoked (the page says a stolen backup still opens the network meanwhile); the master DEK; their doors on shared nodes; what they planted |
   | S2 Shared doors | every shared full node | the person's password on a shared node (sign-in, `/api/join`): the accounts node publishes `superadmin_doors_set` without the people being removed; each shared node applies it and confirms in its `node_state` that the list it applied (`applied_doors_event`) is one the accounts node WROTE itself - a `superadmin_doors_set` in the accounts node's own log, written there (`origin_peer IS NULL`), with a version at least the one S2 published - and that it keeps no superadmin door of its own and no mirror of anybody being removed (`removed_mirrors`). "A version at least as high" alone is not enough (a list forged with a stolen seed of the accounts node can carry any version, but it never enters that node's own log); waiting for the exact list S2 published is too much (a newer genuine list - a password change, a second removal, a shared node joining - replaces it for good). The list goes out only from the accounts node, and only once that node has its new identity key for the removal: a list signed by the author's current key replaces one signed by a key it has retired since, whatever the versions, and the publisher's version is one past every version it has seen for itself. While `accounts_home` is another node, or none is set, the step waits with a plain sentence saying what to set - never "hand it over there" when that node belongs to a person being removed or such a person made it the accounts node | the master DEK; what they planted. Between S0 and S2 the person's mirror on a shared node is "being removed" (no sign-in, no join), but their mirror door still opens that node's database with their password |
   | S3 Review | accounts node | every superadmin, device, key, agent, invitation and account the person created or that has no owner on record: listed, pre-selected "remove" when planted or ownerless, each decided (keeping a pre-selected one needs a typed reason); removing another superadmin starts that person's removal. What the person did is read from the audit log (accounts created, promotions - a role that changed to superadmin -, devices joined with their door, agents), which is pruned after 90 days by default (`BMB_AUDIT_RETENTION_DAYS`): older actions are not seen, and every superadmin, device and key is listed anyway. The review follows the network while the process is open: it is built again when the devices, accounts, accounts node, unknown-owner slots and agents or invitations change (new items undecided, decisions kept; a new item re-opens a done review). Pre-selected besides: every device whose replicated row names the person as owner, every device that joined after S0 (a join through their door on a node that had not applied the lock-out, even one that claimed to be shared), every device enrolled through an invitation they made (in any invitation state). "Decide as pre-selected" (page and `--accept-review`) never decides a superadmin - each needs a human's decision (`--keep superadmin:<id>`); only the deciding administrator's own item is kept. The accounts node is listed with who made it that; it is never "removed" here | the master DEK |
   | S4 Rotate | every active full node | the master DEK: a quiet rotation with reason `offboarding:<process id>` that every node applies by itself; old snapshots and database copies taken afterwards open nothing new | v1 doors of people who stay reach the new key through the bridge (listed at S5) |
   | S5 Close | accounts node | the account is deleted, or demoted to the role and folders stored at S0; every device whose row names the person is revoked once more. It does not close while this node or an active peer reports `retired_key_unacknowledged_events`, `signed_as_this_node`, `shared_doors_error` or `accounts_home_changed`, or while the accounts node is a node this review removed or that left the network: each is said with what to do | - |

   Several removals share S1, S4 and S5: a rotation proposed after the last review of every open process serves them all;
   one proposed before a review does not (its doors were not yet decided). A node that a step waits for is either waited
   for ("open this node once" when it is locked) or revoked; a revoked node is never waited for.

7. **Confirmations come from `node_state`** (the control event, materialised in `tbl_node_state`): the identity key
   fingerprint (S1), the doors-list version (S2), the DEK fingerprint (S4), and - since this release - the number of v1
   doors and whether chain material is kept (`v1_doors`, `dek_bridge`; `DekBridgeNodeStateContributor`). The close lists
   every full node whose report still keeps a bridge.

8. **Tombstones.** `person_removed` (superadmin-only control event: person id, outcome, time - no names, no folders) is
   kept for ever in `tbl_removed_person`, which is in `SnapshotTables.Replicated` and `.Monotonic` so a network restore
   merges it and never drops it. A tombstone is enforced on apply, after any restore (`SnapshotService.AfterRestoreAsync`),
   at start (`TombstonesOpenStep`) and at the first sync after a join (`RemovedPersonEnforcer`): the account of that person
   is locked out first, then deleted (or demoted, where this node stored the demotion of that process). A person promoted
   again after a finished demotion gets a new person id, so the old tombstone never takes the new rights away.

9. **The process travels.** `offboarding_state` (superadmin-only control event) carries ids, the target kind, the step and
   the times, newest Lamport wins, only from the driving node. A shared full node shows the process and can take it over
   once the accounts node is revoked (a superadmin of that node, with their own password); S0 was done on the accounts
   node, and its tombstone applies to the accounts data when it is restored.

   **Who speaks for a process**. A receiver takes a state from the driver of record it holds; a state
   from any other node (a take-over, or a superadmin peer closing a removal it does not drive with a huge Lamport time) only
   once that driver is revoked on the receiver - the take-over precondition. A node never counts itself as revoked, so the
   node that drove a process refuses another node's state for it; it is revoked everywhere else after a take-over and
   cannot sync. A process a receiver does not know is created only by its accounts node of record, or by any superadmin
   node while none is designated there (otherwise deferred).

   **The take-over is single-writer**. It is refused while another node drives the process, and while the
   accounts node is another node still in the network (the trace of a take-over done elsewhere). When the accounts node was
   lost with the node where the removal started, the take-over makes this node the accounts node, so S2 can publish from
   here; an administrator who holds only a mirror of the lost node's list here gets it as an account of their own, with
   the password they just proved by opening their door (N3: only a node with such an account may become the accounts
   node). **Two take-overs at once** (two administrators on two shared nodes, before either saw the other) leave the nodes
   split between two accounts nodes, each ignoring the other's lists; both raise `accounts_home_changed`. To recover:
   revoke the stale one (the accounts node the network should not follow) on every node, then on the one that stays set
   the accounts node to itself again (Devices page); every node follows it, and the removal goes on there.

   A take-over is also refused from a node that is not the accounts node and still keeps superadmin doors of its own (it
   never applied a list: an account of the person not linked to them may still open it), and by an actor whose account
   this node's audit says the person created or promoted.

10. **The account-transition frame refuses to strand the device box.** Every demotion, deletion and lock-out deletes the
   person's doors in one transaction (`RunAccountTransitionAsync`). Since this release it refuses to delete a person's
   password door unless another active superadmin holds a sealed (v2) password door on this node: the device box is
   published again only from a sealed door, so with v1 stragglers alone the box that copied the leaver's door would stay
   active. A tombstone skips that one check: a door that stays opens the node with the leaver's password, which is worse.

11. **An event signed as this node that this node did not write** (it arrived by pull or push, carries a valid
   signature of any key this node ever had, and is dated after the oldest own event still held above the last compaction
   cut, or after the clock when none is held) can only come from somebody holding a copy of the seed. It is never this
   node's own: the identity re-key lists only events this node wrote (`origin_peer IS NULL`), and it raises
   `signed_as_this_node` (one marker per event, acknowledged on the identity page; the same event never raises it twice).
   A forgery dated before that floor cannot be told from an own event that compaction removed; after a restore from an
   older backup, events this node wrote after that backup are marked too.

## What each party can and cannot learn or do

- **A removed person:** On every up-to-date shared node, their replicated `tbl_user` row and `tbl_key_slot` (and boot-store door) are deleted on apply of the next `superadmin_doors_set`. They can no longer sign in, open the vault, or join a new device via `/api/join` with their old password, and combined with quiet DEK rotation on shared nodes, any old DEK they knew no longer opens newly rotated material.
- **A personal copy:** Never receives `superadmin_doors_set` envelopes, never alters its local superadmin/slots from `superadmin_doors_set`, and is not included in `accounts_home` selection; if a peer is changed from `personal` to `shared` (or joined with `--shared`) while `accounts_home` is set, it adopts the accounts node's list.
- **A blind copy:** Holds neither Master DEK nor private decrypting key for `superadmin_doors_set` (rejected as envelope recipients and refused as `accounts_home`), only relaying opaque ciphertext bytes; it learns nothing about superadmin usernames, hints, salts, or door public keys.

## Consequences

- Adding, renaming, changing password of, or removing a superadmin on the accounts node converges automatically across all unlocked shared server nodes without restarting them or sharing passwords.
- Recovery keys minted on a shared node still verify locally against `recovery` slots, while changing a replicated superadmin's password must be performed on the accounts node.
- Removing a superadmin takes minutes to days, not one click, and needs the acting administrator's password twice at most
  (start, and the rotation if the process was interrupted by a restart: the password is held in memory for 30 minutes,
  never stored).
- A one-person bank never sees any of it.
- The offline re-key refuses while a removal is open (plan A6).
- The CLI has `bmb user remove-superadmin <name> --demote-to <role>|--delete --as <admin>` and
  `bmb offboarding status|continue`.

## Open

- A node whose identity seed is kept outside its database (ADR 0013: phones, blind nodes, `FileNodeKey`) cannot re-key
  in place; if one is a shared full node, S1 waits for it to be paired again as a new node, or revoked.
- A blind copy is never waited for by S2 or S4. In S1 the accounts node re-keys at once; a blind copy (or any device) that
  has not synced since it was paired is an S1 row "cannot follow" until its first sync shows the capability. "Follows" is
  read as "advertises `identity-rekey-v1`" - whether it already applied the rotation is not observable from here.
- The opener of a join that asks to be `shared` is only in the serving node's audit; the review catches such a join by
  "joined after S0" instead. A device re-joined under an id it had before keeps its old `created_at` and is caught only by
  its owner row or the audit.
- An account the person created on ANOTHER node is not knowable to the take-over check (the doors list carries no creator).
- The confirmation of S1 is the key this node trusts for each node. A rotation that is not for this process but happened
  after S1 began also confirms a node: it is a new seed made after the lock-out, which is what the step is for.
