-- Migration 012: Add media table for encrypted image storage
-- Images are stored as encrypted files on disk ({dataPath}/media/{id}.enc)
-- This table holds metadata and key material only

CREATE TABLE IF NOT EXISTS tbl_media (
    id              TEXT PRIMARY KEY,
    article_id      TEXT REFERENCES tbl_article(id),
    file_name       TEXT NOT NULL,
    content_type    TEXT NOT NULL,
    file_size       INTEGER NOT NULL,
    encrypted_dek   BLOB NOT NULL,
    dek_iv          BLOB NOT NULL,
    iv              BLOB NOT NULL,
    status          TEXT NOT NULL DEFAULT 'A',
    lamport_ts      INTEGER NOT NULL DEFAULT 0,
    source_node_id  TEXT,
    created_at      TEXT NOT NULL,
    deleted_at      TEXT
);

CREATE INDEX IF NOT EXISTS idx_media_article_id ON tbl_media(article_id);
CREATE INDEX IF NOT EXISTS idx_media_status ON tbl_media(status);
