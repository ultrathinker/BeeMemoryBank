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

**A node that joins with the master password is a superadmin** (owner's decision, BMB-42).
`JoinEndpoints` creates the entry with `is_superadmin = 1`, and every join flow (web, CLI, phone)
records the node it joined through the same way — one rule, `JoinAuthority`. `EventApplier` checks
the flag before applying `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `hard_delete`,
`restore_network` and `master_password_changed`; an event of one of those types signed by a peer
without the flag is rejected with `UnauthorizedAccessException`. Article/folder/comment events are
never gated on it. A blind node (NodeId with the blind mark) is never a superadmin and cannot use
`/api/join` at all.

Only a node whose `is_superadmin` bit is set on the *receiving* node's own whitelist row can:

- **Revoke another peer** — `ApplyWhitelistRevokeAsync` flips the target's row to `Status = "R"` on
  every node that receives and accepts the event. A node ignores a revoke aimed at itself, so the
  revoked peer keeps its own data; it just stops being accepted anywhere else.
- **Hard-delete an article or folder everywhere** — `hard_delete` routes to
  `HardDeleteService.ApplyRemoteAsync`, which physically purges the rows and deletes the `.enc`
  media files. It is not a tombstone and there is nothing to restore from afterwards.
- **Restore the whole network from its own snapshot** — see "Network-Wide Snapshot Restore" below.
  Peers with `auto_accept_restore` apply it unattended.

For a while joins were recorded content-only (`is_superadmin = 0`). That protected nothing: the
master password already hands over the DEK, and a node that knows it can do everything a
superadmin can on its own copy. What it did do was split the mesh — such a node applied its own
hard delete or password-change notice locally while every peer refused the event, and nothing ever
reconciled the two. The flag can still be cleared (or set) deliberately:
`PUT /api/whitelist/{nodeId}/superadmin` changes `is_superadmin`, and the new value rides in a
`whitelist_update` event. Because each node decides
from *its own* whitelist row, a promotion or a demotion only binds the nodes the event actually
reaches — and a peer running a build from before this field existed deserialises the payload
without it and leaves the row exactly as it was. The affected node is also not informed of a
change made about it elsewhere (it holds no whitelist row for itself), so a node that thinks it
became a superadmin, or that lost the role, may go on emitting cluster-state events that every
other node quietly rejects. See "Initial Join: Snapshot-Based Bootstrap" below for the one place a
node's authority *is* inferred rather than announced: the immediate node a joiner dials into.

**One further consequence for the event log itself:** until metadata encryption is switched on, payloads are
plaintext JSON. A peer that only ever authenticates and pulls still learns every article title, tree path and tag name
in the vault, even though bodies stay encrypted — see [encryption.md](encryption.md#what-is-not-encrypted-by-design).
After the switch, content events travel sealed (a blind copy and anyone who only relays them learn none of it), while
control events (whitelist, rotations, recovery material, anchors, checkpoints, restores, the metadata keys and the
activation) stay readable; a full node still holds every title in its own tables. See
[Metadata Encryption (Protocol 4)](#metadata-encryption-protocol-4).

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
    eventType: "article_create", payload: "{...}", signature: "base64" },
  ...
]  (up to 1000 events per request)
```

**The actor fields never leave the node (2.5.2).** The example above has no `actorType`/`actorName`/`viaAgentName`/`entityId` on purpose: those are local metadata, and everything a node serves or pushes goes through `SyncWire.Strip`, which nulls them so the keys are absent from the JSON entirely. None of the four is covered by the event signature, so nothing on the wire needed them: a receiver stamps the actor fields from its own whitelist row and derives the entity id from the signed fields (see `EventEntityId`). Older peers read the missing keys as null, so 2.5.2 nodes keep syncing with 2.0.x–2.5.1 in both directions.

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

**What is owed to a peer (2.6.4).** A push carries everything but the events the peer wrote itself, so the delivery status counts the same rows (`unsyncedCount`, `IEventLogRepository.CountEventsToRelayAfterAsync`), not every event after the peer's position. When the push finds nothing left to send, the log after the peer's position can only be its own events; the position then moves to the head of the log that was read before the push (`ISyncPushPositionRepository.AdvanceCursorAsync`, only for a peer that already has a row, never after a stalled send) and `pushed_at`, which is "last delivery", is left as it was. Before this, such a peer showed "N changes not delivered" for ever and the stuck position looked like a gap to the compaction of the log.

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

A node can enable Invisible Mode to hide itself from sync partners. When invisible, the node rejects every incoming sync call (503) and its own sync loop makes none, so its partners see it as not answering.

Since 2.6.2 the switch is in **Admin > Invisible Mode** (superadmin only), no longer in the sync status of the header; while it is on, the sync icon of the header carries a badge and the status says so with a link to that card. `GET`/`POST /api/sync/invisible` are unchanged.

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
      "lastContactAt": "2026-04-09T12:30:00Z",       // the last successful CONTACT, either way (2.6.2; it used to be the last push of events)
      "lastContactDirection": "outbound",            // "outbound" = we -> it (this node called) | "inbound" = it -> us (it called this node)
      "lastPushAt": "2026-04-09T09:12:00Z",          // the last time events were delivered to it
      "lastOutboundAt": "...", "lastInboundAt": "...",
      "canBeCalled": true,           // it has an address: this node calls it; without one it calls this node
      "state": "answering",          // answering | not_answering | calls_us | unknown ("not checked since this node started")
      "online": true,                // contact within the window below, and not failing
      "notAnsweringSince": null, "notAnsweringReason": null
    }
  ]
}
```

**Contact is not events.** A contact is a completed session: this node's loop called the peer and the session ended well (`PeerContactLog`, "we -> it", with the pull position a finished pull leaves on disk as the trace that survives a restart), or the peer authenticated to this node or called it with a token ("it -> us", with the peer's last authentication on its whitelist row as the trace). The last push of events is its own field, because on a quiet network it grows old while the peers talk every minute. A node this one calls whose calls fail is **not answering** since the first failure of the streak, with the reason ("no answer: connection refused", "it answered 403: its door is closed to this node") - unless something was heard from it since, in which case it is alive and calls in. A node without an address is never asked; it **calls this node**, and the status shows when it last did. 

**What is remembered, and for how long.** The contact memory is per process and is not saved: after a restart a node this one calls is **unknown** (the status shows what the disk remembers as the last contact, and says "not checked since this node started") until the next cycle or the next check has called it; it is never shown as "answering" on the strength of a position on disk. What was learned by calling a peer belongs to the address it was called at: when a peer is given another address (here, or by a replicated whitelist update), is revoked, or is replaced, the pause for an unreachable peer, the closed-door window, the failure reason and the streak are dropped, the sync loop is signalled, and the new address is called in the next cycle (`IPeerStateResetter`, called by the writers of the whitelist; the loop also notices by itself at the start of every cycle and of every check). The log keeps only peers on the whitelist and none that nobody has touched for 30 days.

**Online** means contact within `2.5 x` the cadence the contact is expected at, and at least two minutes: this node's own sync interval (`BMB_SYNC_INTERVAL_SECONDS`, 60 s by default: 2.5 minutes) for a node it calls; the longer of that and five minutes for a node that calls in, a phone (12.5 minutes by default). A recorded time more than two minutes ahead of this clock is not believed (a peer's clock, a clock set back); a smaller lead is taken as "now".

**The reason is a closed set.** The sentence shown for a failure is chosen from a fixed list (no answer, refused, timed out, a certificate that does not match, a closed door `401/403`, another node answering at the address, an answer that cannot be read as a sync answer, a connection that broke, no sync server at the address, a peer that is behind this node's compaction (or the other way round), and one generic "the call failed (the details are in this node's log)"). The text of an exception - an address, a path, a header, a parser's message - never becomes part of it; the node's log has the type of the failure, not its text.

```
POST /api/sync/nodes/{nodeId}/check        (superadmin, internal key, never an agent)
{ "outcome": "answered" | "not_answering" | "calls_us" | "busy" | "invisible" | "unknown_node", "reason": "...", "applied": 0, "node": { ...the row above... } }
```

A double-click on a node of the sync status (the header dropdown and the "Sync Delivery Status" dialog), or the **Check now** button in its row (a real button: focus, Enter, Space, a tap), shows a spinner on the row and calls this: for a node with an address this node runs one session with it now (the loop's lock, the same session a cycle runs) and the row says "Alive - synced just now" or "Not answering" with the reason; for a node without an address nothing is asked and the row says it calls this node, with the time of its last call. `404` for a node that is not on the whitelist (or this node), `409` while Invisible Mode is on or a cycle is running. One check of one node runs at a time (a second request for it is answered `busy` at once, not queued behind the first), a node is checked at most once every five seconds, and a check waits at most 20 seconds for the loop's lock. The call makes this node call out, so the Web proxy accepts it only from a page of the site: the request must carry the page's anti-forgery token in the `RequestVerificationToken` header (and the cookie it pairs with), otherwise it answers `400` and nothing is forwarded.

`GET /api/whitelist/sync-status` (the Trusted Nodes "Last contact" column) answers from the same facts: `lastContactAt` is the last contact, with `direction`, `pushedAt`, `state`, `notAnsweringSince`, `notAnsweringReason`. `updatedAt` keeps the meaning it always had for a client of an earlier version - the later of the last pull from the node and the last push to it, `0001-01-01` when there is neither (a node that only calls in).

The same projection tells the blind nodes' contact: `GET /api/blind-nodes` carries `contact` for every node, the Blind nodes page shows its direction, the last delivery and "not checked since this node started", the host status of a blind node (`bmb blind status`, `/api/blind/status`) lists the additive fields `contact_at`, `contact_direction`, `last_push_at`, `last_outbound_at`, `last_inbound_at`, `state`, `online`, `not_answering_since`, `not_answering_reason` per peer (`last_contact` keeps its delivery meaning), and a blind node this PC cannot call for six hours is a `not_answering` banner on the Blind nodes page (a banner only; the desktop's notifications stay the `silent` and stale-baseline kinds'). This PC's own my-standing call to a blind node is outbound contact; the protocol column it also stamps is never read as the node calling in.

Protected by `InternalKeyValidator` — only accessible from the Web proxy (same server). Used by the sync status UI widget and post-save toast.

## Network Map Endpoint

```
GET /api/sync/network        (superadmin, internal key; the Web proxy route is /api-proxy/sync/network)

