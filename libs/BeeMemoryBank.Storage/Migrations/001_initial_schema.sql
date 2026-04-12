-- Article metadata (public layer)
-- Similar to tbl_article from v1, but without content (body is encrypted separately)
CREATE TABLE tbl_article (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    tree_path TEXT NOT NULL,
    embedding_projection BLOB,
    embedding_model_version TEXT,
    embedding_pending INTEGER NOT NULL DEFAULT 1,
    status TEXT NOT NULL DEFAULT 'A',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);

-- Tags (like tbl_tag in v1)
CREATE TABLE tbl_tag (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE
);

-- Article-tag relation (like tbl_article_tag in v1)
CREATE TABLE tbl_article_tag (
    article_id TEXT NOT NULL REFERENCES tbl_article(id),
    tag_id INTEGER NOT NULL REFERENCES tbl_tag(id),
    PRIMARY KEY (article_id, tag_id)
);

-- Encrypted article bodies
CREATE TABLE tbl_article_body (
    article_id TEXT PRIMARY KEY REFERENCES tbl_article(id),
    ciphertext BLOB NOT NULL,
    iv BLOB NOT NULL,
    encrypted_dek BLOB NOT NULL,
    dek_iv BLOB NOT NULL
);

-- Master DEK access slots (LUKS-style)
CREATE TABLE tbl_key_slot (
    slot_id INTEGER PRIMARY KEY AUTOINCREMENT,
    slot_type TEXT NOT NULL,
    encrypted_master_dek BLOB NOT NULL,
    iv BLOB NOT NULL,
    salt BLOB,
    argon_memory INTEGER,
    argon_iterations INTEGER,
    argon_parallelism INTEGER,
    created_at TEXT NOT NULL
);

-- This node's identity
CREATE TABLE tbl_node_identity (
    node_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    ed25519_public_key BLOB NOT NULL,
    ed25519_private_key BLOB NOT NULL,
    can_generate_embeddings INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

-- Whitelist of trusted nodes
CREATE TABLE tbl_whitelist (
    node_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    ed25519_public_key BLOB NOT NULL,
    api_address TEXT,
    can_generate_embeddings INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'A',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);

-- Conflict versions (kept for 7 days)
CREATE TABLE tbl_conflict_version (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL REFERENCES tbl_article(id),
    source_node_id TEXT NOT NULL,
    lamport_ts INTEGER NOT NULL,
    ciphertext BLOB NOT NULL,
    iv BLOB NOT NULL,
    encrypted_dek BLOB NOT NULL,
    dek_iv BLOB NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

-- Audit log (like tbl_audit_log in v1)
CREATE TABLE tbl_audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    action TEXT NOT NULL,
    details TEXT,
    actor_type TEXT NOT NULL DEFAULT 'user',
    actor_id TEXT,
    actor_name TEXT,
    created_at TEXT NOT NULL
);

CREATE INDEX idx_article_tree_path ON tbl_article(tree_path);
CREATE INDEX idx_article_status ON tbl_article(status);
CREATE INDEX idx_conflict_version_article ON tbl_conflict_version(article_id);
CREATE INDEX idx_conflict_version_expires ON tbl_conflict_version(expires_at);
CREATE INDEX idx_audit_log_entity ON tbl_audit_log(entity_type, entity_id)
