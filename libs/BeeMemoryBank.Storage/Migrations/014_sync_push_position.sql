CREATE TABLE IF NOT EXISTS tbl_sync_push_position (
    remote_node_id    TEXT PRIMARY KEY,
    last_pushed_seq   INTEGER NOT NULL DEFAULT 0,
    pushed_at         TEXT NOT NULL
);
