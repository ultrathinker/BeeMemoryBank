CREATE TABLE IF NOT EXISTS tbl_agent (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    name                TEXT NOT NULL,
    description         TEXT,
    key_prefix          TEXT NOT NULL,
    key_hash            TEXT NOT NULL UNIQUE,
    encrypted_dek       BLOB NOT NULL,
    dek_iv              BLOB NOT NULL,
    status              TEXT NOT NULL DEFAULT 'A',
    created_at          TEXT NOT NULL
);
