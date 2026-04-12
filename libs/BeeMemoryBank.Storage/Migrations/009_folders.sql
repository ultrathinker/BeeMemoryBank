-- 009_folders.sql
-- Adds explicit folder table and folder_id column on articles.
-- Bootstrap (populating tbl_folder from existing tree_path values) is done
-- in C# (FolderBootstrapper) at startup, not here.

CREATE TABLE IF NOT EXISTS tbl_folder (
    id              TEXT PRIMARY KEY,
    path            TEXT NOT NULL UNIQUE,
    name            TEXT NOT NULL,
    parent_path     TEXT,
    status          TEXT NOT NULL DEFAULT 'A',
    lamport_ts      INTEGER NOT NULL DEFAULT 0,
    source_node_id  TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    deleted_at      TEXT
);

CREATE INDEX IF NOT EXISTS idx_folder_path        ON tbl_folder(path);
CREATE INDEX IF NOT EXISTS idx_folder_parent_path ON tbl_folder(parent_path);
CREATE INDEX IF NOT EXISTS idx_folder_status      ON tbl_folder(status);

ALTER TABLE tbl_article ADD COLUMN folder_id TEXT REFERENCES tbl_folder(id);

CREATE INDEX IF NOT EXISTS idx_article_folder_id ON tbl_article(folder_id)