{
  "self":  { ...a node... },
  "peers": [ { ...a node... } ],
  "links": [ { "from": "guid", "to": "guid", "basis": "observed" | "expected", "state": "...", "scheme": "https", "host": "...", "port": 443,
               "seenFrom": "203.0.113.7", "lastOkAt": "...", "failingSince": null, "reason": null } ],
  "isInvisible": false
}
```

The page the header's **Sync Status** title opens (`/Network`) draws this: this node and the nodes of the whitelist on a circle (one in the middle, two on a line, three a triangle, four or more a circle), an arrow for each call. A node carries the fields of the delivery status plus `kind` (`full`: has an address and is called; `device`: has none, a phone or a node behind a NAT, it calls in; `blind`: a blind copy), `platform` (`windows`, `macos`, `linux`, `android`, `ios`), `inContainer`, the version and protocol it last showed, its address as `scheme`, `host` and `port` (never a path, a query or credentials), `seenFrom` (where its last call came from, as this node's server saw it), and for this node alone `notes`, `folders` and `changes`.

**What is measured and what is expected.** A call is one direction: `from` calls `to`, at the address of `to`. This node can measure only the calls it is one end of: the ones it makes (`observed`, state `answering`, `not_answering` or `unknown`, with since when and why a failing call fails) and the ones it receives (`observed`, state `heard` within the time a call is expected in, `stale` after it). The calls between two other nodes are given as `expected`, by the rule of the network - every node calls every node that has an address, and a blind node calls only where nobody can call it - and are never drawn as seen.

**Where `platform` comes from.** A node says what it runs on in its sync identity (`GET /api/sync/identity`: `platform`, `inContainer`) and in the authentication it sends when it calls (`POST /api/sync/authenticate`). `NodePlatform` is a closed set; what a peer sends is read through `NodePlatform.Normalize` and anything else is dropped. A container is `DOTNET_RUNNING_IN_CONTAINER=true` or `/.dockerenv` (`/run/.containerenv` for Podman). Both are additive fields: a node of an earlier version sends nothing and is drawn from what is known of it (a server, a device, a blind copy) until it runs 2.6.4. What a node told is kept in memory, per process, with the rest of the contact memory (`PeerContactLog`): after a restart a peer is drawn without a platform until the next call, which is within a sync interval for a node this one calls and within the cadence of a phone for one that calls in.

## Node-Local Entities (Not Synced)

Agents, users, and folder ACL entries are node-local — they are created on the node where they belong and are **not** propagated through the sync event stream. A user created on the primary node does not exist on a replica, and vice versa. See `docs/architecture.md → Node Topology` for rationale.

## Event Types

| Type | Payload | Synced? |
|---|---|---|
| `article_create` | title, treePath, tags (deprecated, empty), conceptTags, ciphertext, encrypted_dek, iv, dek_iv, timestamps | Yes |
| `article_update` | same (full replacement, not diff) | Yes |
| `article_delete` | deleted_at | Yes |
| `whitelist_add` | nodeId, displayName, publicKeyB64, apiAddress, canGenerateEmbeddings, **isSuperadmin**, tls_spki, tls_trust | Yes |
| `whitelist_update` | nodeId, displayName, apiAddress, canGenerateEmbeddings, isSuperadmin, tls_spki, tls_trust | Yes |
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
| `restore_network` | snapshot_hash, restore_point_ts, file_size_bytes, expires_at, source_url, filter_secrets | Yes |
| `dek_rotation_proposed` | encrypted_new_dek, iv, new_dek_epoch, rotation_ts, expires_at, originator_node_id | Yes |
| `dek_rotation_commit` | proposed_event_id, encrypted_new_dek, iv, new_dek_epoch, rotation_ts, originator_node_id | Yes |
| `sealed` | the envelope `{v, k, n, b, c}` of a content event (protocol 4, after the switch) | Yes |
| `metadata_key_set` | key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, created_at | Yes |
| `metadata_activation` | activation_id, key_id, generation, activated_at | Yes |

**Content and control.** Every type is one of two classes (`EventClasses`, a guard test fails on a type that is in
neither): *content* — article, comment, folder, media, concept tag events and `hard_delete` — and *control* — the
whitelist, checkpoint, restore, rotation, password-change, recovery, anchor, sealed-secret and metadata key/activation
events. After the metadata switch a content event travels only as `sealed`; a control event is readable at every
protocol. The full list of fields that stay readable is `docs/design/protocol4-clear-fields.golden.txt`.

**Callable nodes (`tls_spki`, `tls_trust`).** A blind copy calls a node the whitelist says it may call, and how it trusts that
node's TLS endpoint travels with the row ([ADR 0007](adr/0007-blind-copies-call-a-full-node.md)). `tls_trust` is `pin` (the
node's key is `tls_spki`: a Docker blind node, or a full node with a certificate of its own) or `public-ca` (no pin: the system's
certificate chain must vouch for the https address, as for a Let's Encrypt certificate behind a reverse proxy); on an update
`none` takes the mode and the pin away, and an absent `tls_trust` changes nothing. Both fields are optional and **sync protocol 3 is
unchanged**: an older node ignores `tls_trust` and applies `tls_spki` as it always did, which is why an update that stops pinning
also sends `tls_spki: ""` — the older node then drops the stale pin instead of failing every sync after a certificate renewal. A
node that does not know the mode never lists the row for pairing. `public-ca` never keeps a pin, and a mode a build does not know
is kept on the row as written, with no pin: the row is not callable there and is never read as `pin` (only an event with no `tls_trust`
at all is read the old way, as a plain pin). Like every whitelist change it needs a superadmin originator and loses to a newer version of the row.

**A peer that is closed to a blind node is left alone (2.6.2).** The sync loop of a blind node (`BMB_ROLE=blind`) dials every whitelisted peer that
has an address, once a minute - in the usual setup the full node calls the blind node and these calls add nothing, but a blind node paired from a hub
(ADR 0007: a full node it can call, because nobody can call the blind node) has to make them. A full node that does not let the blind node's address in
(a reverse proxy with an allow-list answers 403) used to draw a warning a minute for ever. Now a blind node that gets 401 or 403 from a peer logs one
line for the episode ("... is closed to this blind node; asking again every 01:00:00"), asks again once an hour, logs one more line when the peer
answers again, and keeps everything else as it was. A full node's loop is unchanged: it warns every cycle, as before.

An *episode* is one continuous refusal by one peer at one address. It ends, with the one "no longer answers 401/403" line, when the peer gives any answer
that is not a refusal - a good session, another status (a 404, a 502 of its proxy), a failure of its data - and a refusal after that is a new episode with
its own line. It also ends, with no line, when what was learned is forgotten: the peer is revoked or deleted, or it is given another address (the refusal
came from the old one; the new address is asked in the next cycle, not after the hour). A peer that does not answer at all does not end it - nothing is
known about its door - so a gap with no answer between two refusals is still one episode. The memory is the size of the whitelist: `ReconcilePeers` drops
the episodes of nodes that are no longer on it, and "this refusal opens the episode" is decided under one lock, so concurrent sessions write one line.

**Important:** `article_update` sends the full ciphertext (not a diff). This is simpler and safer — diffs on encrypted data are meaningless.

**Media Linking:** When an article is saved (created or updated), the system automatically scans its content for image references. For each newly found image that was previously "orphaned" (uploaded but not linked), a `media_link` event is generated. This ensures that images are properly associated with articles for ACL enforcement and sync consistency across the network.

**Hard Delete:** Unlike `article_delete` (soft delete, recoverable from trash), `hard_delete` permanently purges an article or folder and its media from the local database and disk. On receiving a `hard_delete` event, subscribers purge the corresponding rows immediately and suppress any later `article_update` events for the same entity (via the `tbl_event(event_type, entity_id)` gate index). Only a Superadmin can initiate it through the Admin UI.

## Event Actor Tracking

Every event contains `actor_type` and `actor_name`:
- `actor_type = "web"` — initiated via Web UI
- `actor_type = "agent"`, `actor_name = "agent_name"` — MCP agent
- `actor_type = "cli"` — via CLI

Implementation: `IActorProvider` with two implementations — `HttpActorProvider` (API, from HTTP context) and `CliActorProvider` (CLI).

**Wire hygiene (2.5.2).** `actor_type`, `actor_name`, `via_agent_name` and `entity_id` stay on the node that authored the event. They are local metadata — read by this node's own surfaces (event history, audit UI) — are not part of the signed payload, and are stripped from the sync event stream (pull, push, blind-restore) by `SyncWire.Strip`, so they never cross the network. The applier log keeps the same discipline: its lines name event ids, never titles, paths, tag names or actor names, because the log travels with nodes whose plaintext it must not leak (blind nodes especially).

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
- `Held` — a sealed content event this node cannot apply yet (the vault is locked, its metadata key is not here, or an
  earlier row waits). It is stored and counts as received; the drain applies it later. See
  [Held events](#held-events-received-and-applied).

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

All three ways to join take the snapshot: the Setup page (`POST /api/init/join`), `bmb join`
(the CLI, since 2.4.0; before that it stopped after the key exchange and the first sync then asked
for sequence 0, which a producer that ever compacted or re-keyed its log answers with 410 forever),
and the phone. `bmb join` refuses a producer whose sync protocol version is not compatible (the same
gate as the Setup page) before it writes anything, and a blind node, which has no join door, is
named as such ("join a node that holds the data"). When the snapshot step fails, `bmb join` prints
the reason, exits non-zero and removes the rows it had written, so it can simply be run again; the
phone does the same. The Setup page keeps its partial node and says "wipe & retry". Since 2.5.1 the
joiner also tells the producer (`POST /api/join/abort`, below), so the producer does not keep the
joiner's row, which would count as a peer that never synced and hold back compaction
(`docs/compaction.md`) until an admin revoked it. Against a producer or proxy that does not know the
route (a producer of 2.5.0 or older answers 404) nothing changes: the messages of `bmb join`, the phone
and the Setup page name that row (display name, node id) and say to revoke it.
The producer builds the whole snapshot before it sends the first header, which takes minutes on a
big vault, so a joiner waits for it up to 30 minutes; only the requests of the key exchange are held
to 30 seconds each.

**Flow (joiner perspective):**

1. `POST /api/init/join` on the local Web/API: key exchange with remote, receive master DEK,
   create local `tbl_node_identity`, `tbl_key_slot`, and populate `tbl_whitelist`. Each other
   peer in the producer's `JoinResponse` whitelist carries, besides its key and address, how the
   producer reaches it over TLS: `tlsTrust` (`pin` or `public-ca`) and, for `pin`, `tlsSpki` (the
   SHA-256 pin of its TLS key, base64url). The joiner records them on the inherited row, so it
   checks that peer exactly as the producer does (`SpkiPinRegistry`) instead of through the public
   CAs, which a self-signed peer never passes. Both fields are optional: a producer of 2.5.0 or
   older sends neither (the peer is then recorded without a mode, as before), and a joiner of 2.5.0
   or older ignores them. A pin that is not a well-formed pin leaves that peer out of the join (the
   peer is learned later from the whitelist events) rather than unpinned (`JoinTls`).
2. Challenge+sig authenticate with the remote (standard `/api/sync/authenticate`).
3. `GET /api/sync/snapshot/for-join` (Bearer auth) → signed `.tar.gz`. Headers:
   - `X-BMB-Snapshot-CP-Seq`: sequence number the snapshot represents
   - `X-BMB-Snapshot-Lamport`: clock value at CP
   - `X-BMB-Snapshot-Producer`: producer's node ID
   - `X-BMB-Snapshot-Signature`: Ed25519 signature (base64)
4. Verify the signature against the producer's public key (from whitelist).

   **Step 1's whitelist rows are where the two trust decisions in this handshake are made, and
   they are made independently.** The producer decides what it thinks of *this* joiner —
   `is_superadmin = 1`, because it proved the master password. The joiner decides what it thinks
   of the *producer* — also `is_superadmin = 1`, because the producer handed over a slot the
   password opens — and it cannot defer to the producer's real status, which never travels in
   this response at all: a node is never in its own whitelist (see
   `EventApplier.ApplyWhitelistAddAsync`'s self-skip), so there is nothing for the joiner to read
   the producer's real status from even if it wanted to defer the decision. The operator already
   made this call by typing the producer's URL and the master password that secures the whole
   vault; that is the trust anchor a fresh mesh bootstraps from, and it is why a single-node vault's
   founding node can administer a newly joined second device without an extra promotion step. It
   does not extend any further: the joiner's own belief about the producer has no bearing on what
   any other node in the mesh believes about the joiner — they learn that from the producer's
   `whitelist_add`, which they accept only if the producer is a superadmin in their own rows.
5. Import selected data tables only: `tbl_folder`, `tbl_article`, `tbl_article_body`,
   `tbl_concept_tag`, `tbl_article_concept_tag`, `tbl_concept_tag_edge`, `tbl_media`,
   `tbl_tombstone`, `tbl_conflict_version`, `tbl_projection_matrix`.
   Local `tbl_node_identity` / `tbl_key_slot` / `tbl_whitelist` / `tbl_migration` are
   preserved (they were populated in step 1).
6. Set `sync_position[producer] = cp_seq` so subsequent pulls only fetch tail events.
7. Initialize Lamport clock: `min(snapshot.lamport, local + MAX_CLOCK_ADVANCE)` where
   `MAX_CLOCK_ADVANCE = 1_000_000` (clock-skew attack mitigation). The value is also kept as the
   durable floor `lamport_floor` in `tbl_migration_marker`: the joiner holds no event that carries
   it, and every start initializes the clock from `max(MAX(tbl_event.lamport_ts), floor)`.
8. `MarkInitialSyncCompletedAsync` on `tbl_node_identity`.

Steps 6-8 are one shared routine (`SnapshotJoin.CompleteAsync`) for the Setup page, `bmb join` and the
phone (the Android app and the iPhone full app); without the durable floor of step 7 a restarted phone began
its clock at 0 and every edit it made lost last-writer-wins at its peers.

**Aborting a failed join (`POST /api/join/abort`, 2.5.1).** After the producer answered `POST /api/join`, any
failure on the joiner (a timeout, an unreadable answer, a key slot it refuses, a snapshot it cannot import) makes
`bmb join`, the Setup page and the phone (Android and iPhone) call it once, best effort (10 seconds, never
replacing the original error). Body: `{ masterPassword, nodeId, ed25519PublicKeyB64 }`, over the same
no-redirect, pinned connection as the join; through a join code's LAN door also its `X-BMB-Join-Token`.
The producer checks the master password exactly as the join does (it is the same code), then removes only
the row of that attempt: that node id with that key, active, written within the last hour, never synced (no
row in `tbl_sync_push_position`, the test compaction uses), never its own or a blind id. The removal is an
ordinary signed `whitelist_revoke` (logged first, as in `DELETE /api/whitelist/{nodeId}`), so the mesh, which
heard the `whitelist_add`, hears the removal. Answers: `200 {aborted, removed}` (`removed: false` when there was
nothing to remove, so asking twice is fine), `400` (own or blind id, malformed), `401` (wrong password), `409`
(a row that has synced, has another key, is too old, or the producer is locked: nothing changed), `429`.
One gate serializes the row step of `POST /api/join` and the abort; a join of an id the joiner already
aborted (a request that arrived after the joiner gave up) is refused with `409` and writes no row, for two hours
or until the producer restarts. The route is in the node's public surface and rate-limited like the join; an
audit entry `join_aborted` records it (node id and display name, no secret). The sync protocol stays 3 and no
existing message changes: a 2.5.1 joiner against an older producer gets 404 and falls back, an older joiner never
calls it. The desktop node's "Connect a device" door passes the route (with the code's token, which it does not
spend) and, after the joined device has its snapshot, stays up for two more minutes serving only it.

**Re-join:** if a node's position falls below the producer's last compaction CP, regular
sync returns 410 Gone. The node has no data to recover from — admin must wipe and rejoin.
See `docs/compaction.md`. The node says so: every page of its Web UI shows "This node cannot
catch up ... Wipe this node and join again" (from `snapshotRequired` in `GET /api/sync/status`),
and so does `bmb status` (the scheduler also records the state in `tbl_migration_marker`, key
`sync_snapshot_required`, because the CLI is another process). A successful sync clears it.

**Secret filtering:** the snapshot sent to a joiner is built with `filterSecrets=true`, which
drops `tbl_node_identity`, `tbl_session`, `tbl_agent*`, `tbl_sync_*`, `tbl_compaction_log`,
`tbl_event`, `tbl_key_slot`, `tbl_user`, `tbl_folder_acl_entry`, `tbl_audit_log`,
`tbl_hard_delete_audit`. `tbl_projection_matrix` is intentionally kept (shared embedding
projection across all peers with the same master DEK).

## Network-Wide Snapshot Restore

A network restore can use only a snapshot from this vault: this node's own snapshot, or one from another node in the same network that shares its master key. A snapshot from a different vault cannot be opened, whichever password is entered.

When a superadmin restores from a snapshot in network-mode, the snapshot is broadcast to every whitelisted peer through a `restore_network` sync event. Peers apply it unattended when auto-accept is on for the originator — the same peer-acceptance pattern later reused for DEK rotation. This is the "whole network" option of the Admin restore dialog; the other option, "this node only", is not a sync event at all: the node leaves the network and becomes a new node (new node id and key pair, empty `tbl_whitelist`, sync positions and event log) — see [snapshot-restore.md](snapshot-restore.md). The originator of a network restore keeps its identity.

**Initiator flow (`POST /api/snapshots/restore-network`, `bmb snapshot restore-network`, or the Admin dialog's "Restore the whole network"):**

1. Superadmin only (never an agent key), vault unlocked. The Admin page also sends the master password, which is re-checked. The snapshot is named by file name (a snapshot this node made) or by id (an uploaded one).
2. Build a filtered copy of the snapshot — the identity, key slots, users and sync state are stripped — in `snapshots/restore-pending/{eventId}.bin`, and take its sha256.
3. Emit a `restore_network` event signed with the initiator's Ed25519 key. Payload includes the sha256, `restore_point_ts`, the file size, an expiry 30 days out and the source URL.
4. In the background the initiator takes a safety backup and applies the filtered copy to itself (the replicated tables only: users, key slots and the identity stay). The copy stays on the initiator and is exposed at `/api/snapshots/restore/{eventId}/file` for peers to pull. Distributed-seeding endpoints (`/api/sync/challenge`, `/api/sync/authenticate`, the snapshot file download) are explicitly allowlisted in `MaintenanceMiddleware` so peers can authenticate and download even while the initiator is in maintenance mode.

The start is written to the audit log (`snapshot_restore_started`, `Mode=network`).

**Peer flow (`EventApplier.ApplyRestoreNetworkAsync`):**

1. On receiving the `restore_network` event, look up the originator's `tbl_whitelist.auto_accept_restore` flag.
2. **Auto-accept = true** → enter maintenance mode, fetch the snapshot file from the originator, verify sha256 + Ed25519 signature, apply locally. Set `tbl_restore_event_state.state = Applied`. Set a replay-shield in `tbl_restore_replay_shield` so any pre-restore events from the originator that arrive late are silently dropped (they would otherwise be zombie state from the pre-restore world).
3. **Auto-accept = false** → write `tbl_restore_event_state.state = Pending`. The restore is **not** applied; there is no Apply button in the Admin UI. Switching auto-accept on for the originator makes the peer pick the pending event up the next time it syncs or is unlocked (`RetryPendingRestoresAsync`).
   There is no UI prompt, notification, or manual approval path while it is pending.
4. **Cancel** (`POST /api/snapshots/restore/cancel`) → `state = Cancelled`; the pending download is dropped.

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

- **Auto-accept enabled:** when the COMMIT event arrives, `EventApplier` fires `IDekRotationApplier.AutoAcceptCommitAsync()` in the background. The peer applies the full destructive re-wrap (all DEK columns, node data keys, removal of agents that carry a wrapped Master DEK, sentinel update) without human intervention. If the session is locked at the time, auto-accept is deferred and retried on the next unlock (`RetryPendingAutoAcceptsAsync`).
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

When a peer auto-accepts a rotation, the destructive part on the PEER side keeps all user key slots intact (still wrapped with the old DEK) and only deletes the `tbl_agent` rows that carry a wrapped Master DEK (auto-unlock agents) and `recovery`/`os_auto_unlock` slots. The initiator-side flow is different — there, all OTHER user slots are dropped because the initiator's local users are the canonical set. On the peer, a user's slot stays around so that on the next login, lazy rewrap can migrate it transparently:

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

## Metadata Encryption (Protocol 4)

Release 2.6.0 can encrypt the metadata that sync protocol 3 carries in clear (titles, paths, tags, folder and file
names, the shape of the tree; the cryptography is in [encryption.md](encryption.md#metadata-encryption-sync-protocol-4)).
It is **off until a superadmin switches it on for the whole network**. Until then a 2.6 node declares protocol 3 and
behaves as 2.5.1, except that it already understands protocol 4 (`SyncProtocolVersion.CanApply` 1-4) and advertises
that it does.

### Capabilities and the protocol a node declares

- Every 2.6 node sends the capability `metadata-envelope-v1` and its version at authenticate, in `/api/sync/identity`
  and in `report-position`; the receiving node records what each peer last showed on its whitelist row (capabilities,
  version, when). The readiness report reads those observations.
- `ISyncProtocolPolicy` decides what a node declares and accepts: protocol 3 before the switch, 4 after. Before the
  switch any peer declaring 3 or more is accepted; after it, a peer declaring 4, or 3 with the capability (a 2.6 node
  that has not pulled the activation yet: it must be able to, or the switch would deadlock). A 2.5 node gets 426 at
  authenticate and, on its side, never pulls from a node declaring 4: the two are cut off both ways, as intended.

### The full Android app

The full Android app (2.6.x) syncs through the same `SyncClient` as every node, so it needs nothing of its own for encrypted metadata: it declares protocol 3 until
it has applied the activation and 4 after, always with `metadata-envelope-v1`, and sends its version (the repo `VERSION`, as `AssemblyInformationalVersion`) at
authenticate and in `report-position`. The node shows it on the readiness report of the Metadata card (Admin) next to what the phone last declared. After the
activation the phone opens sealed events with the keys of its unlock, and keeps what it cannot open yet while it is locked, like any full node (see "Held events"
below). `AndroidFullAppWiringTests` keeps the app wired to the shared composition (`AddSync()`), since the app itself cannot be built by a test project.

### An address with no sync server

A peer's address can answer and still have no sync server behind it: a router or a web server answers 404 (or 405, 410, a redirect) for `/api/sync/identity`,
or answers 200 with a web page or the JSON of another service. `SyncAddress.ReadIdentityAsync` (used by `SyncClient` and by the screens that check an
address) says so with `NoSyncServerException`: "No sync server at this address: it answered 404 Not Found. Check the address of this device." The text never
names the address (it may carry a user name or a token; the screen that asked knows which address it asked about). A shut door (401, 403), a server in trouble
(502, 504, the maintenance 503), a captive portal (511), a host that does not answer in time and no connection at all keep their own words: those are servers or
absent peers, not wrong addresses.

- A redirect is "no sync server", not "no answer": the pinned handler every sync client is built on (`SpkiPinGuardHandler`) never follows one and says so with a
  3xx `HttpRequestException`, which the identity read turns into the same finding as a 3xx answered to a plain client (and its text names neither address).
- The identity page is read as a bounded stream: the headers first (a declared length over 64 KB ends it), then at most 64 KB within ten seconds. An endless or a
  trickling body costs neither memory nor a hung screen; a body that is too big is "no sync server", one that does not arrive in time is "no answer".
- The scheduler's cycle result carries each peer that was not served (`SyncCycleResult.PeerFailures`, each with the peer's name and a reason without
  addresses or keys); `Problem` is the cycle's own error or those reasons joined, and is null only when every peer with an address was reached. A wrong address
  is paused like an absent peer (one warning per streak, growing pauses), and the cycles in which it is left alone still report it.
- **A peer that is too old is a failure, not "nothing to do".** A peer on a protocol this node no longer syncs with (below 3, or - once this node has applied the
  metadata activation - 3 without the `metadata-envelope-v1` capability) used to end the session quietly: the phone's heartbeat said "ok (applied 0)" and the
  contact log "answered". It now throws `PeerIncompatibleException` ("it runs an older version (protocol 3) than this device syncs with: upgrade it"), which reaches
  `PeerFailures`, the contact log and the check of a peer like any other failure, is paused with a growing pause (one warning per streak) and is asked again at
  once when the peer is changed or checked.
- The contact log (BMB-313) shows the same sentence as the reason a node "is not answering": "no sync server at this address: it answered 404 Not Found".
- The Android app's background status reads "error: Laptop: no sync server at this address: it answered 404 Not Found" where it used to read "ok (applied 0)";
  Sync now on the Status page says the same in its pop-up.

### Changing a peer's address

There is one writer of a peer's address: `PeerAddressService` (`libs/BeeMemoryBank.Sync`), called by "Change node address" on the Trusted nodes page
(`PUT /api/whitelist/{nodeId}/address`) and by "Edit URL" on the phone's Status page. The generic `PUT /api/whitelist/{nodeId}` no longer takes `apiAddress`
(it answers 400 and points at `/address`; renaming still works). The route is superadmin, **not an agent** (an agent owned by a superadmin is a superadmin, and
used to be able to move a peer), and needs the master password; the phone is its owner's own app and asks nothing more. In this order:

1. The peer exists, is active and is not this node.
2. **The policy** (`PeerAddressPolicy`) accepts the address and makes it canonical (lower-case scheme and host, default port and trailing slash dropped, `https://`
   added when no scheme was typed). Nothing is sent anywhere before this. Refused, with a fixed sentence that does not quote what was typed: another scheme than
   http or https, a user name or password in the address, a `?` or `#` part, an empty host, an unspecified address (`0.0.0.0`, `::`), a link-local address
   (`169.254.0.0/16`, `fe80::/10`, which is where the cloud metadata services live) and the names of those services - the same addresses written as numbers
   included. **Loopback and private LAN addresses are accepted**: nodes live on LANs, and the tests of a setup use loopback.
