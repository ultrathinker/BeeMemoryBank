-- Version columns for tbl_whitelist, the last replicated table that had none.
--
-- Every other replicated row carries (lamport_ts, source_node_id) and every applier gate compares
-- through ConflictResolver.IncomingWins. Whitelist add, revoke and update did not: they applied in
-- arrival order. Two consequences, both silent.
--
-- The first is a security one. ApplyWhitelistAddAsync reactivates a revoked peer ("if
-- existing.Status == 'R' ... Status = 'A'") with no check of WHEN that add was issued. A peer that
-- had been offline while an admin revoked a compromised node still holds an older whitelist_add for
-- it; when that peer finally syncs, the stale add arrives after the revoke and puts the node back
-- into the mesh. Nothing reports it, and the revoking admin's own UI shows the node active again
-- with no indication that their revoke was undone rather than never applied.
--
-- The second is ordinary divergence: two admins renaming the same peer, or changing its address,
-- resolve to whichever event happened to arrive last, so the nodes end up disagreeing about a row
-- neither of them will ever recompare.
--
-- LWW on (lamport_ts, source_node_id) fixes both, and it is the right rule for revoke too rather
-- than a special "revoke always wins": revoke has to be undoable, because re-adding a peer you
-- previously revoked is a real workflow the UI offers. What must not happen is an OLDER add
-- undoing it, which is exactly what plain LWW refuses.
--
-- DEFAULT 0 for lamport_ts and NULL for source_node_id: existing rows predate versioning, and
-- RowVersion.Of maps a null node id to Guid.Empty, which sorts below every real one. So a legacy
-- row loses to any attributed write — the right way round, since the attributed write is the one
-- that carries a decision someone actually made.

ALTER TABLE tbl_whitelist ADD COLUMN lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN source_node_id TEXT;
