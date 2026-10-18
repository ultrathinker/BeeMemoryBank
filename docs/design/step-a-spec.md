# Step A (2.6.0): implementation specification

Status: design of Step A of 2.6.0, in three parts that can be built side by side (section 15). The decision and its
reasons are [ADR 0009](../adr/0009-metadata-confidentiality.md). Base: the tree of v2.5.1; every `file:line` below is
for that tree. Where this document and the code disagree, the code describes today and this document describes the
target; a contradiction this document did not foresee is resolved by taking the reading that fails closed.

Words: **MUST** is a contract other parts rely on; **SHOULD** is the recommended way, a part may deviate for a reason
it states. "Part 1/2/3" are the parts of section 15.

## Contents

1. Terms
2. Before and after activation
3. Event classes and the protocol-4 envelope
4. The metadata key (MEK)
5. Capabilities, readiness, activation, protocol policy
6. Full node: received and applied positions, held events, ordering
7. Chunked container and the baseline
8. Package format 2
9. Blind store: operational database, adoption, deltas, trimming, blob clean-up
10. Blind status, alarms and recovery status without counts
11. Join after activation
12. Restore from a baseline and deltas
13. Phones and desktop blind copies
14. Interfaces between the parts
15. File ownership and composition rules
16. Red tests
17. Golden files, docs, migrations
18. Open points
- Appendix A: contract files (verbatim)
- Appendix B: test helper `MetadataCanaries.cs` (verbatim)

## 1. Terms

| Term | Meaning |
|---|---|
| content event | an event type that can carry human-authored metadata (list in 3.1). After activation it travels only sealed. |
| control event | every other type: whitelist, rotation, recovery material, anchors, checkpoints, restore, MEK, activation. Always clear. |
| sealed event | the protocol-4 wire form of a content event: outer type `sealed`, envelope payload (3.3). |
| opened view | the content event a full node gets back by opening a sealed event: inner type, inner article id, inner payload. |
| MEK | metadata encryption key, 32 random bytes per generation, wrapped under the master DEK (section 4). |
| baseline | encrypted, signed, filtered copy of a full node's replicated tables at a cut, without blobs (section 7). |
| delta | an event a node stores after the baseline it serves or holds: the original signed bytes. |
| cut | the position in a given node's own log up to which a baseline covers that log. |
| serving node | the node that assembles and signs a package (a full node, hub or blind node). |
| producer | the full node that built and signed a baseline. Must be a superadmin in the receiver's whitelist. |
| received position | how far this node has durably stored a peer's log (today's `tbl_sync_position`). |
| applied position | the highest local sequence up to which every row of this node's log is applied (6.6). |
| held event | a received event a full node stored but has not applied yet (vault locked, key missing, or behind another held event). |
| relayed event | a sealed event a blind host stored without opening (it never can). |
| activation | the owner's one-way switch (5.4); recorded by the `metadata_activation` control event. |
| capability | string a 2.6 build advertises: `metadata-envelope-v1`. |

## 2. Before and after activation

2.6.0 ships switched off. Until a node has applied `metadata_activation`:

| Behaviour | Before activation (2.6.0 default) | After activation |
|---|---|---|
| declared protocol (`/api/sync/identity`, authenticate, report-position, my-standing) | 3, plus the capability list | 4, plus the capability list |
| peers accepted (token, pull) | declared >= 3 (as 2.5.1) | declared >= 4, or declared 3 with the capability (window until a later regular release drops it) |
| events authored | protocol 3, clear, exactly as 2.5.1 | protocol 4: content sealed, control clear |
| events applied | protocol 1-4 (a sealed event arriving early is opened or held) | protocol 1-4; protocol-1..3 content = straggler (5.7) |
| events served/pushed to a blind peer | everything, as 2.5.1 | no protocol-1..3 content event, ever |
| blind host receiving clear content | materialises it, as 2.5.1 | drops it (counted as accepted) |
| blind package built/accepted | format 1 (`bmb-blind-package-v1`) | format 2 only (`bmb-blind-package-v2`) |
| join snapshot | plain SQLite, as 2.5.1 | chunked container under the master DEK (section 11) |
| `/api/join` from a joiner without the capability | accepted | 426 |
| locked full node | applies clear events while locked, as 2.5.1 (nothing is ever held: no sealed event exists) | holds sealed events, applies them after unlock |
| blind status note count | removed in 2.6.0 already | removed |
| baselines | not produced | produced by unlocked superadmin full nodes |

A 2.6.0 node can always read a sealed event, before it has applied the activation itself: gossip may deliver
sealed events first. Nothing else changes before activation; in particular no new code path writes anything a
2.5.1 peer could see. A new vault (init) also starts switched off; a one-node vault passes the readiness check at
once (2.6.0 ships switched off; see open point 18.1).

## 3. Event classes and the protocol-4 envelope

### 3.1 Classes

Every constant of `EventTypes` (`libs/BeeMemoryBank.Sync/EventPayloads.cs:5-37`) is classified by a new static
class `EventClasses` (Appendix A.1). A guard test fails when a constant is in neither or both lists, the
same pattern as `EventAuthorizationGuardTests`.

| Class | Types |
|---|---|
| content (sealable) | `article_create`, `article_update`, `article_delete`, `comment_create`, `comment_delete`, `folder_create`, `folder_rename`, `folder_delete`, `media_create`, `media_delete`, `media_link`, `concept_tag_rename`, `concept_tag_merge`, `concept_tag_delete`, `hard_delete` |
| control (clear) | `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `snapshot_checkpoint`, `restore_network`, `dek_rotation_proposed`, `dek_rotation_commit`, `master_password_changed`, `recovery_box_set`, `recovery_box_retire`, `retired_link_set`, `state_anchor`, `sealed_secret_set`, **`metadata_key_set`** (new), **`metadata_activation`** (new) |
| outer | **`sealed`** (new) |

`EventAuthorization` (`EventPayloads.cs:52-121`): `sealed` is `AnyPeer` (the gate runs again on the opened view,
6.3); `metadata_key_set` and `metadata_activation` are `SuperadminOnly`.

### 3.2 Wire form of a sealed event

A sealed event is an ordinary `SyncEvent` (`libs/BeeMemoryBank.Core/Models/SyncEvent.cs`), serialised as today
(`JsonSerializerDefaults.Web`, camelCase), with these values. Receivers MUST check the starred rules and refuse
the event as malformed (`InvalidDataException`, permanent) otherwise.

| Field | Value |
|---|---|
| `eventId`, `nodeId`, `lamportTs`, `createdAt`, `signature`, `sequenceNum` | as today |
| `eventType` | `"sealed"` * |
| `protocolVersion` | `4` * (a `sealed` event with another version is malformed) |
| `articleId` | `null` * (the inner article id is encrypted) |
| `entityId`, `actorType`, `actorName`, `viaAgentName` | `null` on the wire (* a server MUST null them when serving a protocol-4 row; release 2.5.2 does so for every row) |
| `payload` | the envelope text (3.3) |

A **protocol-4 control event** is today's control event with `protocolVersion` 4 and its clear payload; a
protocol-4 event whose `eventType` is a content type is malformed * (an activated author always seals content).
An event of protocol <= 3 is read exactly as today.

### 3.3 Envelope (the `payload` text of a sealed event)

UTF-8 JSON object, written without whitespace in this property order (receivers accept any order but MUST refuse
missing, duplicate or unknown properties):

```
{"v":1,"k":"<key id>","n":"<nonce>","b":["<blob hash>",...],"c":"<ciphertext>"}
```

| Property | Rule |
|---|---|
| `v` | envelope version, the integer `1`. Anything else is malformed. A future envelope comes with a new protocol and capability. |
| `k` | MEK key id: 32 lowercase hex characters (section 4). |
| `n` | AES-GCM nonce: standard base64 of exactly 12 bytes, from the CSPRNG for every envelope. |
| `b` | ciphertext blob hashes the event needs, from the inner payload's `ciphertext_sha256` (article create/update, media create), else `[]`. Each 64 lowercase hex (`BlobReferences.IsWellFormedHash`), unique, sorted ascending (ordinal), at most 16 entries. |
| `c` | standard base64 of AES-256-GCM ciphertext followed by the 16-byte tag. Decoded length L: `16 + 64 <= L <= 16 + 1 MiB` and `(L - 16) % 64 == 0`. |

Size of the whole `payload` text: at most 1.5 MiB (the push and pull byte budgets of `SyncEndpoints.cs:60-66`
are untouched).

### 3.4 Inner plaintext

```
plaintext = UTF8( {"t":"<inner type>","a":<article id or null>,"p":<inner payload object>} ) || 0x00 * k
```

- `t`: one of the content types of 3.1. Any other value: the event fails to open (permanent).
- `a`: the value the protocol-3 event would carry in `ArticleId` (`EventLogger.cs`: the article for article and
  comment and media-link events, `Guid.Empty` for concept-tag events, `null` otherwise), as a "D"-format string or
  JSON `null`.
- `p`: the protocol-3 payload object of that type (`EventPayloads.cs` records), embedded raw: the opened view's
  `Payload` is exactly `p`'s JSON text (`JsonElement.GetRawText()`), so every existing handler, `EventEntityId` and
  `BlobReferences` read it unchanged.
- padding: `k` zero bytes, `0 <= k < 64`, so the length is a multiple of 64. Opening strips trailing 0x00 bytes,
  then requires exactly the three properties.
- the JSON before padding is at most 1 MiB; an author refuses to seal a larger one (no event of 2.6 comes close:
  bodies travel as blobs).

### 3.5 Cryptography of the envelope

- **Event key** of MEK generation g: `EK = HKDF-SHA256(ikm = MEK, salt = empty, info = "bmb-meta-event-v1", L = 32)`.
- **Seal**: `AES-256-GCM(key = EK, nonce = n, plaintext, aad = AAD)`; tag length 16; output `ciphertext || tag`.
- **AAD** (bytes, all integers little-endian, `LP(s)` = int32 byte length of UTF-8(s) followed by those bytes; the
  same encoding as `EventSignature.WriteString`, but written with `BinaryPrimitives` explicitly):

```
"BMB-META-ENV-V1\0"            (16 ASCII bytes, last one 0x00)
LP(eventId.ToString("D"))
LP(nodeId.ToString("D"))
int64(lamportTs)
LP(eventType)                  ("sealed")
LP(createdAt as UTC "o")       (same normalisation as EventSignature.cs:21-22)
int32(protocolVersion)         (4)
int32(v)                       (1)
LP(k)
int32(count of b)
LP(b[0]) ... LP(b[count-1])
```

- Nonce: 96 random bits per envelope; never derived, never reused. A test seals the same inner event 10,000 times
  and requires distinct nonces and ciphertexts.
- Opening failure of any kind (bad tag, wrong key, changed AAD field, changed blob list, padding or JSON rule
  broken): `InvalidDataException`; never a fall-back to reading the payload as protocol 3.

### 3.6 Signature coverage

`EventSignature.BuildPayload` (`libs/BeeMemoryBank.Sync/EventSignature.cs:12-24`) is **not changed**. It covers
event id, node id, Lamport time, type (`sealed`), article id (empty), the payload (the whole envelope text: key id,
nonce, blob list, ciphertext) and the protocol version (4), and the creation time. So a relay cannot alter any
clear field or the ciphertext, cannot relabel a sealed event as another type or protocol, and a sealed event signed
by node X cannot be replayed as node Y's. The AAD binds the same clear fields into the AEAD as well, so the
ciphertext cannot be lifted into another event by a key holder without re-encrypting.

### 3.7 Receipt checks any node makes

In this order, inside `EventApplier` (section 6.3 has the whole pipeline): protocol version known (1-4,
`SyncProtocolVersion.CanApply`); `EventInvariants` (`libs/BeeMemoryBank.Sync/EventInvariants.cs`); already in the
log (any state) -> `Applied`; originator lookup and Ed25519 signature (as `EventApplier.cs:88-120`); **shape**: for a
`sealed` event `SealedEnvelope.Parse` (3.3, 3.2 starred rules), for a protocol-4 content type: malformed. A blind
host stops here and stores the event (9.4). A full node goes on to open it (3.8) or hold it (6.4).

### 3.8 Opening on a full node; the opened view

`VaultEnvelopeOpener` looks the key id up in the MEK ring (4.5). Key unknown or not openable now:
`Hold("key")`; vault locked: `Hold("locked")`; else it opens and returns the opened view:

```
new SyncEvent { SequenceNum, EventId, NodeId, LamportTs, Signature, CreatedAt   = from the outer event,
                EventType = t, ArticleId = a, Payload = p raw text, ProtocolVersion = 4, EntityId = null }
