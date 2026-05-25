-- Phase 3: Remote Account + read-only zerkалирование.
-- Friend-side: tables to register remote BMB nodes ("Remote Accounts") and
-- per-device subscriptions to specific folders on those nodes.
-- Owner-side: long-lived bearer tokens issued for cross-instance read access.

CREATE TABLE tbl_remote_account (
    id                TEXT PRIMARY KEY,           -- local GUID
    display_name      TEXT NOT NULL,              -- "Alice's BMB"
    base_url          TEXT NOT NULL,              -- "https://her-node.example"
    remote_username   TEXT NOT NULL,              -- login on owner-node
    encrypted_token   BLOB NOT NULL,              -- bearer token, wrapped with our master DEK
    token_iv          BLOB NOT NULL,
    token_expires_at  TEXT,                       -- ISO-8601 informational
    last_sync_at      TEXT,
    last_sync_status  TEXT,                       -- 'ok' | 'auth_failed' | 'unreachable' | 'error'
    last_error        TEXT,
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);

CREATE INDEX idx_remote_account_url ON tbl_remote_account(base_url);

CREATE TABLE tbl_remote_subscription (
    id                  TEXT PRIMARY KEY,
    remote_account_id   TEXT NOT NULL REFERENCES tbl_remote_account(id) ON DELETE CASCADE,
    remote_folder_id    TEXT NOT NULL,            -- folder GUID on owner-node
    remote_folder_path  TEXT NOT NULL,            -- path on owner-node, for UI display
    mount_path          TEXT NOT NULL,            -- local path where the replica is rooted
    sync_cursor         TEXT,                     -- last applied event token for /changes
    last_full_sync_at   TEXT,
    created_at          TEXT NOT NULL,
    UNIQUE(remote_account_id, remote_folder_id),
    UNIQUE(mount_path)
);

CREATE INDEX idx_remote_subscription_account ON tbl_remote_subscription(remote_account_id);

-- Shadow markers on existing tables: rows with remote_subscription_id != NULL
-- belong to a mirrored remote share and are read-only at the repository layer
-- (FolderRepository / ArticleRepository write-guards refuse mutations).
ALTER TABLE tbl_folder  ADD COLUMN remote_subscription_id TEXT;
ALTER TABLE tbl_folder  ADD COLUMN remote_origin_id       TEXT;
ALTER TABLE tbl_article ADD COLUMN remote_subscription_id TEXT;
ALTER TABLE tbl_article ADD COLUMN remote_origin_id       TEXT;
ALTER TABLE tbl_article ADD COLUMN remote_version         INTEGER;
ALTER TABLE tbl_article ADD COLUMN remote_updated_by      TEXT;

CREATE INDEX idx_folder_remote_sub  ON tbl_folder(remote_subscription_id);
CREATE INDEX idx_article_remote_sub ON tbl_article(remote_subscription_id);

-- Owner-side: tokens issued to remote accounts.
CREATE TABLE tbl_remote_api_token (
    id            TEXT PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    token_hash    TEXT NOT NULL,                  -- SHA-256 hex
    label         TEXT,
    created_at    TEXT NOT NULL,
    last_used_at  TEXT,
    expires_at    TEXT NOT NULL                   -- ISO-8601
);

CREATE UNIQUE INDEX idx_remote_api_token_hash ON tbl_remote_api_token(token_hash);
CREATE INDEX idx_remote_api_token_user ON tbl_remote_api_token(user_id);
