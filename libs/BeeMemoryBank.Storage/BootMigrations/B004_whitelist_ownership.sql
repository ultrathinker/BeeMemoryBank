-- The boot store's whitelist mirror keeps every column of the vault's tbl_whitelist (docs/design/step-b-spec.md 2.3): vault migration 047
-- added the ownership concern columns (ownership, owner_person_id, owner_label, ownership_lamport_ts, ownership_source_node_id, needs_review).
-- B003 is reserved for a parallel track.

ALTER TABLE tbl_whitelist ADD COLUMN ownership TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE tbl_whitelist ADD COLUMN owner_person_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN owner_label TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN ownership_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN ownership_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN needs_review INTEGER NOT NULL DEFAULT 0;
