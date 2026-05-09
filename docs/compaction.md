# Event Log Compaction

BeeMemoryBank's sync protocol is event-sourced: every operation (create/update/delete) is
logged to `tbl_event` and synced between nodes. Over time this log grows monotonically.
Compaction is how we bound that growth.

## Trigger

Compaction is **admin-triggered only** — no background/periodic compaction. The admin sees
a preview of what would be deleted and confirms in the Web UI (Admin → Event Log & Compaction
→ "Compact Now"). Or via API:

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

Compaction refuses to run if `safe_cp ≤ current_min_seq` ("nothing to compact").

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

That peer must wipe its data and rejoin via the snapshot flow (Setup → Join Network).

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

The Admin page shows an "Event Log & Compaction" section with:
- **Statistics grid**: total events, head seq, min retained seq, active peers
- **Peer positions**: each peer's last synced sequence number
- **Warnings**: e.g. peer position too close to head
- **Compact button**: with preview info and confirmation dialog
- **Compaction history**: expandable table of all `snapshot_checkpoint` events showing CP, events removed, snapshot file, timestamp, and chain link
