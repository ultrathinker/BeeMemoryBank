CREATE TABLE IF NOT EXISTS tbl_metadata_key (
    key_id          TEXT    NOT NULL PRIMARY KEY,   -- 32 lowercase hex, random
    generation      INTEGER NOT NULL,               -- 1 at activation, +1 per DEK rotation
    dek_fingerprint TEXT    NOT NULL,               -- DekFingerprint.Of(the master DEK it is wrapped under)
    epoch_hint      INTEGER NOT NULL DEFAULT 0,     -- tbl_node_identity.dek_epoch of the author, for sorting/UI only
    wrapped         BLOB    NOT NULL,               -- AES-256-GCM ciphertext || tag of the 32-byte MEK
    iv              BLOB    NOT NULL,               -- 12 bytes
    author_node_id  TEXT    NOT NULL,
    created_at      TEXT    NOT NULL,
    lamport_ts      INTEGER NOT NULL DEFAULT 0,
    event_id        TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_metadata_key_generation ON tbl_metadata_key (generation DESC, key_id);

CREATE TABLE IF NOT EXISTS tbl_metadata_activation (
    activation_id   TEXT    NOT NULL PRIMARY KEY,
    key_id          TEXT    NOT NULL,
    generation      INTEGER NOT NULL,
    activated_at    TEXT    NOT NULL,
    author_node_id  TEXT    NOT NULL,
    event_id        TEXT    NOT NULL,
    lamport_ts      INTEGER NOT NULL,
    applied_at      TEXT    NOT NULL
);

ALTER TABLE tbl_whitelist ADD COLUMN last_capabilities TEXT;          -- comma-separated, local observation
ALTER TABLE tbl_whitelist ADD COLUMN last_app_version TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN last_capabilities_seen_at TEXT;
