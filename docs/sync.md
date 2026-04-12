# Multi-Node Synchronization

## Overview

Synchronization allows you to have a copy of the knowledge base on multiple devices (VPS, laptop, desktop). Each device is a full-fledged node with a complete data replica. Changes on any node automatically appear on the others.

**Why not just git / Dropbox?** Articles are encrypted, the event log is signed with Ed25519, and conflicts are resolved automatically. Standard file synchronization tools cannot handle atomic operations and conflict resolution.

**Model:** Event Sourcing. Every operation (creating/editing/deleting an article, adding a node, commenting) is recorded as an event in a linear log. Nodes exchange events over HTTP.

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

NodeB: signature = Ed25519.Sign(privateKey, challenge_bytes)

NodeB → NodeA: POST /api/sync/authenticate { nodeId, challenge, signature }
NodeA: verify signature vs whitelist[nodeId].publicKey
NodeA → NodeB: { token: "bearer_token_base64" }  (TTL 1 hour)
```

**Why challenge-response instead of a simple API key?** The private key is never transmitted over the network. Even if someone intercepts the challenge and signature, they are single-use (TTL 60 seconds).

### Step 2: Pull (receiving events)

```
NodeB → NodeA: GET /api/sync/events?afterSequence=N
               Authorization: Bearer <token>

NodeA → NodeB: [
  { sequenceNum: N+1, eventId: "guid", nodeId: "node-a-id", lamportTs: 2089,
    eventType: "article_create", payload: "{...}", signature: "base64",
    actorType: "web", actorName: null },
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
      "displayName": "Hetzner",
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

## Event Types

| Type | Payload | Synced? |
|---|---|---|
| `article_create` | title, treePath, tags, ciphertext, encrypted_dek, iv, dek_iv, timestamps | Yes |
| `article_update` | same (full replacement, not diff) | Yes |
| `article_delete` | deleted_at | Yes |
| `whitelist_add` | nodeId, displayName, publicKeyB64, apiAddress, canGenerateEmbeddings | Yes |
| `whitelist_update` | nodeId, displayName, apiAddress, canGenerateEmbeddings | Yes |
| `whitelist_revoke` | nodeId | Yes |
| `comment_create` | commentId, articleId, text, createdAt | Yes |
| `comment_delete` | commentId | Yes |
| `folder_create` | folderId, path, name, parentPath | Yes |
| `folder_rename` | folderId, oldPath, newPath | Yes |
| `folder_delete` | folderId | Yes |
| `media_create` | mediaId, articleId, ciphertextB64, encrypted_dek, iv, dek_iv | Yes |
| `media_delete` | mediaId | Yes |

**Important:** `article_update` sends the full ciphertext (not a diff). This is simpler and safer — diffs on encrypted data are meaningless.

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

Node A receives the sentinel from Node B during join. At every sync session, it checks: decrypt(sentinel_B, local_master_dek) == "BeeMemoryBank"? If not — DEKs are incompatible.

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
| `EventApplier.cs` | ~200 | Apply*Async methods: signature validation → conflict check → INSERT/UPDATE |
| `EventPayloads.cs` | ~80 | Record types for JSON payload of each event type |
| `EventSignature.cs` | ~50 | BuildPayload: length-prefixed deterministic serialization |
| `ConflictResolver.cs` | ~40 | ShouldApply: Lamport ts comparison + nodeId tiebreaker |
| `LamportClock.cs` | ~30 | Thread-safe: Tick(), Update(), restore from database |
| `CleanupService.cs` | ~40 | Background: purge expired tombstones + conflict versions |
| `PendingEmbeddingProcessor.cs` | ~60 | Background: generate embeddings for articles with embedding_pending=1 |
