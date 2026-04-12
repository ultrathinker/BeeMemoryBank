-- Add Lamport clock and source_node_id to articles
ALTER TABLE tbl_article ADD COLUMN lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_article ADD COLUMN source_node_id TEXT;

-- Event log: log of all changes on this node
CREATE TABLE tbl_event (
    sequence_num    INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id        TEXT NOT NULL UNIQUE,
    node_id         TEXT NOT NULL,
    lamport_ts      INTEGER NOT NULL,
    event_type      TEXT NOT NULL,   -- 'article_create','article_update','article_delete','whitelist_add','whitelist_revoke'
    article_id      TEXT,
    payload         TEXT NOT NULL,   -- JSON with event fields
    signature       BLOB NOT NULL,   -- Ed25519 signature
    protocol_version INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL
);

-- Sync positions: how much we know about each remote node
CREATE TABLE tbl_sync_position (
    remote_node_id      TEXT PRIMARY KEY,
    last_sequence_num   INTEGER NOT NULL,
    updated_at          TEXT NOT NULL
);

-- Tombstones for deleted articles (kept for 60 days)
CREATE TABLE tbl_tombstone (
    article_id  TEXT PRIMARY KEY,
    created_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL
);

-- Conflict versions repository (conflict versions, 7 days)
CREATE INDEX IF NOT EXISTS idx_event_node_seq ON tbl_event(node_id, sequence_num);
CREATE INDEX IF NOT EXISTS idx_event_lamport_ts ON tbl_event(lamport_ts);
CREATE INDEX IF NOT EXISTS idx_tombstone_expires ON tbl_tombstone(expires_at);
