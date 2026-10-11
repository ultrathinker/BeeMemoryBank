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
