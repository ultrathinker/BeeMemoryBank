-- Blind nodes (BMB-43), stage 0. Two unrelated local additions: the protocol each peer last spoke
-- (plan 3.1) and a blind node's own flags (plan 3.4, 5.3).
--
-- 1. The sync protocol each peer last spoke to this node.
--
-- Protocol 3 is what makes blind nodes safe: a node on protocol 2 does not know the blind-node mark,
-- so it would accept events from a blind id and seal the master DEK for one in a rotation. A 3 node
-- never pushes to it and a 2 node never pulls from a 3 one, so the two simply stop talking — which
-- is only safe if nobody on 2 is still around when a blind node is added. The PC checks that before
-- whitelist_add; these columns are what it checks.
--
-- Written when a peer authenticates (/api/sync/authenticate) and reports its position
-- (/api/sync/report-position). NULL = never heard from since this build: a peer on an older build
-- sends no version at all, so "unknown" is itself the warning sign once it lasts.
--
-- Local observations, not replicated state: no event carries them and they are never compared
-- through ConflictResolver.

ALTER TABLE tbl_whitelist ADD COLUMN last_protocol_version INTEGER;
ALTER TABLE tbl_whitelist ADD COLUMN last_protocol_seen_at TEXT;

-- 2. Local state of a blind node (plan 3.4, 5.3). Key/value, because the handful of entries are
-- unrelated flags, and none of them is ever replicated: a blind node authors no events, and a blind
-- package (like every peer snapshot) leaves tables outside SnapshotTables.Replicated empty.
--   reseed_needed — set when a restore_network arrives: the blind node cannot take part in a
--                   network restore (it has no DEK to check the snapshot against), so it waits for
--                   the first superadmin full node to reseed it. Cleared by that reseed.
CREATE TABLE IF NOT EXISTS tbl_blind_state (
    key         TEXT NOT NULL PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
