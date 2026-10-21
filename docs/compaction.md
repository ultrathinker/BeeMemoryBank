# Event Log Compaction

BeeMemoryBank's sync protocol is event-sourced: every operation (create/update/delete) is
logged to `tbl_event` and synced between nodes. Over time this log grows monotonically.
Compaction is how we bound that growth.

## Trigger

Compaction is **admin-triggered only** — no background/periodic compaction. The admin sees
a preview of what would be deleted and confirms in the Web UI (Admin → Event Log & Compaction
→ "Compact now"). Or via API:

    curl -H "X-Internal-Key: $BMB_INTERNAL_KEY" \
         http://localhost:5300/api/admin/compact/preview

    curl -X POST -H "X-Internal-Key: $BMB_INTERNAL_KEY" \
         -H "Content-Type: application/json" \
         -d '{"reason":"manual"}' \
         http://localhost:5300/api/admin/compact

## Safe compaction point (CP)

The safe CP is the maximum sequence number that can be deleted without cutting off any active
peer's sync position:

    safe_cp = min(peer.last_sequence_num for peer in active_whitelist) - SAFETY_MARGIN

`SAFETY_MARGIN = 1000`. If the node has no active peers, `safe_cp = head_seq - SAFETY_MARGIN`.

Compaction refuses to run if `safe_cp ≤ current_min_seq` ("nothing to compact"). It also refuses (409) while the
node holds events it received but has not applied yet (metadata encryption: a sealed event that arrived while the vault
was locked, or before its key; see [sync.md](sync.md#held-events-received-and-applied)): cutting the log below them
would lose them. Unlock the node and wait until they are applied.

Once metadata encryption is switched on (2.6.0), compaction is also refused while the node holds
sealed events that still wait to be applied — a locked node that has been receiving traffic relays
it but cannot safely compact, because its tables lag behind its event log. The refusal is HTTP 409;
the node's status says how many changes wait ("N changes wait for unlock"), and compaction becomes
possible again at the next unlock.

## What happens on compact

1. Generate a signed snapshot at `safe_cp` (local-backup flavor, full DB; not filtered).
2. Inside one short transaction: `DELETE FROM tbl_event WHERE sequence_num ≤ safe_cp AND event_type != 'snapshot_checkpoint'` + `INSERT INTO tbl_compaction_log`.
3. Outside the transaction: emit a new `snapshot_checkpoint` event containing:
   - `cp_seq`, `events_removed`, `snapshot_file_name`
   - `snapshot_sha256` (hash of the generated `.tar.gz`)
   - `prev_checkpoint_sha256` (SHA-256 of the previous checkpoint's payload, or null for the first)
   - `produced_at`

The checkpoint is Ed25519-signed by the node and stays in `tbl_event` forever — it's never
compacted away (excluded by event_type). This forms a hash-chain audit trail.

## Effect on peers

After compaction, a peer whose `last_sequence_num < safe_cp` cannot catch up via event replay
alone. On the next `GET /api/sync/events?afterSequence=X` (with X below CP), the server returns:

    HTTP 410 Gone
    { "error": "SEQUENCE_TOO_OLD",
      "last_compaction_cp": <cp>,
      "current_head_seq": <head>,
      "message": "Your position is older than the last compaction point. Wipe this node and rejoin via /Setup." }

That peer must wipe its data and rejoin via the snapshot flow (Setup → Join Network, or
`bmb join`, which takes the snapshot too since 2.4.0). The peer shows this state on every page of
its Web UI and in `bmb status` ("This node cannot catch up ... Wipe this node and join again").
A node that joins after a compaction is not affected: it starts from the snapshot's checkpoint.

## Audit trail

`snapshot_checkpoint` events form a Merkle-like chain (each references the SHA-256 of the
previous's payload). Pulling these via `GET /api/admin/compact/checkpoints` (or in the Web UI under
"Compaction history") gives a tamper-evident log of every compaction ever performed.

## API Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/admin/compact/preview` | Preview compaction: head seq, min seq, proposed CP, peer positions, warnings |
| `POST` | `/api/admin/compact` | Execute compaction. Body: `{ "explicitCp": null, "reason": "manual" }` |
| `GET` | `/api/admin/compact/checkpoints` | List `snapshot_checkpoint` events (audit trail), newest first |

All endpoints require `X-Internal-Key` header. POST additionally requires an unlocked session.

## UI

The "Event Log & Compaction" page of the Admin area (`/Admin/Compaction`) explains what the log is and what compaction does, and shows:
- **Statistics grid**: total events, head seq, min retained seq, active peers
- **Peer positions**: each peer's last synced sequence number
- **Warnings**: e.g. peer position too close to head
- **Compact button**: with preview info and confirmation dialog
- **Compaction history**: expandable table of all `snapshot_checkpoint` events showing CP, events removed, snapshot file, timestamp, and chain link

## Blind nodes: local trim

A blind node authors no event, so it cannot publish a `snapshot_checkpoint`. It trims its own log instead
(`BlindLogTrimmer`, every 6 hours, and once after each baseline it adopts; the rule is
`BaselineStore.TrimLogAsync`): rows go up to the highest sequence that

- every active peer has **reported** reading from it (its own report, never what was merely served to it; a peer that never
  reported keeps the log whole), and
- the newest state anchor covers (no anchor: nothing goes).

The cut is recorded in `tbl_compaction_log`, so a peer asking below it gets the same 410 as above.

**After the metadata activation** (Step A, docs/design/step-a-spec.md 9.5) a third bound applies: the cut never passes what the
blind node's current sealed baseline covers of its own log (`tbl_baseline.local_cut`). Until the node has adopted its first
format-2 baseline nothing is trimmed at all - the sealed deltas are then the only copy of that content on the node. A newer
baseline (sent by a superadmin full node, or adopted on a phone) moves the bound forward. Before the activation the rule is
exactly the one above.