3. The same canonical address as the row has is a **no-op**: nothing is probed, published, audited or reset.
4. No other active peer has it.
5. The address is probed with a client that follows no redirect (the pinned handler, named `bmb-address-probe`), and a sync server must answer there with this
   node id **and the public key on record** (compared in constant time; the key never changes through an address change). Another id, another key, no sync
   server and no answer at all are refused with their own sentence. The phone may save an address that does not answer after its owner confirmed it; the first
   session that reaches it checks who answers.
6. The `whitelist_update` event is logged first and the row written with the version it minted; the loop is told (`IPeerStateResetter.PeerChanged`: the failure
   streak, the pause, the closed window and the contact record of the old address are forgotten and a cycle is signalled) and the other nodes are told by the
   event. An address change that arrives from another node does the same when it is applied.
7. One audit line (`peer_address_changed`) says who, which peer, and the old and new address as 12-character fingerprints of `host:port` - never the address.

The sync client also compares the key a peer states against the key on record on every session (`PeerKeyMismatchException`), not only the node id: the id is a
claim, the key is what the network knows the node by.

**Every other place that writes an address uses the same rule.** A running node writes a peer's row in exactly three methods of `PeerAddressService`, which share the
steps above (the policy first - a refused address writes and announces nothing; the key on record never changes; the event is logged first with the address the row stores
and the row carries its version; a repeat that changes nothing writes nothing; the loop is told when the place or the pairing moved; a moved place or pairing is audited
with fingerprints): `ChangeAsync` (a person changes the address, probed), `ApplyProofAsync` (an existing peer proved itself some other way: a re-join with the master
password, the hub pairing whose probe read the key, a blind node paired again from its code) and `AdmitAsync` (a new row: a join, a blind node added). A replicated
`whitelist_add` / `whitelist_update` is applied in two named methods of the event applier with the same policy, the last-writer-wins version rule, the key never replaced
and the loop's reset. The remaining writers build rows from an authoritative source that carries its own versions - the host's answer to a join, a restore or a seed's
manifest, a backup, the phone's replica - and take the address through `PeerAddressPolicy.ForStorage` (the canonical form, or nothing). A join or a pair code with a
refused address is a 400 before any request is built from it or any row is written. `PeerAddressWritersInventoryTests` names the METHOD of every assignment of an
address in the production sources, with the reason it may (`docs/design/peer-address-writers.golden.txt` lists the files): a new writer fails it until it is reviewed
against these rules.