```

Everything after the signature check runs on the view: `EventEntityId.Derive` (`libs/BeeMemoryBank.Sync/EventEntityId.cs`),
the hard-delete gate (`EventApplier.cs:147-156`), the path check (`:166`), the replay shield (`:174-182`), the
superadmin gate on the inner type (`:196`), the handler switch (`:221-299`). The row appended to `tbl_event` is the
**original outer event** (the bytes that relay onward), plus the local columns of 6.1: `local_type = t`,
`local_article_id = a`, `entity_id` = the derived value. Those three are plaintext on a full node (as every title in
its tables is until Step B) and MUST never be served.

### 3.9 Authoring

`EventLogger.AppendEventAsync` (`libs/BeeMemoryBank.Vault/Sync/EventLogger.cs:371-447`), after activation:

- control type: as today, `ProtocolVersion = 4`.
- content type: build the protocol-3 event exactly as today (`EventEntityId.Derive` and the entity-id cross-check
  at `:388-409` run on it), then seal: inner `{t, a, p}`, key = `IMetadataKeyRing.Current` (4.8), outer event with
  `EventType = "sealed"`, `ArticleId = null`, `Payload` = envelope, `ProtocolVersion = 4`; sign the outer event;
  append it with `local_type`, `local_article_id`, `entity_id` and the actor columns filled from the inner event (the
  author's own activity view keeps working). No current key: refuse to write (`InvalidOperationException`, the
  operation fails); never write clear content after activation.

Before activation `ProtocolVersion` stays 3 (`SyncProtocolVersion.Current` today) and nothing is sealed.

### 3.10 Blob references

- `BlobReferences.Collect` (`libs/BeeMemoryBank.Sync/BlobReferences.cs:19-39`): for a `sealed` event, the hashes in
  `b`; otherwise as today. `ReferencesBlob("sealed")` is true. Blind hosts therefore fetch and ship the blobs of
  sealed events exactly as today (`BlobTransport.cs`).
- Blob garbage collection (`libs/BeeMemoryBank.Storage/Sqlite/BlobRepository.cs:139-150`) MUST also keep every hash
  in `json_each(payload, '$.b')` of `sealed` rows and every hash in `tbl_baseline_blob` (9.1): one
  edit of that statement.

### 3.11 Clear-field allow-list

`docs/design/protocol4-clear-fields.golden.txt` is the allow-list of clear fields: for `sealed`, the outer `SyncEvent`
properties and the envelope properties; for each control type, the JSON property names of its payload record. A test
serialises one sample of every type and compares; adding a property to a control payload or to the envelope fails until
the file is updated on purpose, which forces the question "is this metadata?".

## 4. The metadata key (MEK)

### 4.1 Table (migration `037_metadata_keys.sql`)

```sql
CREATE TABLE IF NOT EXISTS tbl_metadata_key (
    key_id          TEXT    NOT NULL PRIMARY KEY,   -- 32 lowercase hex, random
    generation      INTEGER NOT NULL,               -- 1 at activation, +1 per DEK rotation
    dek_fingerprint TEXT    NOT NULL,               -- DekFingerprint.Of(the master DEK it is wrapped under)
    epoch_hint      INTEGER NOT NULL DEFAULT 0,     -- tbl_node_identity.dek_epoch of the author, for sorting/UI only
    wrapped         BLOB    NOT NULL,               -- AES-256-GCM ciphertext || tag of the 32-byte MEK
    iv              BLOB    NOT NULL,               -- 12 bytes
    author_node_id  TEXT    NOT NULL,
    created_at      TEXT    NOT NULL,
    lamport_ts      INTEGER NOT NULL DEFAULT 0,
    event_id        TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_metadata_key_generation ON tbl_metadata_key (generation DESC, key_id);

CREATE TABLE IF NOT EXISTS tbl_metadata_activation (
    activation_id   TEXT    NOT NULL PRIMARY KEY,
    key_id          TEXT    NOT NULL,
    generation      INTEGER NOT NULL,
    activated_at    TEXT    NOT NULL,
    author_node_id  TEXT    NOT NULL,
    event_id        TEXT    NOT NULL,
    lamport_ts      INTEGER NOT NULL,
    applied_at      TEXT    NOT NULL
);

ALTER TABLE tbl_whitelist ADD COLUMN last_capabilities TEXT;          -- comma-separated, local observation
ALTER TABLE tbl_whitelist ADD COLUMN last_app_version TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN last_capabilities_seen_at TEXT;
```

Rows of `tbl_metadata_key` are immutable once written (insert-or-ignore by `key_id`); there is no retirement: old
generations open old events and old baselines. Both tables join `SnapshotTables.Replicated` (so a join snapshot
carries them) and the new list `SnapshotTables.Control` (8.4). `RekeyTables` (`libs/BeeMemoryBank.Rekey/RekeyTables.cs`):
`tbl_metadata_key` = `KeyMaterial` (the re-key replaces all rows by one new generation-1 key under the new DEK when
the node is activated, and logs nothing: the re-keyed node is alone), `tbl_metadata_activation` = `Plaintext`.

### 4.2 Wrapping

`wrapped || tag = AES-256-GCM(key = master DEK, nonce = iv, plaintext = MEK, aad = UTF8("bmb-mek-v1:" + key_id + ":" + generation + ":" + dek_fingerprint))`.
Nothing else wraps a MEK. A blind host stores the row and cannot open it.

### 4.3 Event `metadata_key_set` (control, SuperadminOnly)

Payload (snake_case like its neighbours):

```json
{ "key_id": "...", "generation": 1, "dek_fingerprint": "...", "epoch_hint": 3,
  "wrapped": "<base64>", "iv": "<base64>", "created_at": "2026-...Z" }
```

Applier (shared, works on every host): validate shapes (hex id, 12-byte iv, 48-byte wrapped), insert or
ignore by `key_id` with `author_node_id`, `lamport_ts`, `event_id` from the event; then `IHeldEventDrain.RequestDrain()`
(held `key` events may now open). Never opens anything. A full node that cannot open it (it has not accepted the
DEK rotation the key belongs to) keeps it and its sealed events wait (6.4).

### 4.4 Creation

- **Activation** (5.4): the activating superadmin creates generation 1 if no row exists that it can open.
- **DEK rotation:** on the rotation's initiator, when the node is activated
  (`ISyncProtocolPolicy.SealsMetadata`), the next generation is part of the rotation itself, in two steps.
  1. **In the transaction that swaps the DEK** (`DekRewrapper.RewrapAllAsync(createNextMetadataKey: true)`, initiator
     only; a peer creates nothing, whatever the caller passes): insert the `tbl_metadata_key` row, generation = max
     existing + 1, wrapped under the **new** DEK (`MetadataKeyCrypto.Create`), `event_id` empty until the event is
     logged, and set the durable marker `tbl_migration_marker('mek_publish_pending', '<key_id>')`. There is then no
     committed state with a new DEK and no new key row, and a failure to create the row rolls the rotation back. A marker
     that already stands (an earlier rotation's key is unpublished) is never overwritten: the second rotation fails
     before it changes anything, and `ProposeRotationAsync` / `AcceptCommitAsync` refuse it up front (409). The same
     transaction writes the **completion record** (`DekRotationCompletionRecord`, `tbl_migration_marker` key
     `dek_rotation_completion_pending`, JSON: commit event, new epoch, initiator, snapshot file name, agent keys removed):
     what the initiator still has to finish after the commit. A completion record that stands refuses the next
     proposal/accept the same way.
  2. **After the commit, with the new DEK in the session** (`MetadataKeyPublishService.TryPublishAsync`): in **one
     transaction** append the `metadata_key_set` control event for that row, set the row's `event_id` and `lamport_ts`
     to the event's, and delete the marker; then signal sync and request the early baseline (reason `key_rotation`) so
     blind copies move to the new key.
  The failure of step 2 is **not** treated as done. The rotation's progress stays `Completing`
  (`DekRotationFlowStep.Completing`, last in the enum), `dek_rotation_completed` and `Completed` and the post-rotation
  compaction wait for it, and the step is repeated every 60 s while the vault is unlocked and at every unlock (in
  `SessionService.TriggerPostUnlockCatchUp`, after the rotation retry and before the drain) until the marker is gone.
  A crash between the commit and step 2 resumes the same way after the next unlock: the marker and the row survive, the
  slot was re-wrapped in the same transaction, so the unlock puts the new DEK back, and the event is appended before
  this node writes anything else sealed.
  **Finishing the rotation is separate from publishing the key, and durable.** What is left (the `dek_rotation_completed`
  audit line, the `Completed` step, the post-rotation compaction) is read from the completion record, never from the memory
  of a process: `DekRotationService.FinishPendingCompletionAsync` (also `IDekRotationApplier.FinishPendingCompletionAsync`,
  default no-op) runs when the key was published (the `Published` event), at every unlock (right after the publication
  retry) and every 60 s while unlocked and a record stands. It does nothing while the marker stands (the progress shows
  `Completing`), writes the audit line only if this commit has none yet, deletes the record last, and a failure keeps the
  record for the next try. So a restart after the commit, a failure after the marker was cleared, and a failed audit write
  all end in the rotation being completed exactly once. The notifications after the publication (waking the sync loop,
  the early baseline) are best effort: they cannot make the publication look failed, and they are no part of what is
  retried.
  **While the marker stands, this node refuses to author sealed metadata** (`EventLogger`, through
  `IMetadataKeyPublishGate`): `MetadataKeyPublishPendingException`, HTTP 503 with `Retry-After: 60` and the body code
  `metadata_key_publish_pending`. It must not seal with the previous generation, which a node the rotation left out can
  still open, nor with the new one before its event exists. Control events are not sealed and are not held back.
  A marker whose key row is gone (a re-key replaced the keys) is forgotten with a warning; the marker is local
  bookkeeping and, like the others in that table, is never imported from a snapshot.
  **Authoring is one scope** (`IEventLogger.BeginAuthoringAsync`, `AuthoringBarrier`, Vault). Every operation that changes
  local state and then logs sealed content (article, comment, folder, media, concept-tag change, hard delete; an import or a
  copy as one operation) enters a *shared* scope as its first statement, before any lock of its own, and holds it until
  its event is logged. Entering (1) refuses with the exception above **before anything changed**, so a refused write leaves
  no folder, tag, comment, media row or purge behind without its event, and (2) keeps the rotation from committing: the
  rotation's initiator holds the *exclusive* side (`DekRotationService`, around the pre-rewrap hooks and the DEK
  transaction, up to the swap of the session DEK) and waits for the scopes in flight (120 s, then a precondition failure
  that leaves the commit pending; nothing has been changed). A write that sealed under generation g is therefore appended
  before the swap, never after it; one that starts after the commit finds the marker. The shared scope is reentrant for
  one logical flow (services call services), so a waiting rotation never deadlocks a flow already inside; `EventLogger`
  enters it again around seal and append, which covers a caller that holds none. Endpoints that translate
  `InvalidOperationException` locally let this exception pass (`when (ex is not MetadataKeyPublishPendingException)`), so
  every such route answers 503 + `Retry-After`; the MCP tools print its message.
  **No baseline is produced while the marker stands** (`BaselineProducer`): `IMetadataKeyRing.Current` already offers the new
  generation, and a baseline sealed under it would name a key no blind copy holds a row for. The producer refuses with `NoMetadataKey`, the
  scheduler keeps the request and asks again, and the publication requests the early `key_rotation` baseline itself.
  Peers create nothing. A peer that accepts the rotation can open the key from the event; a peer the rotation left
  out (ADR 0006 envelopes) cannot, so it cannot read metadata authored afterwards.
- Two keys with the same generation (two activations or rotations racing) are both valid: every event names its
  key; authoring picks by 4.8.

### 4.5 Key ring on a full node (`IMetadataKeyRing`, Vault)

- Holds unwrapped MEKs and their event keys **in memory only**, filled lazily: to open key `k`, read its row,
  take the candidate DEKs of the session (`SessionService.GetCandidateDeks`/`TryUnwrapWithCandidates`,
  `libs/BeeMemoryBank.Vault/Core/Services/SessionService.cs:531-640`: the current DEK, the in-memory retired ones
  and the persisted retired ones loaded at unlock, `:374-388`), pick the one whose `DekFingerprint.Of` equals the
  row's `dek_fingerprint`, unwrap.
- Cleared (arrays zeroed) on `SessionService.Locked` (`SessionService.cs:77`) and on DEK swap.
- No open MEK, no derived key and no unwrapped copy is ever written to disk (a stolen disk must be useless). The locked node has
  none, by construction.

### 4.6 Password change, lost password, recovery chain

- Password change: nothing (MEK rows are under the DEK, not the password).
- Lost password: unchanged recovery. A restore resolves the master DEK and its retired chain from recovery boxes
  and links (`RecoveryRestoreService.ResolveKeysAsync`, `server/BeeMemoryBank.Api/Services/Recovery/RecoveryRestoreService.cs:187-209`;
  `RecoveredKeys.Current` and `.Retired` by fingerprint), then opens each MEK row with the DEK of its
  `dek_fingerprint` (`MetadataKeyCrypto.TryUnwrap`, Appendix A.9). Every package carries all MEK rows in
  `control.db` (8.4), outside the baseline they open.
- If every slot, box, link and authorised peer is lost, the metadata is unrecoverable, as the bodies are.

### 4.7 Recovery material on the blind side

Blind hosts store `tbl_metadata_key` and `tbl_metadata_activation` rows as they store recovery boxes: applied from
the control events, carried in every package and backup. Nothing on a blind host can open them; the boundary
tests (16.3, item 10) keep `MetadataKeyCrypto`, the ring and the opener out of every blind assembly (they are Vault types).

### 4.8 Which key seals

`IMetadataKeyRing.Current` = among rows this node can open now, the highest `generation`, ties broken by the
ordinally greatest `key_id`. Every node converges on the same choice once it holds the same rows and DEKs. A node
that has not accepted the latest rotation seals with the newest key it can open; that is still readable by every
current full node (they keep old DEKs) and only the rotation's lag window is exposed to a revoked node.

## 5. Capabilities, readiness, activation, protocol policy

### 5.1 Advertising the capability (all optional fields; older nodes ignore them)

| Where | Addition |
|---|---|
| `GET /api/sync/identity` response (`SyncEndpoints.cs:70-79`) | `capabilities: string[]`, `appVersion: string` |
| `POST /api/sync/authenticate` request (`SyncAuthRequest`, `server/BeeMemoryBank.Api/Models/SyncModels.cs:12`; sent by `PeerAuthenticator.cs:150-155`) | `Capabilities: string[]?`, `AppVersion: string?` |
| `POST /api/sync/report-position` query (`SyncEndpoints.cs:364-415`; sent by `SyncClient.cs:532-548` and `BlindPhonePullClient.cs`) | `capabilities=metadata-envelope-v1` (comma-separated), `appVersion=2.6.0` |
| `GET /api/sync/my-standing` response (`SyncEndpoints.cs:423-447`, `MyStanding`) | responder's `capabilities`, `app_version`, `activated`, and (blind responder) `store` summary; each `peers[]` row: `capabilities`, `app_version`, `capabilities_seen_at` (Appendix A.6) |
| `POST /api/join` request (`JoinRequest`, `server/BeeMemoryBank.Api/Models/Requests.cs:74-79`) | `ProtocolVersion: int?`, `Capabilities: string[]?` |

`appVersion` is `AppVersion.Current`. A build advertises `MetadataCapabilities.Advertised` (Appendix A.3).

### 5.2 Observations

A node records, per whitelist row (local, never replicated), the last capabilities, app version and time it saw,
next to `RecordProtocolVersionAsync`: when a peer authenticates or reports a position here (after the signature
verified, as `SyncEndpoints.cs:190-191, 414-415` do for the protocol), and when this node completes an authenticated
handshake with a peer whose `/api/sync/identity` it read on the same connection, and from a blind
responder's `my-standing` (`BlindNodeManager.AsksForReseedAsync`,
`server/BeeMemoryBank.Api/Services/Blind/BlindNodeManager.cs:245-262`, through the `IWhitelistRepository` capability
method).

### 5.3 Readiness report

`GET /api/sync/readiness` (internal key, superadmin, not an agent). It merges this node's observations with the
`peers[]` rows of `my-standing` from every active, callable full peer it can reach within 10 s each, and from the
blind nodes it manages. Each active whitelist row gets the newest observation and a status:

| Status | Rule |
|---|---|
| `ready` | capability seen, observation not older than 14 days |
| `not_capable` | newest observation lacks the capability (a 2.5 node) |
| `stale` | newest capable observation older than 14 days |
| `unknown` | never observed by this node or any peer it asked |

Response:

```json
{ "activated": false, "activation": null,
  "ready": false,
  "blockers": [ { "code": "peer_not_ready", "node_id": "...", "display_name": "...", "status": "not_capable" },
                { "code": "vault_locked" } ],
  "peers": [ { "node_id": "...", "display_name": "...", "kind": "full|blind", "status": "ready",
               "protocol": 3, "capabilities": ["metadata-envelope-v1"], "app_version": "2.6.0",
               "seen_at": "...", "seen_by": "self|<node id>" } ] }
```

Blocker codes: `peer_not_ready` (one per active row that is not `ready`; revoked rows do not count),
`vault_locked`, `not_superadmin`, `rotation_pending` (a `tbl_dek_rotation_state` row Proposed or Committing),
`events_held` (6.8), `already_activated`. CLI: `bmb sync readiness`. Admin page: a "Metadata encryption" card
with the table, the blockers in words, and the action of 5.4. The answer of "Ask every device now" sits next to its
button and stays on the card for ten minutes, a refused switch leaves the form, and once the switch is on the card is
one status line ("On since ...").

### 5.4 Activation

`POST /api/sync/activation` body `{ "password": "<master password>" }` (internal key, superadmin, not an agent,
re-authentication as the hub endpoint does). 200 `{ activation_id, key_id, generation }`; 409 `{ blockers }`;
401 wrong password; 423 locked. CLI `bmb sync activate` (password from prompt or `--password-stdin`). The Admin
dialog says: take a fresh backup first and keep it offline; it is plaintext and is the only way back.

`ActivationService` (Vault): under one lock, re-run 5.3 (refuse on any blocker); if no openable MEK row, create
generation 1 (4.4) and log `metadata_key_set` (protocol 3 stamp); log `metadata_activation` (protocol 3 stamp);
apply both to its own tables (insert rows, `ISyncProtocolPolicy.MarkActivated()`, observers of 5.5); audit entry
`metadata_activated` (ids only). From then on this node writes protocol 4.

Event `metadata_activation` (control, SuperadminOnly), payload:

```json
{ "activation_id": "<guid>", "key_id": "...", "generation": 1, "activated_at": "..." }
```

Applier: the named key row must exist, else `MetadataKeyMissingException : IDeferrableSyncFailure` (it travels
just before; deferred, retried); insert the row if the table is empty, otherwise ignore with a warning (activation
is one-way and happens once; a second activation from a race changes nothing); then `MarkActivated()` and the
observers. There is no deactivation event.

### 5.5 Effects of activation per host (`IMetadataActivationObserver`, Appendix A.4)

| Host | Observer | Effect |
|---|---|---|
| full node (server, desktop, Android, iPhone) | baseline scheduler | `RequestEarly("activation")`; invalidate the replica package cache and the join snapshot cache |
| Docker blind node | blind-node observer | `BlindState.SetReseedNeededAsync("metadata activation")` (`libs/BeeMemoryBank.Sync/Blind/BlindState.cs:30`): every superadmin full node that syncs with it reseeds it with format 2 (`BlindNodeManager.cs:225-236`); and `BlindReplicaPackageCache.RetireFormat1Async`: the format-1 replica package a phone may have cached (a `tar.gz` with the plain database, up to 30 minutes old) is retired at once, its files go with the last response that leases it (the first-adoption purge of 9.3 only cleans the database) |
| blind phone and blind desktop copy | phone observer | `InitialLoadDone = false`: the next heavy pass downloads a format-2 replica (the path a 410 takes today, `libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindPhoneSync.cs`) |

### 5.6 Protocol policy (`ISyncProtocolPolicy`, Appendix A.3)

A per-host singleton (in-process tests run several nodes; no static state), reading `tbl_metadata_activation` at
start and flipped by `MarkActivated()`.

- `Declared` = 3 before activation, 4 after. It replaces `SyncProtocolVersion.Current` wherever a node says or
  compares what it declares (`SyncEndpoints.cs:78, 442`, `PeerAuthenticator.cs:153`, `SyncClient.cs:532-548`,
  `BlindPhonePullClient.cs` report, `JoinEndpoints.cs:211`, `CoreBlindStatusContributor.cs`, `BlindAlarmService.cs`,
  `InitEndpoints.cs`, `JoinCommand.cs`, `SnapshotJoinClient.cs`, `RestoreInitiatorService.cs`, `SyncTokenStore.cs`, the
  iPhone settings page). `SyncProtocolVersion.Current` keeps the value 3 so every file compiles; the call sites
  are switched file by file (list in 15.1). `EventLogger` stamps `Declared`.
- `SyncProtocolVersion.CanApply(v)` = `v is 1 or 2 or 3 or 4` on every 2.6 node, activated or not.
- `IsCompatiblePeer(declared, capabilities)`: before activation `declared >= 3`; after activation `declared >= 4`, or
  `declared == 3` with `metadata-envelope-v1` (a 2.6 node that has not applied the activation yet: it must be able to
  pull the activation from us; refusing it would deadlock the switch). Used at authenticate (`SyncEndpoints.cs:197`),
  by `SyncClient.cs:190` and by `/api/join`. A later regular release (no separate enforcement release) drops the window.
- `ShouldPullFrom(declared)` = `declared <= 4`. Replaces the "newer protocol, skip pull" test at `SyncClient.cs:201`.
- A 2.5 node: never pulls from a node declaring 4 (its `SyncClient.cs:201`), gets 426 at authenticate. Cut off
  both ways, as intended.

### 5.7 Protocol-1..3 content after activation ("stragglers")

A 2.6 node that authored a content event before it applied the activation wrote it in clear and signed it; it is
legitimate.

- Full nodes apply it as today (it is not held unless 6.4 says so) and request an early baseline (`late_protocol3`).
- Every full node and hub MUST NOT serve or push a protocol-1..3 content event to a blind peer
  (`BlindNodeId.IsBlind(peer)`) once it is activated: `GET /api/sync/events` (`SyncEndpoints.cs:211-269`) leaves such
  rows out and **fills the page with the rows the requester may have**: it reads the log on until `limit` of them
  are found or the log ends, so a page is empty only when no such row is left (a page cut first and filtered after
  could be empty while a sealed row waits further on, and a client that follows the last row it was given would ask
  for the same page forever). Nothing the requester needs is lost: the
  next baseline carries the change. When the log ended and the page holds every row the requester may have, the
  delivery position moves over the left-out rows too, as on the push side. The push loop (`SyncClient.cs:330-440`) leaves them out of chunks to
  a blind peer and moves its push cursor over them as if accepted.
- An activated blind host offered one answers `SilentlyDropped` (accepted, not stored, a log line with ids only).
- These rules apply to every protocol-1..3 content row, historic ones included: after activation a blind host
  receives content only sealed or inside a baseline.

### 5.8 Downgrade and rollback

There is no deactivation. A 2.5 binary started on an activated volume is unsupported (it cannot read sealed events
and its peers refuse it). Rollback after activation = restore the pre-activation backup and lose later changes;
the operator guide says so.

## 6. Full node: received and applied positions, held events, ordering

### 6.1 Columns (migration `038_event_apply_state.sql`)

```sql
ALTER TABLE tbl_event ADD COLUMN apply_state INTEGER NOT NULL DEFAULT 0;  -- 0 applied, 1 held, 2 relayed, 3 quarantined after receipt
ALTER TABLE tbl_event ADD COLUMN hold_reason TEXT;                        -- 'locked' | 'key' | 'order' while held
ALTER TABLE tbl_event ADD COLUMN held_at TEXT;
ALTER TABLE tbl_event ADD COLUMN origin_superadmin INTEGER;               -- receipt verdict (6.5), held rows only
ALTER TABLE tbl_event ADD COLUMN local_type TEXT;                         -- opened view's type (full nodes)
ALTER TABLE tbl_event ADD COLUMN local_article_id TEXT;                   -- opened view's article id (full nodes)
ALTER TABLE tbl_event ADD COLUMN origin_peer TEXT;                        -- the peer that delivered it (pull source or pusher)
ALTER TABLE tbl_event ADD COLUMN origin_seq INTEGER;                      -- its sequence number in that peer's log
CREATE INDEX IF NOT EXISTS ix_event_held ON tbl_event (apply_state, sequence_num) WHERE apply_state <> 0;
```

Existing rows are applied (0). Wire queries (`EventLogRepository.GetAfterSequenceAsync`, `GetEventsToRelayAsync`,
`GetAllAfterSequenceAsync`, `EventLogRepository.cs:71-126`) keep returning the original columns only; the activity
queries (`GetRecentAsync`, `GetByArticleAsync`, used by `ActivityEndpoints.cs:31-36` and `BeeAuditTools.cs:60-66`) read
`COALESCE(local_type, event_type)` and `COALESCE(local_article_id, article_id)`. `origin_peer`/`origin_seq` are
written for every received event (they let a phone trim by its server's positions, 13.3).

### 6.2 Outcomes

`EventApplyResult` gains `Held`: stored durably, not applied yet. Every caller treats `Held` as accepted: the pull
cursor moves over it (`SyncClient.cs:262-275`), the push answer counts it in `Applied` (old pushers then advance;
`SyncApplyResult` gets an extra informative `Held` count) and `LastAppliedSequence` covers it. A blind host's
relayed event is `Applied` (it is everything a blind host ever does with it).

### 6.3 The receipt and apply pipeline (`EventApplier`)

```
ApplyAsync(evt, context?):                                   (EventWriteGate as today, EventApplier.cs:48-57)
  1. CanApply(protocol) else NotSupportedException
  2. EventInvariants.Violation -> UnauthorizedAccessException
  3. row with this event id exists (any apply_state) -> Applied
  4. originator: context.Originators(evt.NodeId) when given (restore replay, section 12),
     else the whitelist lookup with today's unconfirmed/revoked/not-whitelisted exceptions (:88-116)
  5. Ed25519 signature (:118-120)
  6. shape: sealed -> SealedEnvelope.Parse + 3.2 rules; protocol-4 content type -> malformed
  7. outcome = IEnvelopeOpener.OpenAsync(evt, context):
       Plain(evt)   control event, or protocol-1..3 content on a host that applies it
       Opened(view) full node, key available
       Relay        blind host, sealed event      -> append(evt, state 2, origin) ; return Applied
       Hold(reason) full node, locked / key       -> append(evt, state 1, reason, verdict, origin) ; RequestDrain ; return Held
       Drop(reason) activated blind host, clear content -> log ids ; return SilentlyDropped
  8. a content view while any row of this log is held (apply_state 1)
                                                  -> append(evt, state 1, 'order', verdict, origin) ; return Held
  9. ApplyViewAsync(original = evt, view, verdict = originator's flags now)
```

```
ApplyViewAsync(original, view, verdict):
  view.EntityId = EventEntityId.Derive(view)   (+ today's transported-id warning for protocol <= 3, :135-145)
  hard-delete gate on view (:147-156); path check on view (:166); replay shield on original (:174-182)
  superadmin gate: RequiresSuperadmin(view.EventType) && !verdict.IsSuperadmin -> OriginatorNotSuperadminException
  actor columns as today (:126-127, :207); clock.Update(original.LamportTs)
  handler switch on view (:221-299)
  write the row: append (or, for a held row, update to state 0) with local_type, local_article_id, entity_id
  after a hard_delete view, or a protocol-1..3 content view on an activated node: IBaselineScheduler?.RequestEarly(...)
```

`ApplyHeldAsync(localSequence)` (drain entry, inside the same `EventWriteGate` as `ApplyAsync`): load the row, rebuild the event and the stored verdict, open it; still
not openable -> keep it held (update the reason) and stop; opens -> `ApplyViewAsync`; fails to open with the key it
has, or the inner type is not a content type, or a handler throws a permanent failure (classified by
`SyncFailureClassifier`) -> `apply_state = 3` plus a `tbl_sync_quarantine` row (visible in `GET /api/sync/quarantine`),
and continue; a deferrable failure (`BlobMissingException`, ...) -> keep held, stop, retry later.

A sealed event that fails to open at receipt on an unlocked node, with a key it holds, is refused at receipt
(`InvalidDataException` -> today's quarantine path; not stored, not relayed). The same failure in the drain leaves the
row stored (it was relayed already) in state 3. Both never apply it.

### 6.4 Hold rules and order

1. **Control events are never held.** They apply at receipt, locked or not, as today (whitelist, recovery material,
   anchors, `metadata_key_set`, `metadata_activation`, rotations with their own deferral, restores with theirs).
2. **Content events apply in local sequence order.** A content event is held when: it is sealed and the vault is
   locked (`locked`); it is sealed and its key is not openable now (`key`); or any earlier row of the log is held
   (`order`, protocol-1..3 content included).
3. A node that never receives a sealed event never holds anything: before activation 2.6 behaves as 2.5.1.

### 6.5 Receipt verdict

A held row stores `origin_superadmin` = the originator's `is_superadmin` at receipt (it was active at receipt, or the
event would not have been stored). When the drain applies it, the whitelist-active check is not repeated and the
superadmin gate uses the stored flag. Reason: an unlocked node applied X's
content before it applied X's later revocation; a locked node applies the revocation at once (rule 1) and must not
then reject X's earlier content at unlock, or the mesh diverges.

### 6.6 Positions

- **Received**, per peer: `tbl_sync_position` (pull) and, towards a pusher, `LastAppliedSequence` of the push answer
  (both now cover held events). `report-position` reports the received position, as today: the event is durably
  here, so the peer may compact past it.
- **Applied**, this node: `MIN(sequence_num of rows with apply_state = 1) - 1`, or the head when none. Not stored;
  computed by `IHeldEventStatus` (Appendix A.5).

### 6.7 The drain (`HeldEventDrainService`)

- Triggers: every unlock, chained **after** `RetryPendingAutoAcceptsAsync` in the same post-unlock task
  (`SessionService.cs:404-413`), so a rotation accepted at unlock makes its new MEK openable first; `RequestDrain()`
  from `metadata_key_set` and from new holds while unlocked; every 5 minutes while unlocked and held rows exist.
- Applies rows with `apply_state = 1` in `sequence_num` order, stops at the first that cannot apply, stops between
  rows when the vault locks; idempotent after a crash (apply, then mark: a crash in between re-applies, which every
  handler already tolerates, `EventApplier.cs:209-218`).
- **Restore barrier** (SHOULD): a `restore_network` row whose restore is still Pending/Downloading/Applying
  (`tbl_restore_event_state`) stops the drain at its sequence until that restore finishes, so content received after
  a restore is applied after it, as on an unlocked node.
- A `key` hold whose key id no `metadata_key_set` named within 30 days goes to state 3 ("unknown metadata key").

### 6.8 Refused while events are held

`IHeldEventStatus.AnyHeldAsync()` true -> refuse, with a message that names the cause and says "unlock and wait":
compaction (`CompactionService`), baseline production (7.5), join snapshot (`/api/sync/snapshot/for-join`), network
restore initiation, DEK rotation proposal, activation (5.3). A DEK rotation proposal or accept is also refused (409)
while the previous rotation's new metadata key is unpublished (4.4). Each refusal has a test.

### 6.9 Serving while locked; status

- `GET /api/sync/events` serves held rows like any other (original bytes); the locked node is a relay.
- A locked node still cannot pull or push (outbound authentication needs the DEK for a v=1 identity,
  `libs/BeeMemoryBank.Vault/Sync/SessionNodeAuthSigner.cs`). Unchanged.
- `GET /api/sync/status` and `bmb status` gain `held: { count, locked, key, order, oldest_at, applied_through_seq,
  head_seq, quarantined_after_receipt }`; the Web shows "N changes wait for unlock". Warning when held > 100,000 or
  oldest > 7 days.

## 7. Chunked container and the baseline

### 7.1 Container `BMBCH1` (Vault)

Used for baselines (root key = MEK) and the encrypted join snapshot (root key = master DEK). Streaming, bounded
memory (one chunk). No 2 GiB ceiling (the guard of `SnapshotService.Crypto.cs:25, 80-88` stays for BMBDB2 only); the
format's own hard ceiling is 2^32 chunks per file (16 PiB at 4 MiB chunks), enforced by writer and reader (below).

```
0      8 bytes   magic "BMBCH1\0\0"
8      uint32 LE H = header length (1..4096)
12     H bytes   header, UTF-8 JSON:
                 {"v":1,"purpose":"baseline"|"join","key":"mek:<key id>"|"dek:<dek fingerprint>",
                  "salt":"<base64 16 bytes>","nonce_prefix":"<base64 7 bytes>","chunk":4194304}
12+H   chunks i = 0..N-1 (N >= 1): uint32 LE length L_i (16 <= L_i <= chunk + 16), then L_i bytes = ciphertext || tag
```

- Key: `K = HKDF-SHA256(ikm = root, salt = header.salt, info = "bmb-chunked-v1:" + purpose, L = 32)`.
- Nonce of chunk i: `nonce_prefix (7) || uint32 BE i (4) || last (1 byte: 0x01 for the final chunk, else 0x00)`.
  The index is a 64-bit count computed by one function, `NonceFor(byte[] prefix, long index, bool last)`, which throws
  `InvalidDataException` for `index < 0` or `index > 4294967295` and otherwise writes the low 32 bits; nothing else
  builds a nonce. The writer calls it before sealing each chunk, so it refuses an input of more than 2^32 chunks; the
  reader calls it before opening each chunk, so it refuses a file with more. The index is never truncated or wrapped:
  within one file no two chunks share a nonce, and every file has its own key (random 16-byte salt), so no nonce
  repeats under a key. `chunk` in the header is between 4,096 and 16,777,216.
- AAD of every chunk: `SHA-256(bytes 0 .. 12+H-1)` (magic, length and header).
- Plaintext chunks are exactly `chunk` bytes except the last (0..chunk; an empty input is one empty final chunk).
- Reader: header `v` = 1, `purpose` = the expected one, key resolvable; reads chunks in order; the chunk followed by
  end of file is decrypted with `last = 1`, every other with `last = 0`. Truncation at a chunk boundary, reordering,
  substitution, a changed header, a removed final flag, trailing bytes and a wrong key all fail (`InvalidDataException`).
- `container_sha256` in manifests = SHA-256 of the whole file.

### 7.2 What a baseline contains

The plaintext is a SQLite file made like a peer snapshot (`SnapshotService.CreateAsync`,
`server/BeeMemoryBank.Api/Services/SnapshotService.Create.cs:84-102`): `VACUUM INTO` a staging file in the owner-only
staging folder (`SnapshotStaging`), then filter with today's allow-list semantics (`SnapshotTables`,
`libs/BeeMemoryBank.Storage/Sqlite/SnapshotTables.cs:27-67`; `FilterSecretsFrom` with `KeepTables =
["tbl_hard_delete_audit"]`), and on top:

- `tbl_blob` emptied (schema kept): blobs travel and are stored separately, listed in `blobs.txt` (7.6);
- `tbl_article.embedding_projection`, `tbl_article.embedding_model_version`, `tbl_concept_tag.embedding` and
  `tbl_concept_tag.embedding_model_version` set to NULL (derived; a restored node recomputes them);
- every change before the final compaction (`SnapshotService.Create.cs:94-98`), then sealed into BMBCH1 with
  `purpose = "baseline"`, `key = "mek:<IMetadataKeyRing.Current>"`; the plaintext staging file and its journal are
  deleted in a `finally`.

### 7.3 Building

1. Preconditions: activated; unlocked; `IMetadataKeyRing.Current` not null; no held events (6.8); free space >= 2x
   the database size; this node holds every blob the filtered rows reference (else refuse).
2. Under `EventWriteGate.Instance.EnterAsync()` (no apply runs, as the reseed does): read the log head
   (`producer_log_head`), the Lamport clock (`lamport_at_cut`), all pull positions (`pull_positions`), the schema
   version, then `VACUUM INTO`. Release the gate; the rest runs outside it.
3. Filter, null, compact (7.2); collect the blob list (7.6); encrypt (7.1).
4. **Self-check** (MUST): decrypt the container to a second staging file, `PRAGMA integrity_check` = `ok`, row counts
   of `tbl_folder`, `tbl_article`, `tbl_media` equal the filtered copy; delete it.
5. Write the manifest (7.4), sign it, store the four files under `<data>/baselines/produced/<baseline_id>/`
   (owner-only). Keep the two newest produced baselines; delete older ones (product cache, as the replica cache does).

A full node keeps no table for what it produced: the directory is the record.

### 7.4 Baseline manifest (Appendix A.7)

`manifest.json` (UTF-8 JSON, written once, its exact bytes are signed):

```json
{ "format": "bmb-baseline-v1", "baseline_id": "<guid>", "producer_node_id": "<guid>", "created_at": "...",
  "producer_log_head": 1042, "lamport_at_cut": 2089,
  "pull_positions": [ { "remote_node_id": "<guid>", "last_sequence": 77 } ],
  "key_id": "<hex32>", "key_generation": 2, "schema_version": 39,
  "container_sha256": "<hex>", "container_bytes": 123456,
  "blob_list_sha256": "<hex>", "blob_count": 812, "blob_bytes": 50331648 }
```

`manifest.json.sig` = 64 raw bytes, Ed25519 by the producer's identity key over
`"BMB-BASELINE-V1\0" || manifest bytes` (domain separated from every snapshot signature). No titles, no counts of
notes or folders, no coverage per author: what a blind host needs and nothing else. `pull_positions` names the
producer's peers and how far it pulled each (topology, accepted residual).

### 7.5 Who produces, and when (`IBaselineScheduler`)

- Only an activated, unlocked full node with no held events; its authority is checked by every receiver (9.2).
- Triggers: `RequestEarly(reason)` (activation, hard delete applied or authored, protocol-3 straggler applied, key
  rotation, blind reseed request, on demand from Admin) with a 10-minute debounce; the newest produced baseline
  older than 24 hours while the log grew since; a blind node it manages asks for a reseed or reports a baseline
  older than this node's newest (8.7 push).
- A locked hub serves the newest baseline it produced while unlocked; it cannot produce or sign a new one (as today a
  locked hub cannot build a new replica package).

### 7.6 Blob list

`blobs.txt`: the distinct hashes referenced by the filtered copy's `tbl_article_body.ciphertext_hash`,
`tbl_article_version.ciphertext_hash` and `tbl_media.ciphertext_sha256` (the references the blob GC counts,
`BlobRepository.cs:139-150`), lowercase hex, sorted ordinally, one per line, each line ending in `\n`.
`blob_list_sha256` = SHA-256 of the file; `blob_bytes` = sum of their sizes.

## 8. Package format 2

### 8.1 Kinds

| Kind | Contents | Used for |
|---|---|---|
| `full` | baseline + control + deltas + blobs | first seed of a Docker blind node, phone first load, restore package, phone backups |
| `baseline` | baseline only (and `blind-manifest.json`) | reseed of an operational blind node (pushed by a full node), newer baseline for a phone (`?have=`) |

### 8.2 Entries (tar.gz, PAX entries, as today's packages)

```
manifest.json                   outer manifest: today's snapshot manifest shape (every entry with SHA-256),
                                "snapshotFormatVersion": 5, "packageFormat": "bmb-blind-package-v2", "producerNodeId" = serving node
manifest.json.sig               embedded signature of the serving node (today's embedded tag)
blind-manifest.json             BlindManifestV2 (8.3)
baseline/manifest.json          7.4
baseline/manifest.json.sig      7.4
baseline/blobs.txt              7.6
baseline/baseline.bmbch         7.1
control.db                      full only: 8.4
deltas.jsonl                    full only: 8.5
blobs/<hash>                    full only, when contains_blobs: every hash of blobs.txt and of the deltas' "b" lists
```

No `beememorybank.db` entry. The detached signature over `manifest || archive` travels in the response header as
today (`X-BMB-Snapshot-Signature`). A 2.5 reader refuses the package by its format string (`BlindManifest.Parse`,
`libs/BeeMemoryBank.Sync/Blind/BlindManifest.cs:41-48`) or by the missing database entry.

### 8.3 `blind-manifest.json`, format `bmb-blind-package-v2` (Appendix A.8)

Fields of v1 (`BlindManifest.cs:23-32`: seed id, `cp_sequence` = serving node's log head at assembly,
`includes_up_to`, `created_at`, `whitelist` with superadmin flags, pins and trust, `positions` = the serving node's
pull positions, `standing`), with `producer_node_id` meaning the **serving** node, plus: `kind`,
`baseline_id`, `cut_in_server_log` (the serving node's own log position the baseline covers: the producer's
`producer_log_head` when it serves its own baseline; `pull_positions[self]` of the baseline when a blind node
serves one it adopted), `delta_count`, `contains_blobs`, `activation` (`{activation_id, key_id}` or null).

### 8.4 `control.db`

A plain SQLite file holding only the tables of the new list `SnapshotTables.Control` = `tbl_metadata_key`,
`tbl_metadata_activation`, `tbl_recovery_box`, `tbl_dek_retired_link`, `tbl_state_anchor`, `tbl_sealed_secret`, copied
row for row from the serving node (same column names; constraints need not be reproduced). Nothing else. The
receiver imports with today's versioned rules (insert-or-ignore by key; recovery rows by `lamport_ts`, as
`ImportTables` does). A test asserts the file has exactly these tables.

The receiver does not take the rows as they come. Before anything is adopted (8.6 step 6) `ControlDbValidator` holds every row of every
table against the rules the live appliers apply to the same data (`EventApplier.Metadata.cs`, `EventApplier.Recovery.cs`), and refuses the
package if one fails: all six objects are actual tables, with the columns the appliers write; each column has the storage class the applier gives
it (SQLite is dynamically typed: text in a blob column is refused); a key id is 32 lowercase hex characters, a generation at least 1, a
fingerprint 64 lowercase hex characters, a key's material 48 bytes and its IV 12; a box has the kind `strong` or `device`, a preset its kind
may have, status `A` or `R`, and, while active, material of the shape `RecoveryBoxKdf.IsWellFormed` accepts (a retired box has nothing to judge: the
appliers clear it); a link has two different fingerprints and link material; an anchor a digest of the `sd<n>:` shape, a 64-hex HMAC and a
position vector that maps node ids to non-negative positions; a secret one of the two names the applier accepts and, while active, a value of
17 to 4096 bytes with a 12-byte IV; no primary key repeats (the copy carries no constraints); there is at most one activation, and it names
a key of the package with that key's generation; every Lamport time lies in `[0, 2^53-2]` (`BlindManifestV2.MaximumLamport`). Restore's `2^53`
ceiling is exclusive, and a first-seed grant is one above the package maximum, so the grant is at most `2^53-1` and remains restoreable. A
refusal names the table, the row's position and the column, never a value: the rows
hold wrapped keys. The tests apply real events to a node and validate the rows they wrote, so the copied rules cannot drift unnoticed.

### 8.5 `deltas.jsonl`

One `SyncEvent` per line (wire JSON, 3.2), in the serving node's log order, every row with
`sequence_num > cut_in_server_log`, `sequenceNum` = the serving node's sequence; protocol-1..3 content rows left out
(5.7). The receiver stores them through `EventApplier` in relay mode (signatures checked against its whitelist; a row
from an unknown originator is deferred as in ordinary sync and the install fails if it cannot be stored).
Before any row is stored, the receiver checks the whole stream and rejects the package if a line is blank or is not an event, an
event id repeats, a row's Lamport time is outside `[0, 2^53-2]`, a row's `sequenceNum` is not greater than the row before it, is not after
`cut_in_server_log` or is after
`cp_sequence`, or a protocol-1..3 content row is present (`Format2Package.ReadVerifiedDeltasAsync`): the format's invariants are
checked by the receiver, not trusted from the sender.

### 8.6 Verification order (every receiver, MUST)

1. Outer: the embedded signature by the serving node's key that the receiver already trusts (pair code, call code,
   whitelist row, restore code fingerprint: as today), every entry's hash, the detached signature.
2. `blind-manifest.json`: format, kind, the serving node named with the signing key, whitelist rows unique. Signed does not mean usable:
   `BlindManifestV2.Parse` also refuses, before anything is adopted, a manifest without its `whitelist` or `positions`, a row or position
   without a node id or with a negative sequence or a Lamport time that is negative or past `2^53-2`, a node or position listed twice, a peer key that is not 32 bytes of base64,
   a peer without a display name, an activation without a valid id and key id, and a standing event that is incomplete, has a Lamport time
   that is negative or past `2^53-2`, or is a `whitelist_add` whose payload cannot be read. `BaselineManifest.Parse` refuses a manifest without
   its pull positions, key id or hashes, or with a negative position, size or count, or a `lamport_at_cut` outside `[0, 2^53-2]` (7.4).
3. Baseline: `producer_node_id` is in the receiver's trusted whitelist (its own; for a first install, the package's)
   as active and **superadmin**; the signature verifies with that row's key; container size and SHA-256 and
   `blobs.txt` SHA-256 match; `key_generation >= 1`; the container is a `BMBCH1` frame for `purpose = "baseline"` and
   `key = "mek:<key_id of the manifest>"` (magic, header and chunk lengths that tile the file exactly, checked without a key by
   `BaselineContainerFraming`; a database, ZIP or JSON file is refused whatever its hash).
4. Blobs: each `blobs/<hash>` hashes to its name (`BlobHash.Matches`).
5. Deltas: the stream checked as in 8.5, then each stored through the applier (signatures).
6. `control.db`: the table names (8.4) and then every row (`ControlDbValidator`, 8.4). Like steps 1-5 this is decided before anything is
   adopted: the import after the adoption takes the rows as they are.

### 8.7 Routes (no new blind route; `docs/blind-node/ROUTES.golden.txt` unchanged)

| Route | Change |
|---|---|
| `POST /api/blind/seed` (+ `GET /api/blind/seed/{id}`) | carries a format-2 package (`full` or `baseline`); authority as today (pair-code proof or superadmin token, `BlindEndpoints.cs:141-158`). Before the upload of a `baseline` package the full node ships missing blobs through `/api/sync/blobs/check` and `/api/sync/blobs` (as `BlobTransport.ShipForAsync`). Order on the node: a trial import of the package's `control.db`, rolled back (a control database this node's schema cannot take is refused here), for a first seed the grant time computed ahead (the same `GrantTime` over what the node will hold once the purge has run - its rows in the operational tables and the log without its protocol-1..3 content rows - the package's peers and control rows, and its standing events: a seed that leaves no time to grant is refused before anything is adopted), blobs of a `full` package, adoption (every refusal of 9.2 is decided here), then the whitelist rows and control tables, the deltas, and last the serving node's positions, so a refused or failed package leaves nothing behind but surplus blobs and a replay that failed cannot make a later pull skip rows. |
| `GET /api/blind/replica` | after activation serves format 2: `full` without `have`; with `?have=<baseline id>`: 304 when the serving node's baseline is that one, else a `baseline` package. The caller then fetches missing blobs with `/api/sync/blobs/get`. Cache, Range and admission limit as today (`BlindEndpoints.cs:85-132`). |
| `GET /api/blind/restore/package` | format 2 `full` package with blobs. |
| `GET /api/blind/restore/events` | unchanged shape; the blind node's log (deltas and control events). |

Before activation all of these behave exactly as 2.5.1 (format 1).

## 9. Blind store: operational database, adoption, deltas, trimming, blob clean-up

### 9.1 Operational database

Same migrations as every node (the schema is shared). New migration `039_blind_baseline_store.sql`:

```sql
CREATE TABLE IF NOT EXISTS tbl_baseline (
    baseline_id       TEXT    NOT NULL PRIMARY KEY,
    producer_node_id  TEXT    NOT NULL,
    created_at        TEXT    NOT NULL,
    adopted_at        TEXT    NOT NULL,
    lamport_at_cut    INTEGER NOT NULL,
    local_cut         INTEGER NOT NULL,     -- position in THIS node's log the baseline covers
    key_id            TEXT    NOT NULL,
    key_generation    INTEGER NOT NULL,
    container_sha256  TEXT    NOT NULL,
    container_bytes   INTEGER NOT NULL,
    blob_count        INTEGER NOT NULL,
    blob_bytes        INTEGER NOT NULL,
    manifest          BLOB    NOT NULL,     -- the signed bytes
    signature         BLOB    NOT NULL,
    is_current        INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_baseline_current ON tbl_baseline (is_current) WHERE is_current = 1;
CREATE TABLE IF NOT EXISTS tbl_baseline_blob (
    baseline_id TEXT NOT NULL,
    hash        TEXT NOT NULL,
    PRIMARY KEY (baseline_id, hash)
);
CREATE INDEX IF NOT EXISTS ix_baseline_blob_hash ON tbl_baseline_blob (hash);
```

Files: `<data>/baselines/current/` holds `manifest.json`, `manifest.json.sig`, `blobs.txt`, `baseline.bmbch` of the
current baseline (owner-only). `RekeyTables`: both tables `Cleared`.

**Tables that may hold rows on an activated blind host** (golden `docs/blind-node/OPERATIONAL-TABLES.golden.txt`):
`tbl_migration`, `tbl_migration_marker`, `sqlite_sequence`, `tbl_node_identity`, `tbl_whitelist`,
`tbl_sync_position`, `tbl_sync_push_position`, `tbl_sync_quarantine`, `tbl_compaction_log`, `tbl_event` (sealed and
control rows only), `tbl_blob`, `tbl_blind_state`, `tbl_blind_restore_code`, `tbl_recovery_box`,
`tbl_recovery_box_check`, `tbl_recovery_box_pending_retire`, `tbl_dek_retired_link`, `tbl_state_anchor`,
`tbl_sealed_secret`, `tbl_metadata_key`, `tbl_metadata_activation`, `tbl_baseline`, `tbl_baseline_blob`,
`tbl_restore_event_state`, `tbl_restore_replay_shield`, `tbl_dek_rotation_state`, `tbl_audit_log` (the blind node's own
console and claim entries) and the blind-only tables of migrations 027-031 (the golden completes the list from the
schema). **Every other table is empty**, in particular `tbl_folder`, `tbl_article`, `tbl_article_body`,
`tbl_article_version`, `tbl_conflict_version`, `tbl_concept_tag`, `tbl_article_concept_tag`, `tbl_concept_tag_edge`,
`tbl_media`, `tbl_tombstone`, `tbl_comment`, `tbl_projection_matrix`, `tbl_hard_delete_audit` and the FTS tables.
A test drives a blind host through activation and a format-2 adoption and asserts it. The tables exist (the
migrations are shared and later migrations alter them); they hold nothing (blind copies hold no structure).

### 9.2 Adoption (`IBaselineStore.AdoptAsync`, Appendix A.8)

Given verified files (8.6) and the local cut:

1. Producer active and superadmin in **this node's** whitelist (first seed: the pair-code-vouched seeder, as
   `BlindSeedService.CheckProducerAsync` decides today, `server/BeeMemoryBank.Api/Services/Blind/BlindSeedService.cs:272-308`).
2. Monotonic: `lamport_at_cut >= current.lamport_at_cut` and `key_generation >= current.key_generation`; a
   `local_cut` below the current one is refused (a producer cannot roll a blind node back).
3. Every hash in `blobs.txt` present in `tbl_blob` (the sender ships missing ones first; a phone fetches them).
4. Copy the files to `baselines/current/` through a temporary sibling and an atomic switch; write the row with
   `is_current = 1` and its blob list, clear the previous row and its blob list, delete the previous files.
5. First adoption on this host (no earlier row): purge legacy content (9.3).
6. Trim (9.5) and collect blobs (9.6).

`GetCurrentAsync` returns the current baseline only while the four files in `baselines/current/` are the signed ones: manifest and
signature equal to the row, `blobs.txt` and the container equal to the manifest's hashes and size, and the container a `BMBCH1` frame
(the container hash is cached per size and modification time for an hour). Otherwise it returns nothing (the node serves 503 and
trims nothing) until the same baseline is adopted again, which restores the files.

Local cut: a Docker blind node = `pull_positions[self]` of the baseline manifest (0 when absent: nothing trimmed);
a phone or desktop blind copy = `cut_in_server_log` of the package from the server it pulls from (13.3).

### 9.3 First adoption: purge legacy content

With `PRAGMA secure_delete = ON`: delete every row of every table not on the operational list (9.1); delete every
protocol-1..3 content row of `tbl_event`; then `VACUUM` and truncate the WAL (a truncation that cannot finish is a failed purge). Only then set `tbl_blind_state` key
`opaque_store = 1`; until it is set the purge is unfinished and is done again by the next adoption (also of the baseline that is
already current) and by `BlindLogTrimmer` at start-up and on every run. The trimmer's purge needs only that a baseline row exists
(`BaselineStore.HasAdoptedAsync`), not that the baseline's files can be served now and not that the activation was imported: a damaged or
half-switched `current/` must not keep readable rows on the disk; before any baseline was adopted the legacy rows are the only copy and the
trimmer leaves them (the log trim, unlike the purge, still needs a servable baseline, 9.5). A test scans the
database file, WAL and journal for the canaries afterwards (16.3). The database file the 2.5 reseed path kept aside
earlier (`BlindSeedService` keeps the previous database) and older restic snapshots are not touched: they are
reported to the owner (status field `legacy_copies`, docs) for retirement.

### 9.4 Delta store (relay mode)

`RelayEnvelopeOpener` (Appendix A.2) on every blind host:
`sealed` -> `Relay`; clear content -> `Drop` when activated, `Plain` before (materialised as 2.5.1); control -> `Plain`.
So a blind host stores every sealed event with `apply_state = 2` and applies control events as today. Blind hosts
are three compositions: the BlindNode host and the full Api started with `BMB_ROLE=blind` (the one the
`Integration.Tests` blind factory runs) share `BlindRoleServices.AddBlindRoleServices`
(`server/BeeMemoryBank.Api/Startup/BlindRoleServices.cs:25`, called at `ApiServices.cs:268` and
`BlindNodeServices.cs:78`); AppCore's `BlindMobileServices` is the third. All three call
`MetadataHostServices.AddBlindMetadataHost(dataPath)`, which replaces the Vault opener that `AddSync` adds
with `TryAdd` by the relay opener and then calls hook H1, where the blind store is registered (15.2). A composition test
per host asserts which opener it resolves. A sealed hard
delete is relayed and changes nothing on the blind host. A blind host never computes the state
digest after activation (10.3).

### 9.5 Trimming (`BlindLogTrimmer`, `server/BeeMemoryBank.Api/Services/Blind/BlindLogTrimmer.cs:35-62`)

After activation: `cutoff = min(readByAll, anchorCoversUpTo, current.local_cut)`; activated with no current baseline:
no trimming at all (the deltas are the only copy until a baseline covers them). Before activation: as today. A trim
writes `tbl_compaction_log` as today, so a peer below the cut gets 410. Adoption runs one trim.

### 9.6 Blob clean-up on blind hosts

Keep a blob when it is in the current baseline's list, or in `$.b` of a stored sealed row, or in `$.ciphertext_sha256`
of a stored protocol-1..3 row, or younger than 24 hours; delete the others. One statement in
`BlobRepository.CollectGarbageAsync` serves full and blind hosts (3.10); on a blind host the content-table references
are empty after the purge.

### 9.7 Serving from a blind node

`IBaselineSource` (Appendix A.8) on a blind host = the current adopted baseline with `local_cut`. Replica, restore
package and restore events (8.7) are assembled by the format-2 writer from it, the control tables, the deltas after
`local_cut` and the blobs, and signed with the node's external key (no unlock needed).

An activated node with nothing to serve (a blind node not yet reseeded in format 2, a hub that never produced a
baseline and is locked) answers the replica and restore-package routes with 503 and `Retry-After`, never with a
format-1 package; the restore client says "the blind node has no sealed baseline yet".

### 9.8 Docker blind backups (`BlindBackupService`)

After the first format-2 adoption the operational database holds no metadata, so today's `VACUUM INTO` staging copy
plus restic stays; add `baselines/current/` to the restic paths. Restore from such a backup = section 12 with the
backup folder as the source. Between activation and the first adoption backups contain what they contained before
(plaintext); the status says so.

## 10. Blind status, alarms and recovery status without counts

### 10.1 Docker blind node (`CoreBlindStatusContributor`, `server/BeeMemoryBank.Api/Services/BlindStatus/CoreBlindStatusContributor.cs:35-114`)

`stored` loses `articles` (from 2.6.0, activated or not) and keeps `blobs` and `bytes`; new keys:

```json
"activated": true,
"baseline": { "id": "...", "producer": "...", "created_at": "...", "age_seconds": 3600, "key_generation": 2,
              "container_bytes": 123456 } | null,
"deltas": { "count": 812, "bytes": 1048576 },
"legacy_copies": [ "data/beememorybank.db.pre-reseed-...", ... ]
```

The console page and `bmb blind status` show baseline age, delta count and storage size, never a note count.

### 10.2 Phones and desktop blind copies

`BlindReplicaStats.CountNotesAsync` (`libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindReplicaStats.cs`) is
replaced by `BlindStoreStats` (baseline created at, delta count, stored bytes); Android, Windows, macOS and iPhone
home screens show those three.

### 10.3 Recovery status on a blind host (`RecoveryStatusService.cs:69-84`)

Activated: report the newest anchor (id, time, author) with `matches: null` and the text "checked by a restoring full
node"; no digest is computed (the content tables are empty).

### 10.4 Alarms

Stale baseline: a blind node whose current baseline is older than 7 days while it holds deltas. Shown on the blind
console and on the full node's Blind nodes page from `my-standing.store` (`BlindAlarmService`).
Existing contact, protocol, backup and space alarms unchanged.

## 11. Join after activation

1. `/api/join` (`server/BeeMemoryBank.Api/Endpoints/JoinEndpoints.cs:21-230`) already requires an unlocked producer
   (`:73-78`). After activation it also requires `IsCompatiblePeer(req.ProtocolVersion, req.Capabilities)`, else 426
   "update this device to 2.6 or newer"; a 2.5 joiner sends neither and is refused before any row is written.
2. `GET /api/sync/snapshot/for-join` (`SyncEndpoints.cs:272-361`): after activation the database entry of the join
   archive is a BMBCH1 container, `purpose = "join"`, root key = the master DEK, `key = "dek:<fingerprint>"`;
   `manifest.json` says `"dbEncrypted": true, "dbCipher": "bmbch1"`. Refused while events are held (6.8; inside
   the for-join lambda, 15.3). The join
   cache (`SnapshotJoinCache`) keys on the DEK fingerprint and is dropped at activation and DEK rotation. A blind
   requester gets 403 (release 2.5.2).
3. Joiners decrypt with the DEK they just received and only then import: `SnapshotJoinClient`
   (`libs/BeeMemoryBank.Vault/Sync/SnapshotJoinClient.cs:145-240`, used by `bmb join`, Android and iPhone full apps) and
   `SnapshotService.RestoreForJoinAsync` (Setup page, `InitEndpoints.cs:491-518`). A 2.6 joiner that reads protocol 4 in
   the producer's identity refuses a plaintext join archive (fail closed).
4. The imported tables include `tbl_metadata_key` and `tbl_metadata_activation` (Replicated), so the joiner starts
   activated with the MEK rows; it opens them after its first unlock.
5. Before activation: exactly 2.5.1 (plain archive), for every joiner.

## 12. Restore from a baseline and deltas

Sources (`IRestoreSourceV2`): a format-2 `full` package from a blind node (restore code routes, 8.7), an
Android blind backup file holding one, a Docker blind node's backup folder (operational database + `baselines/current/`).
Steps of `RecoveryRestoreService` (`server/BeeMemoryBank.Api/Services/Recovery/RecoveryRestoreService.cs:99-480`)
become:

1. Verify the package (8.6) against the producer key the restore code names (as `:111-148`).
2. Keys from `control.db`'s recovery set and the password (`ResolveKeysAsync`, unchanged); nothing written yet.
3. Open every MEK row of `control.db` with the DEK of its fingerprint (`MetadataKeyCrypto.TryUnwrap`); refuse when the
   baseline's key cannot be opened.
4. Decrypt the baseline into the owner-only staging folder; import its tables (`SnapshotTables.Replicated` + the
   hard-delete audit) and `control.db`'s tables; import the blobs into `tbl_blob`.
5. Bootstrap identity, slot, admin, retired keys (today's step 3, `:264-436`).
6. **Replay** `deltas.jsonl` in order through `EventApplier.ApplyAsync(evt, context)` with
   `context.Originators` = the package whitelist's keys and superadmin flags and `context.EventKeys` = the event keys of
   the opened MEKs (Appendix A.2); progress kept in `tbl_migration_marker` key `restore_replay_seq` so a crash resumes
   (replaying an event already reflected is a no-op: the reseed relies on the same today, `BlindSeedService.cs:572-600`).
7. **Anchor check after the replay** (`StateAnchorService.VerifyAsync`, as `:440-444`) with the deltas and standing as
   evidence: a blind node that withheld deltas shows as a mismatch; peers' statuses follow the anchor as today (`:446-453`).
8. Positions: the baseline's `pull_positions`, and the serving blind node at the package's `cp_sequence`.
9. Lower the marker (today's step 8).

`MaxEvents` of `/api/blind/restore/events` (`BlindRestoreEndpoints.cs:31`) stays; deltas over it make the restore say
"incomplete, sync with the blind node to finish" rather than silently stop.

## 13. Phones and desktop blind copies

### 13.1 Install of a `full` package

`BlindPhoneReplicaClient` (`libs/BeeMemoryBank.Blind.PhoneClient/Sync/Blind/BlindPhoneReplicaClient.cs`, 724 lines)
keeps download, Range resume, back-off and verification, and replaces "build and switch a database" by: verify (8.6),
import whitelist/positions/control rows, import blobs, `IBaselineStore.AdoptAsync` (local cut = `cut_in_server_log`),
store the deltas through the relay applier with `origin_peer` = server and `origin_seq` = their `sequenceNum`, set the
pull position on the server to `cp_sequence`, purge legacy content on the first one (9.3).

### 13.2 Pull client

`BlindPhonePullClient` unchanged except: relay applier, `ApplyContext(ReceivedFrom, ReceivedSeq)`, capabilities in
`report-position`.

### 13.3 Newer baselines and trimming

The heavy pass asks `GET /api/blind/replica?have=<current baseline id>`; on a `baseline` package: fetch missing blobs
(`/api/sync/blobs/get`), adopt with `local_cut` = `cut_in_server_log`, then delete local `tbl_event` rows with
`origin_peer` = that server and `origin_seq <= cut_in_server_log`, then blob clean-up. A phone has no readers of its
log, so nothing else bounds its trim.

### 13.4 Backups and the activation

Backups keep fetching a fresh `full` package from the server (`BlindPhonePackageSource`, as today) and seal it as
today. The activation observer sets `InitialLoadDone = false` (5.5). Screens per 10.2. The Windows, macOS and iPhone
heads change only their status text (they run AppCore).

## 14. Interfaces between the parts

The exact source of every contract file is in Appendix A. A contract file is created **byte for byte** by whichever
part needs it first (two identical additions combine cleanly); nobody changes a contract file on their own. A
change to a contract is made in this document first, and every part follows it.

| Contract (Appendix) | Namespace / file | Implemented by | Used by |
|---|---|---|---|
| A.1 `EventClasses` | `libs/BeeMemoryBank.Sync/Metadata/EventClasses.cs` | (constants) | 1, 2, 3 |
| A.2 `IEnvelopeOpener`, `EnvelopeOutcome`, `ApplyContext`, `OriginatorInfo`, `HoldReasons` | `libs/BeeMemoryBank.Sync/Metadata/IEnvelopeOpener.cs` | 1 (Vault opener, relay opener) | 1 (applier), 3 (restore) |
| A.3 `ISyncProtocolPolicy`, `MetadataCapabilities` | `libs/BeeMemoryBank.Sync/Metadata/ISyncProtocolPolicy.cs` | 1 | 1, 2, 3 |
| A.4 `IMetadataActivationObserver`, `MetadataActivation` | `libs/BeeMemoryBank.Sync/Metadata/IMetadataActivationObserver.cs` | 2 (blind node), 3 (full, phones) | 1 (applier, activation) |
| A.5 `IHeldEventStatus`, `IHeldEventDrain`, `HeldEventSummary` | `libs/BeeMemoryBank.Sync/Metadata/IHeldEventStatus.cs` | 1 | 1, 3 |
| A.6 `IBaselineScheduler`, `BaselineReasons`, `IBlindStoreSummarySource`, `BlindStoreSummary` | `libs/BeeMemoryBank.Sync/Metadata/IBaselineScheduler.cs` | scheduler 3; summary 2 | 1 (applier, my-standing), 2, 3 |
| A.7 `BaselineManifest` | `libs/BeeMemoryBank.Sync/Blind/Format2/BaselineManifest.cs` | (record) | 2, 3 |
| A.8 `BlindManifestV2`, `IBlindPackageV2Writer`, `IBlindPackageV2Reader`, `IBaselineStore`, `IBaselineSource` and records | `libs/BeeMemoryBank.Sync/Blind/Format2/Format2Contracts.cs` | 2 (writer, reader, store, blind source); 3 (full-node source) | 2, 3 |
| A.9 `IMetadataKeyRing`, `MetadataKeyInfo`, `MetadataKeyRow`, `MetadataKeyUnavailableException` | `libs/BeeMemoryBank.Vault/Metadata/IMetadataKeyRing.cs` | 1 | 1, 3 |
| `MetadataKeyCrypto` (part-1 API, signatures under A.9) | `libs/BeeMemoryBank.Vault/Metadata/MetadataKeyCrypto.cs` | 1 | 1, 3 (restore) |
| `EventApplier.ApplyAsync(SyncEvent, ApplyContext?)`, `ApplyHeldAsync(long, CancellationToken)`, `EventApplyResult.Held` | `EventApplier.cs` | 1 | 1, 2, 3 |
| Composition hooks H1-H5 (declared by part 1, one implementing file each) | 15.2 | 2 (H1-H3), 3 (H4, H5) | 1 |

Contract files hold interfaces, records, enums and constants only (A.7 and A.8 also hold their own small
parse/serialise helpers); every implementation is in the files of the part that owns it. The three migrations are contracts too:
their SQL in 4.1, 6.1 and 9.1 is exact, so a part whose tests need another part's tables before the parts are combined
adds the same `.sql` file byte for byte (`*.sql` is LF by `.gitattributes`); `docs/migrations.golden.txt` is regenerated
once the parts are combined (15, rule 2). To make a node "activated" in a test before part 1's activation service exists,
insert one `tbl_metadata_key` row and one `tbl_metadata_activation` row and restart the host (or call
`ISyncProtocolPolicy.MarkActivated()`). Where a part needs another part's
implementation before the parts are combined (part 3: the opener and `MetadataKeyCrypto` for the restore replay, the
format-2 reader, writer and store for phones and restore), it works against a test fake and leaves the wiring for the
combined build.

## 15. File ownership and composition rules

Every existing source file that Step A changes belongs to **exactly one part**; no other part edits it. Where two
parts must put code into one composition, the owning part declares a C# partial-method hook (15.2) and the other part
implements it in a new file of its own. One file stays shared by necessity, with a fixed boundary (15.3).

### 15.1 Owners

| Area | Part 1 (protocol, keys, envelope, full-node pipeline, activation) | Part 2 (blind store, format 2, Docker blind) | Part 3 (baselines, join, restore, phones) |
|---|---|---|---|
| Sync | `EventApplier*.cs`, `EventPayloads.cs`, `SyncProtocolVersion.cs`, `SyncClient.cs`, `PeerAuthenticator.cs`, `BlobReferences.cs`, `EventInvariants.cs` (if needed), `NodeDependencyInjection.cs`, `Blind/MyStanding.cs`, `Metadata/*` (new, including `MetadataHostServices.cs`) | `Blind/Format2/*` (new), `Blind/BaselineStore*.cs` (new), `Blind/Format2/MetadataHostServices.BlindStore.cs` (new, hook H1) | - |
| Storage, Core | migrations 037, 038 **and 039** (039 byte for byte from 9.1, needed for the re-key list); `EventLogRepository.cs`, `IEventLogRepository.cs`, `EventRowLocal` (Core); `WhitelistRepository.cs`, `IWhitelistRepository.cs` (capability methods); `SnapshotTables.cs` (`Control` list, `Replicated` additions); `docs/migrations.golden.txt` | migration 039 (byte-identical copy); `BlobRepository.cs` (GC statement, 3.10, 9.6) | - |
| Vault | `Metadata/*` (ring, crypto, opener, key service, activation, readiness, drain), `Sync/EventLogger.cs`, `Core/Services/SessionService.cs`, `Sync/DependencyInjection.cs`, `Sync/Recovery/RecoveryEventPublisher.cs`, `Sync/DekRotation/DekRewrapper.cs` (the key step and the completion record in the DEK transaction; the Step B mirror calls follow in separate hunks), `Sync/DekRotation/DekRotationCompletionRecord.cs`, `Metadata/AuthoringBarrier.cs` | - | `Metadata/ChunkedContainer.cs` (new), `Sync/SnapshotJoinClient.cs` |
| Rekey | `RekeyTables.cs` (all of it: 037, 038 **and** the two tables of 039 as `Cleared`), `Steps/KeyMaterialStep.cs`, `RekeyD1Check.cs`, `Steps/EventLogResetStep.cs` | - | - |
| Api endpoints | `SyncEndpoints.cs` outside the for-join lambda (15.3); new `SyncActivationEndpoints.cs`; `ActivityEndpoints.cs`; `McpTools/BeeAuditTools.cs` if needed | `BlindEndpoints.cs`, `BlindRestoreEndpoints.cs`, `BlindStatusEndpoints.cs`, `BlindConsoleEndpoints.cs`, `BlindNodeEndpoints.cs` | the for-join lambda of `SyncEndpoints.cs` (15.3), `JoinEndpoints.cs`, `InitEndpoints.cs` |
| Api services | `CompactionService.cs`, `DekRotationService*.cs`, `RestoreInitiatorService.cs`, `SyncTokenStore.cs`, `Models/SyncModels.cs` | `Blind/BlindSeedService.cs`, `BlindSeedCutover.cs`, `BlindLogTrimmer.cs`, `BlindReplicaPackageCache.cs`, `BlindPackageBuilder.cs` (format 1, refused once activated), new `Blind/BlindPackageV2Builder.cs`, `BlindStatus/*`, `BlindBackup/*`, `Recovery/RecoveryStatusService.cs` | new `Blind/BaselineProducer.cs`, `Blind/BaselineScheduler.cs`, `Blind/FullNodeBaselineSource.cs`; `Blind/BlindNodeManager.cs`, `Blind/BlindAlarmService.cs`, `SnapshotService*.cs`, `SessionSnapshotKeyOperations.cs`, `ISnapshotKeyOperations.cs`, `SnapshotJoinCache.cs`, `Recovery/RecoveryRestoreService.cs`, `Recovery/BlindRestoreClient.cs`, `Recovery/RestoreEvidence.cs`, `BlindPhone/AndroidBackupRestoreSource.cs`, `Models/Requests.cs` (`JoinRequest`) |
| Hosts and compositions | `Startup/ApiServices.cs`, `Startup/BlindRoleServices.cs`, AppCore `Services/Blind/BlindMobileServices.cs`: each made `static partial` with the hooks of 15.2; any new `Startup/ApiServices.Metadata.cs` | `server/BeeMemoryBank.BlindNode/*` (incl. `linked-api-files.props`, `Startup/ExternalKeySnapshotKeyOperations.cs`), `tools/blind-link/exclude.txt`, new `Startup/BlindRoleServices.Format2.cs` (H2) and `Startup/ApiServices.Format2.cs` (H3) | new `Startup/ApiServices.Baselines.cs` (H4), new AppCore `Services/Blind/BlindMobileServices.Metadata.cs` (H5) |
| Clients | `server/BeeMemoryBank.Web` Admin card and status line; CLI `bmb sync readiness`, `bmb sync activate`, `bmb status` and the CLI command registration | `server/BeeMemoryBank.BlindCli`, `server/BeeMemoryBank.BlindConsole`, the full CLI's blind command files the BlindCli links | `BlindPhoneReplicaClient.cs`, `BlindPhonePullClient.cs` (relay context and capabilities in report-position), `BlindPhonePackageSource.cs`, AppCore files other than `BlindMobileServices.cs` (`BlindReplicaStats` -> `BlindStoreStats`, `BlindPhoneSync`, state, observer), `Cli/Commands/JoinCommand.cs`, mobile join services, `FullIos/Pages/SettingsPage.xaml.cs`, the four blind heads' status text, the Web Blind nodes page if the alarm needs it |
| Docs | `docs/sync.md` (links to `snapshot-restore.md` for the join), `docs/encryption.md`, `SECURITY.md`, `docs/architecture.md`, `docs/internet-access.md`, the activation operator section | `docs/blind-node/README.md`, `COMPOSITION.md`, `PARITY.md`, `STARTUP-CONTRACT.md`, `docs/compaction.md` | `docs/snapshot-restore.md` (incl. the join after activation), `docs/account-recovery.md`, `docs/blind-node/IOS.md` |
| Tests | new test files it adds; an existing test file is edited only by the part that owns the production file it tests | same | same |

Rules for code that is **linked** into the BlindNode host (`SyncEndpoints.cs`, `BlindRoleServices.cs` and the
`SnapshotService` partials listed in `server/BeeMemoryBank.BlindNode/linked-api-files.props`): it may use library
types and types from files already linked; a new Api type it needs goes into a file part 2 links (the hook file H2),
never into the Vault. Part 3 reaches the chunked container from the linked `SnapshotService.Create.cs` only through a
new `ISnapshotKeyOperations` member **with a default interface implementation** that throws `NotSupportedException`,
so the blind host's `ExternalKeySnapshotKeyOperations` (part 2) needs no change.

`SyncProtocolVersion.Current` keeps its value 3 (the pre-activation declaration) so every file still compiles; each
part switches the call sites **in its own files** to `ISyncProtocolPolicy` (5.6): part 1 `SyncEndpoints.cs` (outside
15.3), `PeerAuthenticator.cs`, `SyncClient.cs`, `EventLogger.cs`, `RecoveryEventPublisher.cs`, `EventLogResetStep.cs`,
`SyncModels.cs`, `SyncTokenStore.cs`, `RestoreInitiatorService.cs`, `EventApplier.cs`; part 2
`CoreBlindStatusContributor.cs`; part 3 `BlindAlarmService.cs`, `InitEndpoints.cs`, `JoinEndpoints.cs`,
`JoinCommand.cs`, `SnapshotJoinClient.cs`, `BlindPhonePullClient.cs`, `FullIos/Pages/SettingsPage.xaml.cs`.

### 15.2 Composition hooks (part 1 declares, the named part implements)

A `static partial void` method without an implementation is removed by the compiler, so each part builds on its
own; once the parts are combined the implementation is called. Each hook has exactly one implementing file.

| Hook | Declared by part 1 in | Called from | Implemented by, in a new file |
|---|---|---|---|
| H1 `static partial void AddBlindStoreServices(IServiceCollection services, string dataPath);` | `libs/BeeMemoryBank.Sync/Metadata/MetadataHostServices.cs`, `public static partial class MetadataHostServices` | its `public static IServiceCollection AddBlindMetadataHost(this IServiceCollection services, string dataPath)`, after it has replaced `IEnvelopeOpener` with `RelayEnvelopeOpener` | part 2: `libs/BeeMemoryBank.Sync/Blind/Format2/MetadataHostServices.BlindStore.cs` (store, reader, blind `IBaselineSource`, `IBlindStoreSummarySource`) |
| H2 `static partial void AddBlindRoleFormat2Services(IServiceCollection services, string dataPath);` | `server/BeeMemoryBank.Api/Startup/BlindRoleServices.cs` | end of `AddBlindRoleServices`, after `services.AddBlindMetadataHost(dataPath);` | part 2: `server/BeeMemoryBank.Api/Startup/BlindRoleServices.Format2.cs`, linked into the BlindNode host by part 2 (package v2 builder, blind activation observer) |
| H3 `static partial void AddFormat2ApiServices(IServiceCollection services, INodeRole role, string dataPath);` | `server/BeeMemoryBank.Api/Startup/ApiServices.cs` | `AddBeeApiServices`, right after `builder.Services.AddSync();` | part 2: `server/BeeMemoryBank.Api/Startup/ApiServices.Format2.cs` (the format-2 writer a full hub serves with) |
| H4 `static partial void AddBaselineServices(IServiceCollection services, INodeRole role, string dataPath);` | `server/BeeMemoryBank.Api/Startup/ApiServices.cs` | `AddBeeApiServices`, right after the H3 call | part 3: `server/BeeMemoryBank.Api/Startup/ApiServices.Baselines.cs` (producer, scheduler, full-node source and observer, join and restore services) |
| H5 `static partial void AddPhoneMetadataServices(IServiceCollection services, string dataDir);` | `libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindMobileServices.cs` | `AddBlindAppCore` (`ConfigureServices` forwards to it, `BlindMobileServices.cs:31-34`), right after `services.AddBlindMetadataHost(dataDir);`, which precedes `services.AddScoped<EventApplier>();` (base line 95) | part 3: `libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindMobileServices.Metadata.cs` (phone activation observer, store stats) |

Each implementing file declares the same class (`public static partial class MetadataHostServices`,
`BlindRoleServices`, `ApiServices`, `BlindMobileServices`) in the same namespace and implements only its hook. Part 1
declares all five hooks; parts 2 and 3 add their hook files on top of those declarations.

### 15.3 The one shared file: `SyncEndpoints.cs`

Part 3 edits only the lambda passed to `app.MapGet("/api/sync/snapshot/for-join", ...)`, its parameter list and
body (base lines 272-361), and nothing else in the file; part 1 edits nothing inside that lambda and leaves the
lines directly around it (the comment line 271 and the blank lines 270 and 362) unchanged, so the two sets of edits
are separate hunks that combine without conflict. Order: part 1, then part 3. The held-events refusal of 6.8
inside that lambda belongs to part 3 (through `IHeldEventStatus`).

### 15.4 Composition rules

1. A file is edited only by its owner in 15.1, apart from the byte-identical contract and migration files of 14 and
   the boundary of 15.3.
2. Goldens touched by several parts (`DI.golden.txt` x2, `vault-types.txt`, `shared-types.txt`,
   `migrations.golden.txt`) are regenerated once the parts are combined, not edited by hand.
3. New migrations get the numbers given here.
4. A part that changes a behaviour updates the product's own docs that describe it.
5. Order of dependence: part 1, part 2, part 3. A part that needs another part's implementation uses that code as it
   stands once the parts are combined; it never copies implementation code.
6. Release 2.5.2 (the engine swap and its companion changes) touches some of the same lines (served actor fields and
   `entityId`, the for-join blind check, tag embeddings in packages, applier log lines, `Directory.Packages.props`); a
   part keeps its change there minimal so the two combine.

## 16. Red tests

### 16.1 Canaries (Appendix B)

Seed a vault whose every metadata field holds a canary: two titles (ASCII and non-ASCII), a three-level folder path,
a renamed folder (old and new names), a tag in two letter cases, a merged and a renamed tag, a file name, a content
type, a protection hint, a version author, a conflict copy title, a hard-deleted folder, a legacy plaintext comment,
an agent name and a user display name. Search every artefact for each canary in UTF-8, UTF-16LE, JSON-escaped,
lower case and base64 (three alignments) with `MetadataCanaries.Scan*`. "No hit" is the pass condition; each scan
test first proves it finds the canary in a protocol-3 / pre-activation artefact (red on 2.5.1 and before activation).

### 16.2 Part 1

1. Wire: after activation `GET /api/sync/events` and push bodies (captured by a `DelegatingHandler`) carry no canary;
   before activation they do (control of the scanner).
2. Envelope tamper, each separately: ciphertext byte, tag, nonce, key id, a blob hash added/removed/reordered, the
   outer type, the protocol version, the article id set, padding not zero -> refused or quarantined, never applied,
   never parsed as protocol 3.
3. Nonce uniqueness (10,000 seals); same change twice -> different ciphertexts.
4. `EventClasses` guard; clear-field golden (3.11).
5. Isomorphism of the sealed path: one scripted sequence (create, edit, move, subtree rename, folder delete with a newer
   edit inside, hard delete of a subtree, tag rename/merge/delete in two cases, media link, comment) authored on A and
   applied by B once before activation (protocol 3) and once after (sealed): B's replicated tables and
   `StateDigest` are equal in both runs and equal to A's.
6. Locked node: sealed pushes to a locked node are `Held` and served onward byte-identical; restart; unlock; applied
   exactly once in order; received and applied positions as 6.6 at every step; revoke-ordering case of 6.5;
   `key` hold released by a late `metadata_key_set`; drain stops at a pending restore.
7. Activation: readiness lists a 2.5 peer as `not_capable` and refuses; activation creates the key and the event;
   a 2.5 peer gets 426 and is not pulled from; a 2.6 peer not yet activated syncs and becomes activated; stragglers
   applied, filtered from blind requesters, early baseline requested.
8. DEK rotation after activation creates generation g+1 under the new DEK; a peer left out of the rotation cannot open
   events sealed under it.
9. Logs: a log capture of the whole of 5 and 6 contains no canary.

### 16.3 Part 2

1. Relay: an activated Docker blind node (`BlindNodeFactory`) and an AppCore blind copy receive the scripted sequence
   sealed; their database file, WAL and journal, log and `tbl_event` contain no canary; the content tables are empty;
   every stored event is byte-identical to the author's (blind isomorphism).
2. A clear content event offered to an activated blind host is dropped and not stored.
3. Adoption refusals: wrong producer, not superadmin, bad signature, container hash, blob list hash, older
   `lamport_at_cut`, older generation, lower local cut, missing blobs.
4. First adoption purges legacy content: canaries present before (control), absent after (file + WAL).
5. Trimming bounded by `local_cut`, read-by-all and the anchor; nothing trimmed while activated without a baseline.
6. Blob clean-up keeps baseline and delta references, drops the rest.
7. Package format 2: no canary in any entry (decompressed, untarred; `baseline.bmbch` raw) nor in the outer manifest;
   `baseline.bmbch` does not start with `SQLite format 3`; a 2.5 reader refuses it; tampered entry refused; a signed package
   with a malformed manifest field, a baseline or delta Lamport time outside `[0, 2^53-2]`, or a `control.db` row no applier would write
   (8.4: a Lamport time past `2^53-2`, a key id of the wrong shape, an activation naming a key the package lacks, ...) is refused by the
   reader and by the seed route before anything is adopted,
   and a first seed that leaves no Lamport time to grant is refused before the baseline is adopted.
8. Status, console, CLI: no `articles` key, no note count, from 2.6.0 on.
9. Backup staging and restic input after adoption: no canary.
10. Operational-table golden (9.1); composition, DI and route goldens; boundary scan of the blind host and AppCore
    output: no Vault metadata type (`IMetadataKeyRing`, `MetadataKeyCrypto`, `VaultEnvelopeOpener`, `ChunkedContainer`).

### 16.4 Part 3

1. Container: truncation at every chunk boundary, reorder, substitution, header byte, final flag, trailing byte,
   wrong key, wrong purpose -> refused; a 64 MiB input with a 64 KiB chunk size round-trips with bounded memory.
   Counter bound (7.1): `NonceFor` accepts index 4294967295 and refuses 4294967296 and -1; nonces of neighbouring
   indices and of the same index with and without the final flag differ; with an internal test-only start index of
   4294967294 the writer seals two chunks of a three-chunk input and refuses the third, and the reader with the same
   start refuses a three-chunk file before opening its third chunk.
2. Baseline: the container and manifest contain no canary; the self-check catches a corrupted container; refused when
   locked, held, not activated, or a blob is missing; the signature verifies; `blobs.txt` equals the references.
3. Round trip (the main correctness test): source vault with the scripted sequence plus media, a conflict, recovery
   boxes and one DEK rotation before the baseline and one after; baseline + deltas -> restore on a clean node -> every
   replicated table equal to the source (locally derived columns excluded), anchor confirmed.
4. Withholding: the same with one delta removed -> anchor mismatch reported.
5. Restore resume: crash after N replayed deltas, restart, completes, tables equal.
6. Join after activation: archive database entry is not SQLite, no canary; 2.6 joiner imports and starts activated;
   a joiner without the capability gets 426 and no row; a 2.6 joiner refuses a plain archive from an activated producer;
   refused while held.
7. Phones: format-2 install, `?have` update, trim by `origin_seq`; the phone database has no canary and empty content
   tables; home screen shows age, deltas, size and no count; a phone backup restores (section 12).
8. Hub and reseed: a full hub serves format 2 after activation; the PC reseeds an activated Docker blind node with a
   `baseline` package after shipping blobs; the blind node's log is not reset and the PC's push position is kept.

### 16.5 After the parts are combined (end to end)

The end-to-end canary run on one mesh: two full nodes, a Docker blind host, an AppCore blind copy and a hub, all on
2.6, readiness, activation, sync, reseed, phone first load and update, restore from the blind node and from a phone
backup, with every artefact scanned; the 2,500 and 50,000 note fixtures (`tools/BeeMemoryBank.SeedGen`) for baseline
production, adoption and restore time and peak memory; `tools/blind-e2e` with activation.

## 17. Golden files, docs, migrations

- Migrations: `037_metadata_keys.sql` (part 1), `038_event_apply_state.sql` (part 1), `039_blind_baseline_store.sql`
  (part 2); `docs/migrations.golden.txt` regenerated once the parts are combined.
- `docs/full-node/ROUTES.golden.txt`: + `GET /api/sync/readiness`, `POST /api/sync/activation` (part 1).
  `docs/blind-node/ROUTES.golden.txt`: unchanged (a test keeps it so).
- `docs/full-node/DI.golden.txt`, `docs/blind-node/DI.golden.txt`, `docs/vault-split/vault-types.txt`,
  `docs/vault-split/shared-types.txt`: change in every part; regenerated once the parts are combined.
- New goldens: `docs/design/protocol4-clear-fields.golden.txt` (part 1), `docs/blind-node/OPERATIONAL-TABLES.golden.txt`
  (part 2).
- Docs per 15.

## 18. Open points

1. **New vaults switched off.** 2.6.0 ships switched off, new vaults included (a one-click activation passes at once). A
   later regular release can make new vaults activated.
2. **Event types hidden.** Hiding the inner type and article id goes beyond the minimum (it
   also hides how many notes change and how often); it costs two local columns. Kept.
3. **The restore barrier of 6.7 is a SHOULD.** 2.5.1 already applies clear content on a locked node before a deferred
   restore; the barrier fixes that for sealed events only.
4. **Join snapshot pre-activation stays plain**, also between two 2.6 nodes, to keep 2.6.0 identical to 2.5.1 until the
   switch.
5. **Locked hub packages.** A locked hub cannot sign a new package (as today); phones keep pulling deltas from it.
6. **Stale `key` holds** go to quarantine after 30 days; the number is a judgement.
7. **Release 2.5.2.** This document is written against the v2.5.1 tree; the lines that release 2.5.2 also touches are
   listed in 15.4, rule 6.

---

## Appendix A: contract files (verbatim)

### A.1 `libs/BeeMemoryBank.Sync/Metadata/EventClasses.cs`

```csharp
namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// Which event types carry human-authored metadata and therefore travel sealed after activation (docs/design/step-a-spec.md 3.1).
/// Every constant of <see cref="EventTypes"/> is in exactly one of <see cref="Sealable"/>, <see cref="Control"/> or is <see cref="Sealed"/>.
/// </summary>
public static class EventClasses
{
    /// <summary>The outer type of every protocol-4 content event.</summary>
    public const string Sealed = "sealed";

    /// <summary>The two new control types (spec 4.3, 5.4). EventTypes declares aliases of these three constants.</summary>
    public const string MetadataKeySet = "metadata_key_set";
    public const string MetadataActivation = "metadata_activation";

    /// <summary>Protocol-4 event version: content sealed, control clear.</summary>
    public const int SealedProtocol = 4;

    public static readonly IReadOnlySet<string> Sealable = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypes.ArticleCreate, EventTypes.ArticleUpdate, EventTypes.ArticleDelete,
        EventTypes.CommentCreate, EventTypes.CommentDelete,
        EventTypes.FolderCreate, EventTypes.FolderRename, EventTypes.FolderDelete,
        EventTypes.MediaCreate, EventTypes.MediaDelete, EventTypes.MediaLink,
        EventTypes.ConceptTagRename, EventTypes.ConceptTagMerge, EventTypes.ConceptTagDelete,
        EventTypes.HardDelete,
    };

    public static readonly IReadOnlySet<string> Control = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypes.WhitelistAdd, EventTypes.WhitelistRevoke, EventTypes.WhitelistUpdate,
        EventTypes.SnapshotCheckpoint, EventTypes.RestoreNetwork,
        EventTypes.DekRotationProposed, EventTypes.DekRotationCommit, EventTypes.MasterPasswordChanged,
        EventTypes.RecoveryBoxSet, EventTypes.RecoveryBoxRetire, EventTypes.RetiredLinkSet,
        EventTypes.StateAnchor, EventTypes.SealedSecretSet,
        MetadataKeySet, MetadataActivation,
    };

    public static bool IsSealable(string eventType) => Sealable.Contains(eventType);

    /// <summary>A protocol-1..3 event whose type is a content type: never served to a blind peer after activation.</summary>
    public static bool IsClearContent(int protocolVersion, string eventType) => protocolVersion < SealedProtocol && Sealable.Contains(eventType);
}
```

The file compiles on the base tree as it is. Part 1 adds to `EventTypes` (`EventPayloads.cs`) the aliases
`Sealed = Metadata.EventClasses.Sealed`, `MetadataKeySet = Metadata.EventClasses.MetadataKeySet` and
`MetadataActivation = Metadata.EventClasses.MetadataActivation`, and classifies them in `EventAuthorization` (3.1);
no other part edits `EventPayloads.cs`.

### A.2 `libs/BeeMemoryBank.Sync/Metadata/IEnvelopeOpener.cs`

```csharp
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>What a host does with a verified event (docs/design/step-a-spec.md 6.3).</summary>
public enum EnvelopeDisposition { Plain, Opened, Relay, Hold, Drop }

public sealed record EnvelopeOutcome(EnvelopeDisposition Disposition, SyncEvent? View = null, string? Reason = null)
{
    /// <summary>Apply the event as it is (a control event, or protocol-1..3 content on a host that applies it).</summary>
    public static EnvelopeOutcome AsIs(SyncEvent evt) => new(EnvelopeDisposition.Plain, evt);
    /// <summary>A sealed event opened: apply <paramref name="view"/>, store the original.</summary>
    public static EnvelopeOutcome OpenedAs(SyncEvent view) => new(EnvelopeDisposition.Opened, view);
    /// <summary>A blind host: store the original, apply nothing.</summary>
    public static EnvelopeOutcome RelayOnly() => new(EnvelopeDisposition.Relay);
    /// <summary>A full node that cannot open it now: store it held (<see cref="HoldReasons"/>).</summary>
    public static EnvelopeOutcome HoldFor(string reason) => new(EnvelopeDisposition.Hold, Reason: reason);
    /// <summary>Accept without storing (an activated blind host offered clear content).</summary>
    public static EnvelopeOutcome DropFor(string reason) => new(EnvelopeDisposition.Drop, Reason: reason);
}

public static class HoldReasons
{
    public const string Locked = "locked";
    public const string Key = "key";
    public const string Order = "order";
}

/// <summary>Keys and flags of an originator, for an apply that must not read tbl_whitelist (restore replay).</summary>
public sealed record OriginatorInfo(byte[] PublicKey, bool IsSuperadmin);

/// <param name="ReceivedFrom">The peer that delivered the event (pull source or pusher); stored as origin_peer.</param>
/// <param name="ReceivedSeq">The event's sequence number in that peer's log; stored as origin_seq.</param>
/// <param name="Originators">Restore replay only: resolves originators instead of tbl_whitelist.</param>
/// <param name="EventKeys">Restore replay only: the 32-byte event key (spec 3.5) of a MEK key id, or null.</param>
public sealed record ApplyContext(
    Guid? ReceivedFrom = null,
    long? ReceivedSeq = null,
    Func<Guid, OriginatorInfo?>? Originators = null,
    Func<string, byte[]?>? EventKeys = null);

/// <summary>
/// Called by EventApplier after the invariants, the originator lookup, the signature and the envelope shape passed.
/// A full node registers the Vault opener, every blind host the relay opener; there is no default.
/// Throws <see cref="InvalidDataException"/> when a sealed event does not open with a key this host holds (permanent).
/// </summary>
public interface IEnvelopeOpener
{
    ValueTask<EnvelopeOutcome> OpenAsync(SyncEvent verified, ApplyContext? context, CancellationToken ct = default);
}
```

### A.3 `libs/BeeMemoryBank.Sync/Metadata/ISyncProtocolPolicy.cs`

```csharp
namespace BeeMemoryBank.Sync.Metadata;

public static class MetadataCapabilities
{
    public const string EnvelopeV1 = "metadata-envelope-v1";

    /// <summary>What this build advertises in identity, authenticate, report-position, my-standing and join.</summary>
    public static readonly IReadOnlyList<string> Advertised = [EnvelopeV1];

    public static bool HasEnvelopeV1(IReadOnlyCollection<string>? capabilities) =>
        capabilities is not null && capabilities.Contains(EnvelopeV1);

    /// <summary>Parses the comma-separated form used in query strings and tbl_whitelist.last_capabilities.</summary>
    public static IReadOnlyList<string> Parse(string? commaSeparated) =>
        string.IsNullOrWhiteSpace(commaSeparated)
            ? []
            : commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The protocol this node declares and accepts; per host, flipped once by the activation (spec 5.6).</summary>
public interface ISyncProtocolPolicy
{
    /// <summary>3 before activation, 4 after.</summary>
    int Declared { get; }

    /// <summary>True once this node has applied metadata_activation.</summary>
    bool SealsMetadata { get; }

    /// <summary>Before activation: declared >= 3. After: declared >= 4, or 3 with the envelope capability.</summary>
    bool IsCompatiblePeer(int? declaredProtocol, IReadOnlyCollection<string>? capabilities);

    /// <summary>False when the peer declares a protocol this build cannot apply (above 4).</summary>
    bool ShouldPullFrom(int? declaredProtocol);

    /// <summary>Called by the activation and by the metadata_activation applier. Idempotent.</summary>
    void MarkActivated();
}
```

### A.4 `libs/BeeMemoryBank.Sync/Metadata/IMetadataActivationObserver.cs`

```csharp
namespace BeeMemoryBank.Sync.Metadata;

public sealed record MetadataActivation(Guid ActivationId, string KeyId, int Generation, DateTime ActivatedAt, Guid AuthorNodeId);

/// <summary>Host effects of the activation (spec 5.5). Called once, after the activation row is written; must not throw for a retry.</summary>
public interface IMetadataActivationObserver
{
    Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default);
}
```

### A.5 `libs/BeeMemoryBank.Sync/Metadata/IHeldEventStatus.cs`

```csharp
namespace BeeMemoryBank.Sync.Metadata;

public sealed record HeldEventSummary(
    int Held, int HeldLocked, int HeldKey, int HeldOrder,
    DateTime? OldestHeldAt, long AppliedThroughSequence, long HeadSequence, int QuarantinedAfterReceipt);

/// <summary>Received-but-not-applied events of this node (spec 6.6, 6.8). A blind host always reports none.</summary>
public interface IHeldEventStatus
{
    Task<bool> AnyHeldAsync(CancellationToken ct = default);
    Task<HeldEventSummary> GetSummaryAsync(CancellationToken ct = default);
}

/// <summary>Asks the drain (spec 6.7) to run soon; a no-op where nothing can be held.</summary>
public interface IHeldEventDrain
{
    void RequestDrain();
}
```

### A.6 `libs/BeeMemoryBank.Sync/Metadata/IBaselineScheduler.cs`

```csharp
namespace BeeMemoryBank.Sync.Metadata;

public static class BaselineReasons
{
    public const string Scheduled = "scheduled";
    public const string Activation = "activation";
    public const string HardDelete = "hard_delete";
    public const string LateProtocol3 = "late_protocol3";
    public const string KeyRotation = "key_rotation";
    public const string BlindReseed = "blind_reseed";
    public const string OnDemand = "on_demand";
}

/// <summary>Full nodes only (spec 7.5); EventApplier takes it as an optional dependency.</summary>
public interface IBaselineScheduler
{
    void RequestEarly(string reasonCode);
}

/// <summary>What a blind host holds, for status and my-standing (spec 10). No note count by design.</summary>
public sealed record BlindStoreSummary(
    Guid? BaselineId, Guid? BaselineProducer, DateTime? BaselineCreatedAt, int? KeyGeneration,
    long DeltaCount, long DeltaBytes, long BlobCount, long BlobBytes, long StoredBytes);

public interface IBlindStoreSummarySource
{
    Task<BlindStoreSummary> GetSummaryAsync(CancellationToken ct = default);
}
```

`MyStanding` (`libs/BeeMemoryBank.Sync/Blind/MyStanding.cs`) gains, as optional trailing members:
`[property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null`,
`[property: JsonPropertyName("app_version")] string? AppVersion = null`,
`[property: JsonPropertyName("activated")] bool? Activated = null`,
`[property: JsonPropertyName("store")] BlindStoreSummary? Store = null`; and `PeerProtocolSeen` gains
`[property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null`,
`[property: JsonPropertyName("app_version")] string? AppVersion = null`,
`[property: JsonPropertyName("capabilities_seen_at")] DateTime? CapabilitiesSeenAt = null`. The handler fills `Store`
from `IBlindStoreSummarySource` when one is registered (blind hosts).

### A.7 `libs/BeeMemoryBank.Sync/Blind/Format2/BaselineManifest.cs`

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>The signed description of a baseline (spec 7.4). The exact bytes of manifest.json are what is signed.</summary>
public sealed record BaselineManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("baseline_id")] Guid BaselineId,
    [property: JsonPropertyName("producer_node_id")] Guid ProducerNodeId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("producer_log_head")] long ProducerLogHead,
    [property: JsonPropertyName("lamport_at_cut")] long LamportAtCut,
    [property: JsonPropertyName("pull_positions")] IReadOnlyList<BlindManifestPosition> PullPositions,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("key_generation")] int KeyGeneration,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("container_sha256")] string ContainerSha256,
    [property: JsonPropertyName("container_bytes")] long ContainerBytes,
    [property: JsonPropertyName("blob_list_sha256")] string BlobListSha256,
    [property: JsonPropertyName("blob_count")] int BlobCount,
    [property: JsonPropertyName("blob_bytes")] long BlobBytes)
{
    public const string CurrentFormat = "bmb-baseline-v1";
    public const string FileName = "manifest.json";
    public const string SignatureFileName = "manifest.json.sig";
    public const string BlobListFileName = "blobs.txt";
    public const string ContainerFileName = "baseline.bmbch";

    private static readonly byte[] SignatureDomain = "BMB-BASELINE-V1\0"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BaselineManifest Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BaselineManifest>(bytes)
            ?? throw new InvalidDataException("Empty baseline manifest.");
        if (manifest.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported baseline format '{manifest.Format}'.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>
    /// A producer's signature makes the manifest authentic, not complete: the collections and strings every receiver reads
    /// (<see cref="PullPositionOf"/>, the hash comparisons) must be there and in range, or reading fails later, after a baseline
    /// was already taken. Whether the values are right is the receiver's business (the hashes, the key, the cut).
    /// </summary>
    private void Validate()
    {
        if (KeyId is null || ContainerSha256 is null || BlobListSha256 is null)
            throw new InvalidDataException("The baseline manifest lacks its key id or one of its hashes.");
        if (ProducerLogHead < 0 || ContainerBytes < 0 || BlobCount < 0 || BlobBytes < 0)
            throw new InvalidDataException("The baseline manifest holds a negative position, size or count.");
        if (LamportAtCut < 0 || LamportAtCut > BlindManifestV2.MaximumLamport)
            throw new InvalidDataException("The baseline manifest holds a Lamport time outside the format-2 limit.");
        if (PullPositions is null)
            throw new InvalidDataException("The baseline manifest lacks its pull positions.");
        var seen = new HashSet<Guid>();
        foreach (var position in PullPositions)
        {
            if (position is null || position.RemoteNodeId == Guid.Empty)
                throw new InvalidDataException("The baseline manifest holds a pull position without a node id.");
            if (position.LastSequence < 0)
                throw new InvalidDataException("The baseline manifest holds a negative pull position.");
            if (!seen.Add(position.RemoteNodeId))
                throw new InvalidDataException("The baseline manifest lists a pull position twice.");
        }
    }

    /// <summary>What the producer signs and every receiver verifies: domain tag, then the manifest bytes as stored.</summary>
    public static byte[] SignaturePayload(byte[] manifestBytes)
    {
        var payload = new byte[SignatureDomain.Length + manifestBytes.Length];
        SignatureDomain.CopyTo(payload, 0);
        manifestBytes.CopyTo(payload, SignatureDomain.Length);
        return payload;
    }

    /// <summary>The producer's pull position on <paramref name="node"/>, or 0 when it never pulled from it.</summary>
    public long PullPositionOf(Guid node) =>
        PullPositions.FirstOrDefault(p => p.RemoteNodeId == node)?.LastSequence ?? 0;
}
```

### A.8 `libs/BeeMemoryBank.Sync/Blind/Format2/Format2Contracts.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync.Blind.Format2;

public static class BlindPackageKinds
{
    public const string Full = "full";
    public const string Baseline = "baseline";
}

public sealed record BlindActivationRef(
    [property: JsonPropertyName("activation_id")] Guid ActivationId,
    [property: JsonPropertyName("key_id")] string KeyId);

/// <summary>blind-manifest.json of a format-2 package (spec 8.3). ProducerNodeId is the SERVING node.</summary>
public sealed record BlindManifestV2(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("seed_id")] Guid SeedId,
    [property: JsonPropertyName("producer_node_id")] Guid ProducerNodeId,
    [property: JsonPropertyName("cp_sequence")] long CpSequence,
    [property: JsonPropertyName("includes_up_to")] long? IncludesUpTo,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("whitelist")] IReadOnlyList<BlindManifestPeer> Whitelist,
    [property: JsonPropertyName("positions")] IReadOnlyList<BlindManifestPosition> Positions,
    [property: JsonPropertyName("baseline_id")] Guid BaselineId,
    [property: JsonPropertyName("cut_in_server_log")] long CutInServerLog,
    [property: JsonPropertyName("delta_count")] int DeltaCount,
    [property: JsonPropertyName("contains_blobs")] bool ContainsBlobs,
    [property: JsonPropertyName("activation")] BlindActivationRef? Activation,
    [property: JsonPropertyName("standing")] IReadOnlyList<SyncEvent>? Standing = null)
{
    public const string FileName = "blind-manifest.json";
    public const string CurrentFormat = "bmb-blind-package-v2";
    public const string ControlDbFileName = "control.db";
    public const string DeltasFileName = "deltas.jsonl";
    public const string BaselineDirectory = "baseline";
    public const string BlobsDirectory = "blobs";

    /// <summary>
    /// The exclusive Lamport ceiling used by restore (<c>RecoveryRestoreService.MaxRestoredLamport</c>): 2^53 events, beyond any real
    /// mesh and far from the type's overflow edge. A first-seed grant is one above the highest time it brings, so the package leaves
    /// one value for that grant and still keeps it below restore's exclusive ceiling.
    /// </summary>
    public const long RestoreLamportExclusive = 1L << 53;

    /// <summary>The highest Lamport time a format-2 package may carry; the next value is reserved for a first-seed grant.</summary>
    public const long MaximumLamport = RestoreLamportExclusive - 2;

    /// <summary>The highest Lamport time a first-seed grant may use and still be accepted by restore.</summary>
    public const long MaximumFirstSeedGrant = RestoreLamportExclusive - 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BlindManifestV2 Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BlindManifestV2>(bytes)
            ?? throw new InvalidDataException("Empty blind manifest.");
        if (manifest.Format != CurrentFormat)
            throw new InvalidDataException($"Unsupported blind package format '{manifest.Format}'.");
        if (manifest.Kind is not (BlindPackageKinds.Full or BlindPackageKinds.Baseline))
            throw new InvalidDataException($"Unsupported blind package kind '{manifest.Kind}'.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>
    /// Everything a receiver reads from the manifest after it has adopted the baseline - the whitelist rows, the positions, the
    /// standing events, the activation - is checked here, before anything is adopted: the manifest is signed, which makes it
    /// authentic, not well-formed, and a field that fails halfway through a seed leaves the node half-applied (step-a-spec 8.6).
    /// </summary>
    private void Validate()
    {
        if (ProducerNodeId == Guid.Empty) throw Invalid("names no serving node");
        if (BaselineId == Guid.Empty) throw Invalid("names no baseline");
        if (CpSequence < 0 || CutInServerLog < 0 || DeltaCount < 0 || IncludesUpTo is < 0)
            throw Invalid("has a negative position or count");

        if (Whitelist is null) throw Invalid("has no whitelist");
        var peers = new HashSet<Guid>();
        foreach (var peer in Whitelist)
        {
            if (peer is null || peer.NodeId == Guid.Empty) throw Invalid("lists a row without a node id");
            if (!peers.Add(peer.NodeId)) throw Invalid("lists a node twice");
            if (peer.DisplayName is null) throw Invalid($"lists node {peer.NodeId} without a display name");
            if (!IsPublicKey(peer.PublicKeyB64)) throw Invalid($"lists node {peer.NodeId} with a public key that is not {CryptoConstants.Ed25519PublicKeySize} bytes of base64");
            if (peer.LamportTs < 0 || peer.LamportTs > MaximumLamport) throw Invalid($"lists node {peer.NodeId} with a Lamport time that is negative or past the limit");
        }

        if (Positions is null) throw Invalid("has no positions");
        var positions = new HashSet<Guid>();
        foreach (var position in Positions)
        {
            if (position is null || position.RemoteNodeId == Guid.Empty) throw Invalid("has a position without a node id");
            if (position.LastSequence < 0) throw Invalid($"has a negative position for node {position.RemoteNodeId}");
            if (!positions.Add(position.RemoteNodeId)) throw Invalid($"lists a position twice for node {position.RemoteNodeId}");
        }

        if (Activation is not null && (Activation.ActivationId == Guid.Empty || !SealedEnvelope.IsKeyId(Activation.KeyId)))
            throw Invalid("has an activation without a valid id or key id");

        // The standing events of a first seed go through the applier one by one (a refused one is skipped), but their
        // Lamport times set the grant time, and a whitelist_add is read for its key before it is applied.
        foreach (var evt in Standing ?? [])
        {
            if (evt is null || evt.EventId == Guid.Empty || string.IsNullOrEmpty(evt.EventType) || evt.Payload is null)
                throw Invalid("carries a standing event that is not a complete event");
            if (evt.LamportTs < 0 || evt.LamportTs > MaximumLamport)
                throw Invalid("carries a standing event whose Lamport time cannot be used");
            if (evt.EventType == EventTypes.WhitelistAdd && !ReadsAsWhitelistAdd(evt.Payload))
                throw Invalid("carries a standing whitelist_add event whose payload cannot be read");
        }
    }

    private static InvalidDataException Invalid(string what) => new($"The blind manifest {what}.");

    private static bool IsPublicKey(string? base64)
    {
        if (base64 is null) return false;
        Span<byte> key = stackalloc byte[CryptoConstants.Ed25519PublicKeySize];
        return Convert.TryFromBase64String(base64, key, out var written) && written == key.Length;
    }

    private static bool ReadsAsWhitelistAdd(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<WhitelistAddPayload>(payload) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A baseline on disk: the directory holds manifest.json, manifest.json.sig, blobs.txt and baseline.bmbch.</summary>
public sealed record BaselineFiles(string Directory, BaselineManifest Manifest, byte[] ManifestBytes, byte[] Signature);

/// <summary>The baseline a node can serve and the position of ITS OWN log the baseline covers (spec 8.3, 9.7).</summary>
public sealed record ServableBaseline(BaselineFiles Files, long CutInLocalLog);

/// <summary>Full node: newest produced baseline. Blind host: current adopted one.</summary>
public interface IBaselineSource
{
    Task<ServableBaseline?> GetServableAsync(CancellationToken ct = default);
}

public sealed record BlindPackageV2Request(
    string Kind, Guid SeedId, ServableBaseline Baseline, long? IncludesUpTo, bool IncludeBlobs, string? OutputDirectory = null);

public sealed record BlindPackageV2(string FilePath, string Sha256, long Length, byte[] DetachedSignature, BlindManifestV2 Manifest);

/// <summary>Assembles and signs a format-2 package from a servable baseline and this node's own log, control rows and blobs.</summary>
public interface IBlindPackageV2Writer
{
    Task<BlindPackageV2> WriteAsync(BlindPackageV2Request request, CancellationToken ct = default);
}

/// <summary>A package after the checks of spec 8.6, steps 1-4. Deltas are still to be stored through the applier.</summary>
public sealed record VerifiedPackageV2(
    string Directory, BlindManifestV2 Manifest, BaselineFiles Baseline,
    string? ControlDbPath, string? DeltasPath, string? BlobsDirectory);

/// <param name="trustedProducer">Public key and superadmin flag of a baseline producer as the RECEIVER trusts it; null = unknown.</param>
public interface IBlindPackageV2Reader
{
    Task<VerifiedPackageV2> ExtractVerifiedAsync(
        string archivePath, byte[] detachedSignature, byte[] servingNodePublicKey,
        Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> trustedProducer,
        string targetDirectory, CancellationToken ct = default);
}

public sealed record AdoptedBaseline(BaselineFiles Files, long LocalCut, DateTime AdoptedAt);

public enum AdoptOutcome { Adopted, AlreadyCurrent, Refused }

public sealed record AdoptResult(AdoptOutcome Outcome, string? Reason, int TrimmedEvents, int CollectedBlobs);

/// <summary>A blind host's baseline store (spec 9). Never opens a baseline.</summary>
public interface IBaselineStore
{
    Task<AdoptedBaseline?> GetCurrentAsync(CancellationToken ct = default);

    /// <param name="localCut">Position in this node's own log the baseline covers (spec 9.2).</param>
    /// <param name="producerAuthorized">The producer check of spec 9.2 step 1, made by the caller who knows the authority (pair code or whitelist).</param>
    Task<AdoptResult> AdoptAsync(BaselineFiles files, long localCut, bool producerAuthorized, CancellationToken ct = default);

    Task<int> TrimAsync(CancellationToken ct = default);
    Task<int> CollectGarbageAsync(CancellationToken ct = default);

    /// <summary>Spec 9.3; idempotent.</summary>
    Task PurgeLegacyContentAsync(CancellationToken ct = default);
}
```

### A.9 `libs/BeeMemoryBank.Vault/Metadata/IMetadataKeyRing.cs`

```csharp
namespace BeeMemoryBank.Vault.Metadata;

public sealed record MetadataKeyInfo(string KeyId, int Generation);

/// <summary>A row of tbl_metadata_key as stored (spec 4.1).</summary>
public sealed record MetadataKeyRow(string KeyId, int Generation, string DekFingerprint, byte[] Wrapped, byte[] Iv);

public sealed class MetadataKeyUnavailableException(string keyId)
    : InvalidOperationException($"Metadata key {keyId} cannot be opened on this node now.")
{
    public string KeyId { get; } = keyId;
}

/// <summary>Unwrapped MEKs of an unlocked full node, in memory only (spec 4.5). Cleared when the vault locks.</summary>
public interface IMetadataKeyRing
{
    /// <summary>The key that seals now (spec 4.8); null when locked or none can be opened.</summary>
    MetadataKeyInfo? Current { get; }

    bool CanOpen(string keyId);

    /// <summary>
    /// HKDF-SHA256 subkey of the MEK <paramref name="keyId"/> with <paramref name="info"/> and <paramref name="salt"/>, 32 bytes.
    /// The caller clears it. Throws <see cref="MetadataKeyUnavailableException"/>.
    /// </summary>
    byte[] DeriveKey(string keyId, string info, ReadOnlySpan<byte> salt);
}
```

Not a contract file, but the part-1 API that part 3's restore calls (14, 15):
`libs/BeeMemoryBank.Vault/Metadata/MetadataKeyCrypto.cs`, `public static class MetadataKeyCrypto` with
`const string EventKeyInfo = "bmb-meta-event-v1"`,
`(byte[] Mek, MetadataKeyRow Row) Create(int generation, byte[] masterDek)` (spec 4.2; caller clears `Mek`),
`byte[]? TryUnwrap(MetadataKeyRow row, IEnumerable<byte[]> candidateDeks)` (the DEK whose fingerprint matches; null when
none), `byte[] Derive(byte[] mek, string info, ReadOnlySpan<byte> salt)` (HKDF-SHA256, 32 bytes) and
`byte[] DeriveEventKey(byte[] mek)` (= `Derive(mek, EventKeyInfo, empty)`, spec 3.5).

## Appendix B: `MetadataCanaries.cs` (test helper, verbatim)

Placed as `tests/<project>/MetadataCanaries.cs` in every test project that scans (SDK projects include it without a
csproj change); identical text everywhere, so two parts adding it to the same project combine cleanly.

```csharp
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BeeMemoryBank.TestSupport;

public sealed record CanaryHit(string Canary, string Encoding, string Where, long Offset);

/// <summary>Canary values and a byte scanner for the Step A red tests (docs/design/step-a-spec.md 16.1).</summary>
public static class MetadataCanaries
{
    public static string Ascii(string label) => $"cnry{label}{Guid.NewGuid():N}";

    public static string NonAscii(string label) => $"ZürichΩ{label}{Guid.NewGuid():N}";

    /// <summary>The byte forms a canary may take in a file, a body or a log.</summary>
    public static IEnumerable<(string Name, byte[] Bytes)> Forms(string canary)
    {
        var utf8 = Encoding.UTF8.GetBytes(canary);
        yield return ("utf8", utf8);
        yield return ("utf16le", Encoding.Unicode.GetBytes(canary));
        yield return ("lower", Encoding.UTF8.GetBytes(canary.ToLowerInvariant()));
        var escaped = JsonSerializer.Serialize(canary, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        yield return ("json", Encoding.UTF8.GetBytes(escaped[1..^1]));
        for (var shift = 0; shift < 3; shift++)
        {
            var shifted = new byte[shift + utf8.Length];
            utf8.CopyTo(shifted, shift);
            var b64 = Convert.ToBase64String(shifted);
            var start = shift == 0 ? 0 : 4;
            var core = b64[start..^4];
            if (core.Length >= 8) yield return ($"base64+{shift}", Encoding.ASCII.GetBytes(core));
        }
    }

    public static List<CanaryHit> Scan(byte[] haystack, IEnumerable<string> canaries, string where)
    {
        var hits = new List<CanaryHit>();
        foreach (var canary in canaries)
            foreach (var (name, needle) in Forms(canary))
            {
                var at = haystack.AsSpan().IndexOf(needle);
                if (at >= 0) hits.Add(new CanaryHit(canary, name, where, at));
            }
        return hits;
    }

    public static List<CanaryHit> ScanFile(string path, IEnumerable<string> canaries)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Scan(memory.ToArray(), canaries, path);
    }

    /// <summary>Every file below <paramref name="directory"/> (database, -wal, -shm, journals, logs, packages as raw bytes). Not the node's instance lock:
    /// it holds no data and a running host holds it exclusively (an exclusive flock on Linux and macOS: any other open fails).</summary>
    public static List<CanaryHit> ScanDirectory(string directory, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != ".instance.lock")
            .SelectMany(f => ScanFile(f, list)).ToList();
    }

    /// <summary>A .tar.gz package: the compressed bytes, then every entry decompressed.</summary>
    public static List<CanaryHit> ScanArchive(string archivePath, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        var hits = ScanFile(archivePath, list);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null) continue;
            using var memory = new MemoryStream();
            entry.DataStream.CopyTo(memory);
            hits.AddRange(Scan(memory.ToArray(), list, $"{archivePath}!{entry.Name}"));
        }
        return hits;
    }
}
```
