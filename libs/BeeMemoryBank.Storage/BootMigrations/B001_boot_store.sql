-- The boot store of a sealed node (docs/design/step-b-spec.md 2.3): the one plain file a sealed node reads before unlock.
-- Every table and column is on the allow-list docs/design/boot-store.golden.txt. It holds wrapped keys exactly as exposed as
-- today's key slots, the database-key wrappers, the public identity, the whitelist (peer names, addresses and TLS pins: the
-- residual ADR 0009 accepts) and what arrived while the database was closed (sealed or control events, ciphertext blobs).
-- Never: a user name, a password hash, an agent name, a title, a path, a tag, a decrypted payload, a clear content event.

CREATE TABLE IF NOT EXISTS tbl_boot_meta (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

-- Doors (spec 4.1): copies of tbl_key_slot rows and of the wrapped DEK of auto-unlock agents. Derived; rewritten from the
-- sealed database (spec 4.3). Several rows per source while a replacement is pending.
CREATE TABLE IF NOT EXISTS tbl_boot_door (
    door_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    kind                 TEXT    NOT NULL CHECK (kind IN ('slot', 'agent')),
    source_id            INTEGER NOT NULL,
    slot_type            TEXT,
    encrypted_master_dek BLOB    NOT NULL,
    iv                   BLOB    NOT NULL,
    salt                 BLOB,
    argon_memory         INTEGER,
    argon_iterations     INTEGER,
    argon_parallelism    INTEGER,
    key_hash             TEXT,
    kdf_version          INTEGER,
    content_sha256       TEXT    NOT NULL,
    dek_fp               TEXT,
    pending              INTEGER NOT NULL DEFAULT 0,
    written_at           TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_boot_door ON tbl_boot_door (kind, source_id, content_sha256);
CREATE INDEX IF NOT EXISTS ix_boot_door_key_hash ON tbl_boot_door (key_hash) WHERE key_hash IS NOT NULL;

-- The database key wrapped under one master DEK per row (spec 3.3).
CREATE TABLE IF NOT EXISTS tbl_boot_db_key (
    dek_fp         TEXT    NOT NULL,
    dbk_generation INTEGER NOT NULL,
    wrapped        BLOB    NOT NULL,
    iv             BLOB    NOT NULL,
    created_at     TEXT    NOT NULL,
    PRIMARY KEY (dek_fp, dbk_generation)
);

-- The public part of this node's identity, for /api/sync/identity while closed (spec 6.1).
CREATE TABLE IF NOT EXISTS tbl_boot_identity (
    node_id            TEXT NOT NULL PRIMARY KEY,
    display_name       TEXT NOT NULL,
    ed25519_public_key BLOB NOT NULL,
    updated_at         TEXT NOT NULL
);

-- Mirror of the vault's tbl_whitelist, same columns (a test keeps them equal), so WhitelistRepository reads it unchanged.
-- Status 'S' exists only here: suspended by a revoke or update received while closed (spec 6.4).
CREATE TABLE IF NOT EXISTS tbl_whitelist (
    node_id                   TEXT PRIMARY KEY,
    display_name              TEXT NOT NULL,
    ed25519_public_key        BLOB NOT NULL,
    api_address               TEXT,
    can_generate_embeddings   INTEGER NOT NULL DEFAULT 0,
    status                    TEXT NOT NULL DEFAULT 'A',
    auto_accept_restore       INTEGER NOT NULL DEFAULT 0,
    auto_accept_dek_rotation  INTEGER NOT NULL DEFAULT 0,
    is_superadmin             INTEGER NOT NULL DEFAULT 0,
    created_at                TEXT NOT NULL,
    updated_at                TEXT NOT NULL,
    deleted_at                TEXT,
    lamport_ts                INTEGER NOT NULL DEFAULT 0,
    source_node_id            TEXT,
    last_protocol_version     INTEGER,
    last_protocol_seen_at     TEXT,
    tls_spki                  TEXT,
    tls_trust                 TEXT,
    last_capabilities         TEXT,
    last_app_version          TEXT,
    last_capabilities_seen_at TEXT
);

-- Events received while the database was closed (spec 6.2), served with this node's sequence numbers (spec 6.3) and replayed
-- at open (spec 6.6). Only 'sealed' and control events: a clear content event is refused before it gets here.
CREATE TABLE IF NOT EXISTS tbl_relay_event (
    relay_seq        INTEGER PRIMARY KEY,
    event_id         TEXT    NOT NULL UNIQUE,
    node_id          TEXT    NOT NULL,
    lamport_ts       INTEGER NOT NULL,
    event_type       TEXT    NOT NULL,
    article_id       TEXT,
    payload          TEXT    NOT NULL,
    signature        BLOB    NOT NULL,
    protocol_version INTEGER NOT NULL,
    created_at       TEXT    NOT NULL,
    origin_peer      TEXT,
    origin_seq       INTEGER,
    received_at      TEXT    NOT NULL,
    bytes            INTEGER NOT NULL
);

-- Ciphertext blobs received while closed (spec 6.5), moved into tbl_blob at open.
CREATE TABLE IF NOT EXISTS tbl_relay_blob (
    hash        TEXT    NOT NULL PRIMARY KEY,
    data        BLOB    NOT NULL,
    size        INTEGER NOT NULL,
    received_at TEXT    NOT NULL
);

-- Positions peers reported on this node's log while closed (spec 6.1), merged at open.
CREATE TABLE IF NOT EXISTS tbl_relay_report (
    remote_node_id TEXT    NOT NULL PRIMARY KEY,
    reported_seq   INTEGER NOT NULL,
    updated_at     TEXT    NOT NULL
);
