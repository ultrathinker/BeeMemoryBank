# ADR 0003: Single-Transaction Atomicity for DEK Rotation

## Status
Accepted

## Context
DEK rotation replaces the node's master Data Encryption Key (DEK) with a new one. Doing so
requires re-wrapping every per-row encrypted DEK across four tables (`tbl_article_body`,
`tbl_article_version`, `tbl_conflict_version`, `tbl_media`), deleting all agents (their API
keys are encrypted with the old DEK and the server cannot re-wrap secrets it never sees in
plaintext), cleaning up key slots, and finally flipping the node's sentinel value and
`dek_epoch` on `tbl_node_identity`.

The system has no per-row marker of *which DEK epoch* a row is currently wrapped under. The
per-row AAD versioning (`wrapped.Length == 48` for legacy v0, 49-byte v1 with an AAD prefix)
only distinguishes wrap *format*, not which master DEK generation was used. The only
ground truth for "what is the current DEK" is the single sentinel value + `dek_epoch` pair on
`tbl_node_identity`. That creates a sharp constraint: if some rows get re-wrapped with the new
DEK while others are left on the old DEK — e.g. because the process crashed partway through a
long-running rewrap — there is no way to detect this from the data itself. A row wrapped under
the old DEK looks structurally identical to one wrapped under the new DEK; the only way to find
out is to try unwrapping it against whichever DEK the sentinel currently claims is active, and
fail. Once that state exists, it is not just inconvenient to recover from — it is
unrecoverable, because nothing on disk records which of the two DEKs a given row actually needs.

## Evaluated Options

1. **Incremental, resumable rewrap with checkpointing outside the transaction.**
   Process rows in batches, committing each batch as its own transaction, and persist a
   `last_processed_id` checkpoint so a crash can resume from where it left off.
   - **Pros**: shorter-lived locks per batch; naturally scales to very large databases without
     holding one long-lived write transaction.
   - **Cons**: this is precisely the failure mode described above. Between batch commits, the
     database sits in a state where some rows are on the new DEK and some are still on the old
     one, with no record of the split. A crash at that point is not a delayed retry — it is data
     loss, because there is no per-row epoch marker to tell a recovery pass which DEK to try.

2. **Per-row DEK-epoch marker, enabling safe incremental/resumable rewrap.**
   Add an explicit "wrapped under epoch N" tag to every encrypted-DEK column, so a resumed pass
   (or a straggler-detection sweep) could tell old-DEK rows from new-DEK rows directly.
   - **Pros**: would remove the undetectability problem that rules out option 1, and open the
     door to incremental rotation on very large databases.
   - **Cons**: a schema/format change touching every table that stores a wrapped DEK, a new
     comparison the unwrap hot path must perform on every read, and a straggler-sweep mechanism
     to guarantee eventual convergence — substantially more moving parts than the rotation flow
     needs today, for a scale problem BeeMemoryBank (a personal/small-team knowledge base, not a
     multi-terabyte store) does not currently have.

3. **Single all-or-nothing transaction** (chosen). Re-wrap all four tables, delete agents,
   update/delete key slots, and flip the sentinel + epoch inside one SQLite transaction.
   - **Pros**: eliminates the undetectable-partial-state risk entirely — the sentinel/epoch flip
     and every row's re-wrap either land together or none of them do.
   - **Cons**: holds one open write transaction for the entire operation; no mid-flight
     pause/resume — a crash forces a full retry via a fresh `AcceptCommitAsync` call.

## Decision
`RewrapDestructiveCoreAsync` (`server/BeeMemoryBank.Api/Services/DekRotationService.Rewrap.cs`)
performs the entire destructive phase — re-wrapping `tbl_article_body`, `tbl_article_version`,
`tbl_conflict_version`, and `tbl_media`, deleting agents, updating/deleting key slots, and
updating the sentinel, `dek_epoch`, and rotation state on `tbl_dek_rotation_state` — inside a
single SQLite transaction (`conn.BeginTransaction()` / `tx.Commit()` / `tx.Rollback()` on any
exception). Only after that transaction commits does the code swap the in-memory master DEK
(`_sessionService.SwapMasterDek(newDek)`). This is documented directly above
`AcceptCommitAsync` in `DekRotationService.Accept.cs` as a deliberate design note.

### Justification
- **One transaction matches the one source of truth.** The sentinel + `dek_epoch` pair is a
  single global fact about "what DEK is current." Every row's wrap state must move in lockstep
  with that fact, or the fact becomes a lie for whichever rows didn't move. A single transaction
  is the direct way to guarantee that.
- **Rollback is the resumability mechanism, not application-level checkpointing.** On any
  exception the transaction rolls back and the old DEK remains active and fully consistent
  end-to-end; per the `AUDIT NOTE` in `DekRotationService.Accept.cs`, retry simply means running
  a new Propose+Accept cycle. There is no partial-progress state to reconcile because none was
  allowed to exist.
- **The one-transaction constraint still had to be made to perform.** `ReWrapTableAsync` uses
  keyset pagination (`WHERE pk > @lastPk ORDER BY pk LIMIT 500`) rather than `OFFSET`, specifically
  so the rewrap of each table stays O(n) instead of O(n²) *within* the single transaction — a
  performance concession made in service of keeping the atomicity model simple, not a
  compromise of it.

Separately, `AcceptCommitCoreAsync` (`DekRotationService.Accept.cs`) takes a pre-rotation
snapshot (`snapshotService.CreateAsync(...)`) *before* the rewrap transaction runs. This is the
actual recovery mechanism for a rotation that needs to be undone after the fact — the
transaction guarantees the rewrap itself is atomic, but it cannot undo a rotation that already
committed successfully and only later turns out to have been the wrong call (e.g. discovered
after the fact to be based on a mistaken decision). Notably, the snapshot is deleted only on the
*failure* path (to avoid every failed retry leaving a `~DBsize` archive on disk); on success it
is left in place, precisely because a successful commit is the case where a human might later
need to fall back to the prior state.

## Consequences and Trade-offs
- DEK rotation is not incremental: it holds one open write transaction for the full duration of
  the rewrap, gated behind `HeavyOperationLock` and maintenance mode so no other write can
  interleave. On a very large database this means a correspondingly long-held transaction; the
  design accepts that cost in exchange for removing the undetectable-partial-state failure mode.
- A crash mid-rewrap costs a full re-run of the batch loop from scratch (via a fresh Accept
  call) rather than resuming from a checkpoint — an acceptable cost given that the alternative
  is an unrecoverable mixed-DEK database.
- The pre-rotation snapshot is the only supported way to walk back a rotation that committed
  successfully; there is no in-place "undo rotation" operation once the transaction has
  committed and the in-memory DEK has been swapped.
