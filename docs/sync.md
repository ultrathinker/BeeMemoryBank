# Multi-Node Synchronization

## Overview

Synchronization allows you to have a copy of the knowledge base on multiple devices (VPS, laptop, desktop). Each device is a full-fledged node with a complete data replica. Changes on any node automatically appear on the others.

**Why not just git / Dropbox?** Articles are encrypted, the event log is signed with Ed25519, and conflicts are resolved automatically. Standard file synchronization tools cannot handle atomic operations and conflict resolution.

**Model:** Event Sourcing. Every operation (creating/editing/deleting an article, adding a node, commenting) is recorded as an event in a linear log. Nodes exchange events over HTTP.

## Trust Model

Worth reading before you invite a second node. The security-policy version of the same facts is in
[SECURITY.md](../SECURITY.md#trust-model).

**A node joins by presenting the master password.** `POST /api/join` tries it against every
password-bearing key slot on the bootstrap node and, when one opens that belongs to an active
superadmin (or to a legacy pre-user-table `password` slot), hands back the wrapped Master DEK and a
copy of the whitelist. There is no invite token and no approval step — the password is the whole
gate.

**A joined node is written to the whitelist content-only.** `JoinEndpoints` creates the entry with
`is_superadmin = 0` — mesh *membership* (proven by the master password) and cluster-state
*authority* are separate grants. `EventApplier` checks the flag before applying `whitelist_add`,
`whitelist_revoke`, `whitelist_update`, `hard_delete`, `restore_network` and
`master_password_changed`; an event of one of those types signed by a peer without the flag is
rejected with `UnauthorizedAccessException`. Article/folder/comment events are never gated on it —
a content-only peer syncs exactly like any other.

**So a joined replica starts as a passive copy for cluster state, and an active one only for
content.** Only a node whose `is_superadmin` bit is set on the *receiving* node's own whitelist row
can:

- **Revoke another peer** — `ApplyWhitelistRevokeAsync` flips the target's row to `Status = "R"` on
  every node that receives and accepts the event. A node ignores a revoke aimed at itself, so the
  revoked peer keeps its own data; it just stops being accepted anywhere else.
- **Hard-delete an article or folder everywhere** — `hard_delete` routes to
  `HardDeleteService.ApplyRemoteAsync`, which physically purges the rows and deletes the `.enc`
  media files. It is not a tombstone and there is nothing to restore from afterwards.
- **Restore the whole network from its own snapshot** — see "Network-Wide Snapshot Restore" below.
  Peers with `auto_accept_restore` apply it unattended.

Before this changed, `JoinEndpoints` wrote every new peer in with `is_superadmin = 1` ("trust on
join"), so completing the join handshake was enough on its own to gain all three powers above —
without one write to a database you own. Content-only is now the default; authority is granted the
same deliberate way it can be taken away: `PUT /api/whitelist/{nodeId}/superadmin` sets or clears
`is_superadmin`, and the new value rides in a `whitelist_update` event. Because each node decides
from *its own* whitelist row, a promotion or a demotion only binds the nodes the event actually
reaches — and a peer running a build from before this field existed deserialises the payload
without it and leaves the row exactly as it was (still content-only for a new join on that build,
still whatever it already was for an older one). The affected node is also not informed of a
change made about it elsewhere (it holds no whitelist row for itself), so a node that thinks it
became a superadmin, or that lost the role, may go on emitting cluster-state events that every
other node quietly rejects. See "Initial Join: Snapshot-Based Bootstrap" below for the one place a
node's authority *is* inferred rather than announced: the immediate node a joiner dials into.

**One further consequence for the event log itself:** payloads are plaintext JSON. A peer that only
ever authenticates and pulls still learns every article title, tree path and tag name in the vault,
even though bodies stay encrypted — see [encryption.md](encryption.md#what-is-not-encrypted-by-design).

## How It Works in Practice

```
User creates an article on VPS (node-a.example.com)
    │
    ▼ ArticleService.CreateAsync() → EventLogger.LogArticleCreateAsync()
    │
    ▼ Event written to tbl_event (sequence=1042, lamport_ts=2089, actor_type="web")
    │
    ▼ EventLogger calls SyncTrigger.Signal() → immediate sync triggered
    │
    ▼ SyncScheduler wakes up (was awaiting signal or 60s timeout)
    │
Node B (node-b.example.com) receives push within seconds
    │
    ▼ SyncClient: GET /api/sync/events?afterSequence=1041 → receives event
    │
    ▼ EventApplier.ApplyArticleCreateAsync() → article appears in Node B's database
    │
    ▼ SyncPosition updated: VPS → last_sequence=1042
    │
    ▼ SyncPushPosition updated: NodeB → last_pushed_seq=1042
```

## Synchronization Protocol (4 Steps)

### Step 1: Authentication (Ed25519 challenge-response)

```
NodeB → NodeA: POST /api/sync/challenge
NodeA → NodeB: { challenge: "random_32_bytes_base64", serverNodeId: "..." }

NodeB: signature = Ed25519.Sign(privateKey, challenge_bytes)  // signs a V2 payload bound to serverNodeId

NodeB → NodeA: POST /api/sync/authenticate { nodeId, challenge, signature }
NodeA: verify signature vs whitelist[nodeId].publicKey
NodeA → NodeB: { token: "bearer_token_base64" }  (TTL 1 hour)
```

**Why challenge-response instead of a simple API key?** The private key is never transmitted over the network. Even if someone intercepts the challenge and signature, they are single-use (TTL 60 seconds).

**The challenge is bound to who it was fetched from (2026-09-04, breaking).** A signed response used
to be redeemable against whichever node the audience field named — and that field came from the
*responding* peer's own self-reported node identity, not from anything the caller pinned in advance.
A hostile node could declare itself to be node C, relay a challenge fetched live from the real C, and
present the resulting signature there as if it came from the victim. The audience is now taken from
`tbl_whitelist` (pinned out-of-band when the peer was added) instead of the peer's own claim, and the
client signs and the server verifies only the audience-bound (`V2`) challenge format — the older,
unbound format is gone from both sides. **Every node in the mesh, including the Android app, must run
this build or newer to keep syncing with the rest of the network.**

### Step 2: Pull (receiving events)

```
NodeB → NodeA: GET /api/sync/events?afterSequence=N
               Authorization: Bearer <token>

NodeA → NodeB: [
  { sequenceNum: N+1, eventId: "guid", nodeId: "node-a-id", lamportTs: 2089,
    eventType: "article_create", payload: "{...}", signature: "base64",
    actorType: "web", actorName: null, viaAgentId: null },
  ...
]  (up to 1000 events per request)
```

### Step 3: Applying Events

For each received event:
1. `Ed25519.Verify(publicKey, BuildPayload(event), signature)` — verify signature
2. Check: has `eventId` already been applied? → skip (idempotency)
3. `LamportClock.Update(event.lamportTs)` — update logical clock
4. `EventApplier` applies by type: article_create → INSERT, article_update → conflict check + UPDATE, ...
5. Event is saved to `tbl_event` (for further propagation to other nodes)

### Step 4: Push (sending local events)

```
NodeB → NodeA: POST /api/sync/events
               Authorization: Bearer <token>
               Body: [ { eventId, nodeId, lamportTs, eventType, payload, signature }, ... ]
               (batch up to 500)
```

**Node ID filter:** Push only sends events originating from the local node (`WHERE node_id = localNodeId`). Events received from remote nodes are not echoed back — the remote already has them. This reduces wasted traffic.

**Push position tracking:** After a successful push, `tbl_sync_push_position` is updated with the last pushed sequence number. This enables the delivery status UI to show per-node sync progress.

## Push-on-Save (SyncTrigger)

By default, SyncScheduler polls every 60 seconds. With push-on-save, saves trigger immediate synchronization:

```
EventLogger.AppendEventAsync()
    │
    ▼ SyncTrigger.Signal()  — sets flag + releases SemaphoreSlim
    │
    ▼ SyncScheduler.WaitAsync() returns immediately (was blocked on semaphore)
    │
    ▼ SyncAllAsync() runs — syncs with all public nodes
```

**Debounce:** 100 events per second → 100 Signal() calls → but `Interlocked.Exchange` ensures only one `SemaphoreSlim.Release()`. One sync cycle processes all accumulated events.

**Concurrent guard:** `SemaphoreSlim(1,1)` in SyncScheduler prevents timer-triggered and signal-triggered syncs from running simultaneously.

**Fallback:** If no signal arrives, SyncScheduler wakes up after the 60-second timeout and syncs normally.

## Event Relay (Gossip)

When a node pushes events to a sync partner, it now pushes **all known events**, not only those originating from itself. This ensures faster propagation across multi-node topologies where not every node has a direct connection to every other node.

Implementation: `SyncClient` uses `GetAllEventSequenceNumbers()` instead of filtering by local `node_id`. Events that originated from a third node are relayed onward, effectively implementing a gossip protocol. This eliminates the need for full mesh connectivity — a chain of nodes will eventually converge.

## Invisible Mode

A node can enable Invisible Mode to hide itself from sync partners. When invisible, the node still pulls updates from its partners but does not appear in their sync status or delivery reports.

Controlled by `InvisibleModeService` (`libs/BeeMemoryBank.Core/Services/InvisibleModeService.cs`). Use cases include:
- **Maintenance:** operate without triggering sync alerts on partner nodes
- **Private operation:** read updates without revealing presence
- **Temporary disconnection:** step away from the sync mesh without alerting partners

The invisible node behaves normally from its own perspective — it receives events, applies them, and can push its own events when it chooses to become visible again.

## Lightweight Ping Endpoint

```
GET /api/sync/ping?afterSequence=<long>

204 No Content           — no new events (fast path, ~200 bytes roundtrip)
200 { "count": 3 }       — 3 new events exist → client should run full sync
```

No authentication required (does not expose content, only presence). Used by mobile clients to avoid full sync overhead on every poll.

## Delivery Status Endpoint

```
GET /api/sync/delivery-status
Authorization: via InternalKeyValidator (BMB_INTERNAL_KEY)

{
  "localNodeId": "guid",
  "nodes": [
    {
      "nodeId": "guid",
      "displayName": "VPS-1",
      "nodeType": "public",          // public (has apiAddress) | private (no apiAddress)
      "lastPushedSeq": 12345,
      "totalLocalEvents": 12348,
      "isSynced": false,             // lastPushedSeq >= totalLocalEvents
      "lastContactAt": "2026-04-09T12:30:00Z"
    }
  ]
}
```

Protected by `InternalKeyValidator` — only accessible from the Web proxy (same server). Used by the sync status UI widget and post-save toast.

## Node-Local Entities (Not Synced)

Agents, users, and folder ACL entries are node-local — they are created on the node where they belong and are **not** propagated through the sync event stream. A user created on the primary node does not exist on a replica, and vice versa. See `docs/architecture.md → Node Topology` for rationale.

## Event Types

| Type | Payload | Synced? |
|---|---|---|
| `article_create` | title, treePath, tags (deprecated, empty), conceptTags, ciphertext, encrypted_dek, iv, dek_iv, timestamps | Yes |
| `article_update` | same (full replacement, not diff) | Yes |
| `article_delete` | deleted_at | Yes |
| `whitelist_add` | nodeId, displayName, publicKeyB64, apiAddress, canGenerateEmbeddings, **isSuperadmin** | Yes |
| `whitelist_update` | nodeId, displayName, apiAddress, canGenerateEmbeddings | Yes |
| `whitelist_revoke` | nodeId | Yes |
| `comment_create` | commentId, articleId, text, createdAt | Yes |
| `comment_delete` | commentId | Yes |
| `folder_create` | folderId, path, name, parentPath | Yes |
| `folder_rename` | folderId, oldPath, newPath | Yes |
| `folder_delete` | folderId, path | Yes |
| `media_create` | mediaId, articleId, ciphertextB64, encrypted_dek, iv, dek_iv | Yes |
| `media_delete` | mediaId | Yes |
| `media_link` | mediaId, articleId | Yes |
| `concept_tag_rename` | oldName, newName | Yes |
| `concept_tag_merge` | source, target | Yes |
| `concept_tag_delete` | name | Yes |
| `hard_delete` | entityType ("article"/"folder"), entityIdentifier (articleId or folder path), occurredAt | Yes |
| `snapshot_checkpoint` | cp_seq, events_removed, snapshot_file_name, snapshot_sha256, prev_checkpoint_sha256, produced_at | Yes |
| `restore_network` | snapshot_file_name, sha256, originator_node_id, base_cp_seq, restored_at | Yes |
| `dek_rotation_proposed` | encrypted_new_dek, iv, new_dek_epoch, rotation_ts, expires_at, originator_node_id | Yes |
| `dek_rotation_commit` | proposed_event_id, encrypted_new_dek, iv, new_dek_epoch, rotation_ts, originator_node_id | Yes |

**Important:** `article_update` sends the full ciphertext (not a diff). This is simpler and safer — diffs on encrypted data are meaningless.

**Media Linking:** When an article is saved (created or updated), the system automatically scans its content for image references. For each newly found image that was previously "orphaned" (uploaded but not linked), a `media_link` event is generated. This ensures that images are properly associated with articles for ACL enforcement and sync consistency across the network.

**Hard Delete:** Unlike `article_delete` (soft delete, recoverable from trash), `hard_delete` permanently purges an article or folder and its media from the local database and disk. On receiving a `hard_delete` event, subscribers purge the corresponding rows immediately and suppress any later `article_update` events for the same entity (via the `tbl_event(event_type, entity_id)` gate index). Only a Superadmin can initiate it through the Admin UI.

## Event Actor Tracking

Every event contains `actor_type` and `actor_name`:
- `actor_type = "web"` — initiated via Web UI
- `actor_type = "agent"`, `actor_name = "agent_name"` — MCP agent
- `actor_type = "cli"` — via CLI

Implementation: `IActorProvider` with two implementations — `HttpActorProvider` (API, from HTTP context) and `CliActorProvider` (CLI).

## Lamport Clock — Logical Time

```csharp
// On local event:
public long Tick() => Interlocked.Increment(ref _counter);

// On receiving a remote event:
public long Update(long remoteTs)
{
    while (true)
    {
        var current = Interlocked.Read(ref _counter);
        var next = Math.Max(current, remoteTs) + 1;
        if (Interlocked.CompareExchange(ref _counter, next, current) == current)
            return next;
    }
}
```

**Why:** Physical time is unreliable (NTP drift, timezone issues, clock adjustments). A Lamport clock guarantees causal ordering: if A caused B, then `lamport(A) < lamport(B)`.

**Why not HLC (Hybrid Logical Clock)?** HLC adds physical time for human readability. For a knowledge base this is unnecessary — `createdAt`/`updatedAt` are stored separately.

**Saturating advance + jump cap.** `Update(remoteTs)` clamps the forward jump to `MaxJump = 10_000_000` ticks per call (mitigates a malicious or buggy peer pushing a forged sky-high lamport_ts that would lock the local clock at `long.MaxValue` and break every future event). Adds are saturating: when `current + MaxJump` would overflow `long.MaxValue`, the clock holds at `long.MaxValue` rather than wrapping to negative.

## Conflict Resolution (ConflictResolver.cs)

Scenario: the same article is modified on two nodes before synchronization.

```csharp
// Last Writer Wins by Lamport timestamp
if (incoming.LamportTs > existing.LamportTs) → incoming wins
if (incoming.LamportTs < existing.LamportTs) → existing wins

// Tiebreaker: on equal timestamps — the larger GUID nodeId wins
if (incoming.LamportTs == existing.LamportTs)
    → string.Compare(incoming.NodeId, existing.NodeId) > 0 ? incoming : existing
```

**Determinism:** Any node, given the same two events, will choose the same winner. This guarantees eventual consistency without coordination.

**The losing version** is saved to `tbl_conflict_version` (TTL 7 days) — it can be restored manually.

## Sentinel Value — DEK Compatibility Check

Before synchronization, nodes verify Master DEK compatibility:
```sql
-- tbl_node_identity.sentinel_value
-- AES-256-GCM("BeeMemoryBank", masterDEK) → BLOB
```

Node A receives the sentinel from Node B during join. At every sync session, it checks: `MasterKeyManager.VerifySentinel(sentinel_B, local_master_dek)`? If not — DEKs are incompatible. Note: `ComputeSentinel` uses a fresh random IV each call, so direct byte comparison never works — always use `VerifySentinel`.

## Event Integrity (EventSignature.cs)

```csharp
// Deterministic serialization for signing:
public static byte[] BuildPayload(SyncEvent e)
{
    // Length-prefixed concatenation:
    // eventId + nodeId + lamportTs + eventType + articleId + payload + protocolVersion + createdAt
}
```

**Why signatures?** Tamper protection: a compromised node cannot forge events from another node. Each event is signed with the author node's private key.

## EventApplier — Result Enum and Defensive Gates

`EventApplier.ApplyAsync` returns an `EventApplyResult`:

- `Applied` — event was processed and persisted (or was a duplicate already in the log).
- `SilentlyDropped` — event was deliberately ignored (replay-shield, hard-delete tombstone, malformed payload). The pull/push loops still advance their cursor past it so the same event is not re-fetched forever.
- `Skipped` — event raised an exception that callers should surface (signature mismatch, unknown protocol version, etc.).

Three defensive gates run before per-type handlers:

1. **Hard-delete gate.** If `tbl_hard_delete_audit` records that the entity (article id or folder path) was hard-deleted at this or a later lamport_ts, the event is `SilentlyDropped`. Survives event-log compaction (the audit table is independent).
2. **Replay shield.** Per peer, if the event's `lamport_ts` is below the shield threshold last set by a `restore_network` event from the same peer, the event is `SilentlyDropped` (zombie state from a pre-restore timeline).
3. **Authorisation gate.** `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `hard_delete`, and `restore_network` may only be applied if the originating peer's local `tbl_whitelist.is_superadmin` is true. A non-superadmin peer trying to push these gets `UnauthorizedAccessException` raised on the receiver — caller surfaces it.
4. **Tree-path payload validation** (`TreePathCanonicalizer.IsIllegal`). For events carrying user-controlled paths (article create/update, folder create/rename), payloads with `..`, `.`, control chars, or NUL are `SilentlyDropped`. Cosmetic non-canonical input (`//`, trailing `/`) is allowed through so we don't permanently diverge from peers running pre-canonicalisation code.

## TreePath Canonicalisation

`libs/BeeMemoryBank.Core/Services/TreePathCanonicalizer.cs` is the single source of truth for folder-path form:

- `Canonicalize(string?)` — returns `/seg1/seg2/…`; empty/null/`/` → `/`; collapses `//`; throws `ArgumentException` on `..` / `.` / control chars / NUL.
- `TryCanonicalize(string?)` — same but returns `null` instead of throwing (used in sync apply).
- `IsIllegal(string?)` — strict-only check, ignores cosmetic differences (used in EventApplier validation gate).

Canonicalisation runs at every write entry point: `FolderService.CreateAsync/MoveAsync/RenameAsync`, `ArticleService.CreateAsync/UpdateAsync`. This prevents a User scoped to `/Public` from poisoning the namespace with literal-string folders like `/Public/../Admin/X` — the path is rejected at write time, not interpreted at read time.

## Tombstone Conflict Resolution (LWW)

Tombstones use a Last-Writer-Wins gate on `(lamport_ts, source_node_id)` — not naive `INSERT OR REPLACE`:

```sql
INSERT INTO tbl_tombstone (...) VALUES (...)
ON CONFLICT(article_id) DO UPDATE SET ...
WHERE excluded.lamport_ts > tbl_tombstone.lamport_ts
   OR (excluded.lamport_ts = tbl_tombstone.lamport_ts
       AND excluded.source_node_id IS NOT NULL
       AND tbl_tombstone.source_node_id IS NOT NULL
       AND excluded.source_node_id > tbl_tombstone.source_node_id);
```

Without this, an out-of-order `delete` event from one peer could overwrite a strictly-newer tombstone from another peer, allowing the article to "resurrect" the next time the network pulls.

## Tombstones and Cleanup

| Entity | TTL | Purpose |
|---|---|---|
| Tombstone (soft delete) | 60 days | Prevents resurrection: an offline node syncs `article_create`, but the tombstone says "already deleted" |
| Conflict version | 7 days | Allows manual recovery of the losing version |
| Ciphertext of deleted articles | 30 days | Article DEK is destroyed → ciphertext is undecryptable, but still takes space |

`CleanupService` — a background service that periodically purges expired records.

## Key Files (BeeMemoryBank.Sync/)

| File | LOC | Purpose |
|---|---|---|
| `SyncScheduler.cs` | ~95 | Background service: awaits SyncTrigger signal or 60s timeout → syncs all public nodes. SemaphoreSlim(1,1) concurrent guard |
| `SyncTrigger.cs` | ~30 | ISyncTrigger implementation: SemaphoreSlim + Interlocked debounce for async-safe push-on-save signaling |
| `SyncClient.cs` | ~180 | HTTP client: challenge → auth → pull events → apply → push local events (node_id filtered) → update push position |
| `EventLogger.cs` | ~130 | Log*Async methods: creates SyncEvent, signs, saves, passes actor info, signals SyncTrigger |
| `EventApplier.cs` | ~350 | Apply*Async methods: signature validation → conflict check → INSERT/UPDATE; handlers for DEK rotation events, restore, hard-delete |
| `EventPayloads.cs` | ~200 | Record types for JSON payload of each event type (incl. DekRotationProposed/Commit, RestoreNetwork, HardDelete) |
| `EventSignature.cs` | ~50 | BuildPayload: length-prefixed deterministic serialization |
| `ConflictResolver.cs` | ~40 | ShouldApply: Lamport ts comparison + nodeId tiebreaker |
| `LamportClock.cs` | ~30 | Thread-safe: Tick(), Update(), restore from database |
| `CleanupService.cs` | ~40 | Background: purge expired tombstones + conflict versions |
| `PendingEmbeddingProcessor.cs` | ~60 | Background: generate embeddings for articles with embedding_pending=1 |

## Initial Join: Snapshot-Based Bootstrap

When a fresh node joins an existing network, it does NOT replay the full event log of the
producer. Instead, the producer sends a signed snapshot of the current state, and the joiner
tails events from that point onward.

**Flow (joiner perspective):**

1. `POST /api/init/join` on the local Web/API: key exchange with remote, receive master DEK,
   create local `tbl_node_identity`, `tbl_key_slot`, and populate `tbl_whitelist`.
2. Challenge+sig authenticate with the remote (standard `/api/sync/authenticate`).
3. `GET /api/sync/snapshot/for-join` (Bearer auth) → signed `.tar.gz`. Headers:
   - `X-BMB-Snapshot-CP-Seq`: sequence number the snapshot represents
   - `X-BMB-Snapshot-Lamport`: clock value at CP
   - `X-BMB-Snapshot-Producer`: producer's node ID
   - `X-BMB-Snapshot-Signature`: Ed25519 signature (base64)
4. Verify the signature against the producer's public key (from whitelist).

   **Step 1's whitelist rows are where the two trust decisions in this handshake are made, and
   they are not the same decision.** The producer decides what it thinks of *this* joiner — always
   content-only, `is_superadmin = 0`, per the trust model above. The joiner decides what it thinks
   of the *producer* — trusted on first use, `is_superadmin = 1` — because the producer's own
   authority never travels in this response at all: a node is never in its own whitelist (see
   `EventApplier.ApplyWhitelistAddAsync`'s self-skip), so there is nothing for the joiner to read
   the producer's real status from even if it wanted to defer the decision. The operator already
   made this call by typing the producer's URL and the master password that secures the whole
   vault; that is the trust anchor a fresh mesh bootstraps from, and it is why a single-node vault's
   founding node can administer a newly joined second device without an extra promotion step. It
   does not extend any further: if the joiner later relays a THIRD node's join, that third node
   still starts content-only exactly as this one did, and the joiner's own belief about the
   producer has no bearing on what any other real node in the mesh believes about the joiner.
5. Import selected data tables only: `tbl_folder`, `tbl_article`, `tbl_article_body`,
   `tbl_concept_tag`, `tbl_article_concept_tag`, `tbl_concept_tag_edge`, `tbl_media`,
   `tbl_tombstone`, `tbl_conflict_version`, `tbl_projection_matrix`.
   Local `tbl_node_identity` / `tbl_key_slot` / `tbl_whitelist` / `tbl_migration` are
   preserved (they were populated in step 1).
6. Set `sync_position[producer] = cp_seq` so subsequent pulls only fetch tail events.
7. Initialize Lamport clock: `min(snapshot.lamport, local + MAX_CLOCK_ADVANCE)` where
   `MAX_CLOCK_ADVANCE = 1_000_000` (clock-skew attack mitigation).
8. `MarkInitialSyncCompletedAsync` on `tbl_node_identity`.

**Re-join:** if a node's position falls below the producer's last compaction CP, regular
sync returns 410 Gone. The node has no data to recover from — admin must wipe and rejoin.
See `docs/compaction.md`.

**Secret filtering:** the snapshot sent to a joiner is built with `filterSecrets=true`, which
drops `tbl_node_identity`, `tbl_session`, `tbl_agent*`, `tbl_sync_*`, `tbl_compaction_log`,
`tbl_event`, `tbl_key_slot`, `tbl_user`, `tbl_folder_acl_entry`, `tbl_audit_log`,
`tbl_hard_delete_audit`. `tbl_projection_matrix` is intentionally kept (shared embedding
projection across all peers with the same master DEK).

## Network-Wide Snapshot Restore

When a superadmin restores from a snapshot in network-mode, the snapshot is broadcast to every whitelisted peer through a `restore_network` sync event. Peers either auto-apply or queue for manual review — the same peer-acceptance pattern later reused for DEK rotation.

**Initiator flow (`POST /api/snapshots/restore` with `mode=network`):**

1. Verify master password, validate the local snapshot file (size + sha256).
2. Apply the snapshot to the local DB (drops the same set of tables documented under "Initial Join", restores from the `.tar.gz`).
3. Emit a `restore_network` event signed with the initiator's Ed25519 key. Payload includes `snapshot_file_name`, `sha256`, `originator_node_id`, `base_cp_seq`, `restored_at`.
4. The snapshot file itself stays on the initiator at `data/snapshots/{file}` and is exposed at `/api/snapshots/restore/{eventId}/file` for peers to pull. Distributed-seeding endpoints (`/api/sync/challenge`, `/api/sync/authenticate`, the snapshot file download) are explicitly allowlisted in `MaintenanceMiddleware` so peers can authenticate and download even while the initiator is in maintenance mode.

**Peer flow (`EventApplier.ApplyRestoreNetworkAsync`):**

1. On receiving the `restore_network` event, look up the originator's `tbl_whitelist.auto_accept_restore` flag.
2. **Auto-accept = true** → enter maintenance mode, fetch the snapshot file from the originator, verify sha256 + Ed25519 signature, apply locally. Set `tbl_restore_event_state.state = Applied`. Set a replay-shield in `tbl_restore_replay_shield` so any pre-restore events from the originator that arrive late are silently dropped (they would otherwise be zombie state from the pre-restore world).
3. **Auto-accept = false** → write `tbl_restore_event_state.state = Pending` and surface a banner in the peer's Admin UI with `Apply` / `Reject` buttons. The admin makes the call.
4. **Reject** → `state = Cancelled`. The peer disconnects from the network for the originator's events going forward (incoming events from that peer would belong to a divergent timeline). To rejoin, the admin must wipe and re-join.

**Crash recovery:** the startup sweep in `Program.cs` flips any `tbl_restore_event_state.state` row stuck in `Downloading` or `Applying` to `Failed` so the admin sees a clear "needs decision" indicator instead of a phantom in-progress restore. Network-wide restore also writes media files into `data/media/` BEFORE the SQL commit; orphan `*.enc` files from a crashed run get reconciled at startup.

## DEK Rotation Events

DEK rotation propagates through the sync event stream as two event types:

### `dek_rotation_proposed`

The initiator node broadcasts this after the superadmin triggers rotation. Peers record it in `tbl_dek_rotation_state` as `Proposed` and wait — they do **not** start rotating yet.

**Payload (since ADR-0006, 2026-09-04):**

| Field | Type | Purpose |
|---|---|---|
| `peer_envelopes` | object | One X25519-sealed envelope per currently-trusted peer, keyed by recipient node id — each peer opens only its own entry (see `docs/adr/0006-confidential-dek-rotation-x25519-envelopes.md`) |
| `encrypted_new_dek` | string (base64), **nullable** | Legacy fallback: `AES-256-GCM(newDek, oldDek)`. Present only when a peer on this mesh still needs the pre-ADR-0006 path; omitted otherwise |
| `iv` | string (base64), **nullable** | 12-byte GCM nonce for the legacy `encrypted_new_dek` field, when present |
| `new_dek_epoch` | int | Monotonic counter; `current_epoch + 1` |
| `rotation_ts` | string (ISO 8601) | When the rotation was initiated |
| `expires_at` | string (ISO 8601) | 24-hour expiry window |
| `originator_node_id` | string | Node ID of the initiator |

A peer whose whitelisted Ed25519 key turns out to be malformed/off-curve is excluded from
`peer_envelopes` for this rotation (logged loudly) rather than aborting the rotation for the rest of
the mesh; the initiator itself is never excluded by this filter.

### `dek_rotation_commit`

Emitted immediately after `dek_rotation_proposed` (MVP: no quorum wait). Peers use the `proposed_event_id` reference to match the COMMIT with a locally stored PROPOSED.

**Payload:** same shape as `dek_rotation_proposed` above (`peer_envelopes` + nullable legacy
`encrypted_new_dek`/`iv`), plus:

| Field | Type | Purpose |
|---|---|---|
| `proposed_event_id` | string | Links back to the PROPOSED event |
| `new_dek_epoch` | int | Matches the PROPOSED epoch |
| `rotation_ts` | string (ISO 8601) | Matches the PROPOSED timestamp |
| `originator_node_id` | string | Node ID of the initiator |

**Resolving the new DEK (`DekRotationMaterial.ResolveNewDek`):** a receiving node first tries to
open its own entry in `peer_envelopes`; only if that's absent (a pre-ADR-0006 sender) does it fall
back to unwrapping the legacy `encrypted_new_dek` with the current Master DEK. See
[SECURITY.md](../SECURITY.md#online-dek-rotation) for what the envelope scheme does and does not
protect against.

### Peer-Acceptance Model

DEK rotation uses the same peer-acceptance pattern as snapshot restore (see "Restore Network" in EventApplier).

**Auto-accept toggle:** `tbl_whitelist.auto_accept_dek_rotation` — a per-peer boolean flag (default: false). Set via the Admin UI or `POST /api/whitelist/{nodeId}/auto-accept-dek-rotation`.

- **Auto-accept enabled:** when the COMMIT event arrives, `EventApplier` fires `IDekRotationApplier.AutoAcceptCommitAsync()` in the background. The peer applies the full destructive re-wrap (all DEK columns, agent deletion, sentinel update) without human intervention. If the session is locked at the time, auto-accept is deferred and retried on the next unlock (`RetryPendingAutoAcceptsAsync`).
- **Auto-accept disabled:** the COMMIT is recorded in `tbl_dek_rotation_state` as `Committing`. The Admin UI shows a pending banner with Apply (`POST /api/dek-rotation/peer-accept/{id}`) and Reject (`POST /api/dek-rotation/peer-reject/{id}`) buttons. A pending list is available via `GET /api/dek-rotation/peer-pending`.

**Reject:** marks the rotation as `Rejected`. The peer's DEK diverges from the network — it can no longer decrypt new events. The peer must eventually accept a rotation or be re-joined.

### Security: PROPOSED Must Precede COMMIT

On receiving a `dek_rotation_commit`, `EventApplier` validates that the matching `proposed_event_id` exists locally in `tbl_dek_rotation_state`. If not found, the event **throws** (is not recorded in `tbl_event`), causing the sync pull to fail and retry. This prevents a malicious peer from forging a COMMIT for a rotation that was never proposed.

If the PROPOSED event simply hasn't arrived yet (ordering fluke), the next sync pull will deliver it first, and the COMMIT succeeds on retry.

### Sentinel Mismatch During Sync

After a peer rotates its DEK but before this node has accepted, the sentinel values diverge. The sync system:

- **Logs a warning** when sentinel mismatch is detected.
- **Does NOT block** event pull. Blocking would create a deadlock: the node needs the COMMIT event to fix the mismatch, but can't receive it because the mismatch blocks pull.

### Lazy Slot Rewrap After Peer Auto-Accept

When a peer auto-accepts a rotation, the destructive part on the PEER side keeps all user key slots intact (still wrapped with the old DEK) and only deletes `tbl_agent` rows and `recovery`-type slots. The initiator-side flow is different — there, all OTHER user slots are dropped because the initiator's local users are the canonical set. On the peer, a user's slot stays around so that on the next login, lazy rewrap can migrate it transparently:

1. The system detects the sentinel doesn't match the DEK derived from the user's password.
2. `LazySlotRewrapService` walks `tbl_dek_rotation_state.Applied` rows chronologically.
3. For each rotation, it unwraps the next DEK from the commit event payload using the current candidate.
4. After each unwrap, `VerifySentinel(currentSentinel, candidateDek)` is called. On match, the walk stops.
5. The user's key slot is re-wrapped with the current DEK.

This is transparent to the user — no manual action required. See `docs/encryption.md → Lazy Slot Rewrap`.

**Resilience (2026-09-04/05):** a single row that a faster peer had already re-wrapped, or a
version/legacy-framed row needing different handling, used to abort the *entire* rotation
transaction and leave the node stuck retrying the same failure forever. Per-row handling now tries
the old key, then the new key, and only marks a row genuinely unreadable as a last resort, reporting
counts (rewrapped / already-on-new-key / unreadable) rather than a bare pass/fail. The chain
material `LazySlotRewrapService` needs is now persisted locally (migration 020) instead of being
readable only from the `dek_rotation_commit` event — which event-log compaction routinely deletes,
previously locking out any user who hadn't logged in since the rotation.

## Recent hardening (2026-09)

- **`tbl_whitelist` is version-stamped** (`lamport_ts`/`source_node_id`, migration 021) like every
  other replicated table, closing a hole where a node offline during a peer revocation could put
  that peer back into the mesh with its own stale `whitelist_add` on reconnect. Rows revoked before
  this migration get a narrower, permanent carve-out against a remote (not local) re-add.
- **`EntityId` is derived from already-signed fields, not trusted as a transported one.** A node
  that only relays someone else's event could otherwise rewrite `EntityId` without invalidating the
  signature — and the hard-delete gate keys on that exact field.
- **Sync quarantine now distinguishes `Permanent` failures (bad signature, malformed payload) from
  `Deferred` ones** (a precondition — the sender's whitelist entry, a blob, a rotation COMMIT — just
  hasn't arrived yet), retried on a separate, longer wall-clock budget instead of being discarded on
  the same 5-attempt counter as an actual forgery. Quarantine state is persisted (`tbl_sync_quarantine`)
  and survives a restart; `GET /api/sync/quarantine` / `DELETE /api/sync/quarantine/{eventId}` expose
  and clear it. A revoked event originator is always classified `Permanent`, never `Deferred` —
  otherwise re-adding a revoked peer inside the retry window could replay its whole pre-revocation
  backlog.
- **The replicated-table set for snapshots/joins is an explicit allow-list, not a deny-list of
  secrets** — a new table is stripped by default until deliberately added to the allow-list, closing
  a class of bug where a forgotten table (e.g. `tbl_remote_api_token`) shipped its contents to every
  joining peer.
