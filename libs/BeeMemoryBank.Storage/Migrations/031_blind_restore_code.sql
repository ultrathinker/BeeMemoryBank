-- Blind nodes (BMB-43, plan 6.7): one-time restore codes issued on a blind node. A code lets a new
-- device download the recovery package and claim the blind node (become its local superadmin), so
-- only its SHA-256 is stored. Rows are never deleted: the table is also the log of every issue, which
-- the blind status page and Windows show. Node-local, never replicated.
CREATE TABLE IF NOT EXISTS tbl_blind_restore_code (
    code_hash         TEXT    NOT NULL PRIMARY KEY,
    issued_at         TEXT    NOT NULL,
    expires_at        TEXT    NOT NULL,
    issued_by         TEXT,
    used_at           TEXT,
    claimed_node_id   TEXT,
    failed_attempts   INTEGER NOT NULL DEFAULT 0,
    revoked           INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_blind_restore_code_issued ON tbl_blind_restore_code (issued_at DESC);