**Dialling a name.** A literal address is judged when it is typed; a **name** (`https://169.254.169.254.nip.io`) passes that and then resolves to whatever its DNS
says. So every HTTP client that dials a peer or the other computer of a join - the sync client, the address probe, the restore client, the join client of a typed
address, the one pinned by a join code, and the named no-redirect client that a join without a code and the probe relay use - connects through `PeerSocketConnector`
(in the Crypto assembly, so that the phones' join code path has it too; `PeerHttp.NoRedirectHandler`, `SpkiPin.CreatePinnedHandler` and `JoinHttp.CreateClient` are the
factories): it resolves the host
itself, refuses if **any** answer is an address a node may not call (unspecified, link-local, the cloud metadata addresses, multicast), connects only to the addresses
it has just checked, and checks the address it ended up connected to, so a name that resolves differently the second time cannot move a connection. No system proxy is
used (a proxy would resolve and connect for this node, and the check would judge the proxy). Loopback and private LAN addresses are allowed. The hub probe uses the
same connector with its stricter rule (it also refuses loopback). `PeerHttpArchitectureTests` fails for a handler that is built without the connector unless the place is
listed with the reason it never dials a peer (the chat's image fetch, an S3 backup target, the OpenRouter client, the node's own front on loopback).

**What a failure says.** A screen, an API answer, a status or a Warning says what *kind* of failure it was and never the words of the other side or of the stack:
`PeerContactLog.Describe` for a failed call (no answer, refused, timed out, a certificate that was not accepted, a closed door, an answer that cannot be read) and
`FailureText.Say` for everything else (a failure this code threw to be read says its own sentence; the rest is told as a kind). An answer's reason phrase and body are
never read for a message (a join door's refusal is told by its status), a pair code and a join code are checked by the address policy before use, and a blind node
refuses a seed with a typed code whose sentence is fixed (`BlindSeedRefusal`). A warning about an unreachable peer says the kind of failure and the exception's type,
the node's LAN status, the blind job detail and the web pages' error lines use `FailureText.Say`, and the phone apps' generic busy helper shows
`FailureText.Story`: the whole text of a failure of local work (a disk, a database, a file), and only the kind of failure for HTTP, socket, TLS, JSON, timeout and the
typed peer refusals, told apart by the type of the exception. `NetworkFailureTextArchitectureTests` reads the networking projects and fails for an exception's message
inside a log call, in a status member, or in the text a person reads, unless the place is listed with the reason it is local work. `ExceptionTextSitesInventoryTests` (`docs/design/exception-text-sites.golden.txt`) lists every file that
uses an exception's message and fails when a new one appears, and nothing may read a `ReasonPhrase`.

### Sending and receiving sealed events

- After the switch `EventLogger` seals every content event with the newest metadata key this node can open and writes
  protocol 4. Without such a key it refuses the write instead of writing in clear.
- A full node opens a sealed event and runs every check on the opened view as before (signature on the outer event,
  hard-delete gate, path checks, replay shield, superadmin gate on the inner type). The row it stores is the original
  outer event, which it relays byte for byte; its own activity views read the local columns `local_type`,
  `local_article_id` and `entity_id`, which are never served.
- A blind host (the Docker blind node, a phone, a desktop blind copy) never opens anything: it stores sealed events as
  relayed and, once activated, drops content offered in clear.
- **Stragglers.** A 2.6 node that wrote content before it pulled the activation wrote it in clear and signed it; full
  nodes apply it as usual and ask for an early baseline (`late_protocol3`). No full node serves or pushes protocol-1..3
  content to a blind peer after the switch: `GET /api/sync/events` leaves such rows out and fills the page
  with the rows the requester may have (it reads on until the page is full or the log ends, so a client that follows
  its cursor never stalls on a left-out row), and the push loop passes over them as if accepted. A blind copy gets content only sealed or
  inside a baseline.
- **Blobs.** A sealed event lists the hashes of the blobs it needs in its envelope, so blob transport ships them
  without opening anything (`BlobReferences`).

### Held events: received and applied

A full node that cannot apply a sealed content event yet stores it as **held** (`tbl_event.apply_state = 1`, migration
038) with a reason: `locked` (the vault is locked), `key` (its metadata key is not openable here yet) or `order` (an
earlier row is held: content applies in log order, clear stragglers included). Control events never wait.

- *Received* covers held rows: the pull cursor, `report-position` and the push answer's `LastAppliedSequence` move over
  them, and `GET /api/sync/events` serves them like any other row, so a locked node is a relay. *Applied* is
  `MIN(held sequence) - 1`, or the head when nothing is held.
- **The drain** (`HeldEventDrainService`) applies held rows in sequence order, exactly once: after every unlock (chained
  after the DEK rotation retry, so a key of a rotation accepted at unlock opens first), when a `metadata_key_set` arrives,
  and every 5 minutes while unlocked rows wait. It stops when the vault locks, and at a `restore_network` row whose
  restore is still pending, downloading or applying, so that content received after a restore is applied after it. A
  `key` hold whose key no `metadata_key_set` named within 30 days becomes state 3 ("unknown metadata key"); a row that
  does not open with the key it names, or that a handler refuses for good, also becomes state 3 — it was relayed and
  stays in the log, never applied.
- A held row keeps the originator's superadmin flag as it was at receipt: a revocation that arrived and applied while
  the node was locked does not make it reject content an unlocked node had already applied.
- **Refused while anything is held**, with a message saying to unlock and wait: compaction, a DEK rotation proposal, a
  network restore, the metadata switch (and, on the hosts that produce them, baselines and join snapshots).
- **Status.** `GET /api/sync/status` has `held: { count, locked, key, order, oldest_at, applied_through_seq, head_seq,
  quarantined_after_receipt, warning, message }`; `bmb status` prints a `Held:` line; the Web layout shows "N changes
  wait to be applied". It warns when more than 100,000 rows are held or the oldest is older than 7 days.

### Metadata keys and DEK rotation

The switch creates key generation 1. On an activated network every DEK rotation makes its initiator create the next
generation under the new DEK (`metadata_key_set`) and ask for an early baseline (`key_rotation`); peers create nothing.
A node the rotation left out cannot open the new generation, so it cannot read metadata written afterwards. A node
that has not accepted the rotation yet holds events sealed under the new key (`key`) until it does.

The new generation is part of the rotation, not an afterthought. The transaction that swaps the DEK also inserts the next
`tbl_metadata_key` row (wrapped under the new DEK) and a durable marker (`tbl_migration_marker`, key
`mek_publish_pending`), so there is never a committed new DEK without its metadata key. Right after the commit one more
transaction appends the `metadata_key_set` event and deletes the marker. If that does not succeed (a failed write, a
crash) the rotation is **not** complete: its progress stays `Completing`, the completion record waits, the step is
retried every minute and at every unlock, and meanwhile this node refuses to write sealed metadata (HTTP 503, code
`metadata_key_publish_pending`, `Retry-After: 60`, on every route that writes articles, comments, folders, media, tags or
purges) rather than seal with a generation the node left out can still open. A refused write changes nothing: the check
comes before the first change. A write that is already running when a rotation starts is finished first; the rotation
waits for it (and gives up, changing nothing, if it does not end within two minutes), so nothing is ever appended under
the old key after the new DEK is in place. What the rotation still has to finish after its commit (the completion audit
line, the `Completed` step, the compaction) is recorded in the same transaction as the commit and finished from that
record, also after a restart and after a failure. A second rotation is refused (409) until the marker is gone and the
previous rotation is finished.

## Switching Metadata Encryption On

An operator guide. The switch is **one way**: there is no off switch.

1. **Update every device** to 2.6.0 or newer — servers, desktops, phones, the Docker blind node, desktop blind copies —
   and let each one sync once, so this node sees that it is ready. Update the devices that are already paired: a 2.5
   node that joins a 2.6 node afterwards, and a 2.5 Docker blind node that a 2.6 full node tries to seed, are refused with
   "upgrade this node first" (the snapshot or package carries schema 39; 2.5.2 stops at 36 — the rule every release that adds a
   migration has), while a pair that was already syncing keeps syncing at protocol 3 during the upgrade.
2. **Check readiness**: Admin → *Metadata encryption* → *Ask every device now*, or `bmb sync readiness`
   (`GET /api/sync/readiness`). Every active device is `ready`, `not_capable` (it runs an older version), `stale` (not
   seen for more than 14 days) or `unknown` (never seen by this node or a device it asked). The switch is refused while
   anything blocks it: a device that is not ready, this node being locked, a device that does not see this node as a
   superadmin, a DEK rotation in progress, changes held on this node, or the switch already being on. A device you no
   longer use: revoke it, it then no longer counts.
3. **Take a fresh backup of this node and keep it offline if you want one.** It holds your titles in clear. There is no way back from the node:
   the switch cannot be undone, and the node never restores a backup that is not encrypted (2.6.2).
4. **Switch it on**: on the Admin card tick "I took a fresh backup", enter the master password and confirm; or
   `bmb sync activate --yes` (it asks for the master password, or reads it with `--password-stdin`). The API is
   `POST /api/sync/activation` `{ "password": ... }`: superadmin, never an agent; the password is checked, the vault is
   not unlocked by it; 409 lists the blockers. This node creates key generation 1, writes the activation, declares
   protocol 4 and seals what it writes from that moment; the audit log records `metadata_activated`. Every other device
   switches as it syncs: it pulls the key and the activation. A device still on 2.5 is refused (426) until it is
   updated.
5. **Afterwards**: blind copies take an encrypted (format 2) baseline and purge the readable content they held; a
   2.5 binary must not be started on an activated volume (unsupported: it cannot read sealed events, and its peers
   refuse it).

**Rollback** means restoring the backup taken in step 3 (a network restore, or rebuilding the network from it) and
losing every change made since the switch. There is no deactivation event.

**Retiring readable copies.** The switch protects what travels and what blind copies keep from then on; it does not
reach copies made before it. Old backups and snapshots of full nodes, restic snapshots of the blind node made before
its first encrypted baseline, and a blind node's database kept aside by an earlier reseed (listed as `legacy_copies` in
its status) still hold titles in clear: delete them, or keep them as carefully as the backup of step 3, once the
network runs encrypted. A full node's own database keeps titles in clear by design (it shows and searches them).

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
