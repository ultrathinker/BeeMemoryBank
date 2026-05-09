-- 001_initial_schema.sql
-- Squashed baseline schema (consolidates former 001..008).
-- On fresh DBs: creates the full current schema in one shot — no incremental migrations.
-- On existing DBs that already passed through 001..008: version=1 is in tbl_migration, so
-- this file is skipped on startup. Ghost Hunter in MigrationRunner removes the now-stale
-- tbl_migration rows for versions 2..8 automatically (no code changes required).

-- Parent tables (no outgoing FKs) ----------------------------------------

CREATE TABLE tbl_folder (
    id                    TEXT PRIMARY KEY,
    path                  TEXT NOT NULL,
    name                  TEXT NOT NULL,
    parent_path           TEXT,
    status                TEXT NOT NULL DEFAULT 'A',
    lamport_ts            INTEGER NOT NULL DEFAULT 0,
    source_node_id        TEXT,
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL,
    deleted_at            TEXT,
    cascade_delete_op_id  TEXT
);

CREATE TABLE tbl_key_slot (
    slot_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    slot_type            TEXT NOT NULL,
    encrypted_master_dek BLOB NOT NULL,
    iv                   BLOB NOT NULL,
    salt                 BLOB,
    argon_memory         INTEGER,
    argon_iterations     INTEGER,
    argon_parallelism    INTEGER,
    created_at           TEXT NOT NULL
);

CREATE TABLE tbl_node_identity (
    node_id                 TEXT PRIMARY KEY,
    display_name            TEXT NOT NULL,
    ed25519_public_key      BLOB NOT NULL,
    -- v=0: legacy plaintext seed in ed25519_private_key (ed25519_private_key_iv NULL).
    -- v=1: master-DEK-wrapped seed in ed25519_private_key, IV in ed25519_private_key_iv,
    --      AAD = "bmb-node-pk" || node_id (16 bytes, big-endian Guid bytes).
    -- Encryption-on-init is wired in code; legacy rows are upgraded lazily.
    ed25519_private_key     BLOB NOT NULL,
    ed25519_private_key_iv  BLOB,
    ed25519_private_key_v   INTEGER NOT NULL DEFAULT 0,
    can_generate_embeddings INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL,
    sentinel_value          BLOB,
    initial_sync_completed  INTEGER NOT NULL DEFAULT 1,
    dek_epoch               INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE tbl_whitelist (
    node_id                  TEXT PRIMARY KEY,
    display_name             TEXT NOT NULL,
    ed25519_public_key       BLOB NOT NULL,
    api_address              TEXT,
    can_generate_embeddings  INTEGER NOT NULL DEFAULT 0,
    status                   TEXT NOT NULL DEFAULT 'A',
    auto_accept_restore      INTEGER NOT NULL DEFAULT 0,
    auto_accept_dek_rotation INTEGER NOT NULL DEFAULT 0,
    -- Authorises sync events that mutate cluster state (whitelist add/revoke,
    -- hard_delete, restore_network). Default 0 = regular peer; promote
    -- explicitly via UI / join flow.
    is_superadmin            INTEGER NOT NULL DEFAULT 0,
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL,
    deleted_at               TEXT
);

-- created_at format is locked to ISO-8601 "O" with trailing Z, otherwise
-- the audit-log pruning DELETE (which compares strings lexicographically against
-- DateTime.UtcNow.ToString("O")) would silently delete the wrong subset.
CREATE TABLE tbl_audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    action      TEXT NOT NULL,
    details     TEXT,
    actor_type  TEXT NOT NULL DEFAULT 'user',
    actor_id    TEXT,
    actor_name  TEXT,
    created_at  TEXT NOT NULL CHECK (
        created_at GLOB '????-??-??T??:??:??.???????Z'
    )
);

CREATE TABLE tbl_event (
    sequence_num     INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id         TEXT NOT NULL UNIQUE,
    node_id          TEXT NOT NULL,
    lamport_ts       INTEGER NOT NULL,
    event_type       TEXT NOT NULL,
    article_id       TEXT,
    payload          TEXT NOT NULL,
    signature        BLOB NOT NULL,
    protocol_version INTEGER NOT NULL DEFAULT 1,
    created_at       TEXT NOT NULL,
    actor_type       TEXT,
    actor_name       TEXT,
    via_agent_name   TEXT,
    entity_id        TEXT
);

CREATE TABLE tbl_sync_position (
    remote_node_id    TEXT PRIMARY KEY,
    last_sequence_num INTEGER NOT NULL,
    updated_at        TEXT NOT NULL
);

CREATE TABLE tbl_sync_push_position (
    remote_node_id  TEXT PRIMARY KEY,
    last_pushed_seq INTEGER NOT NULL DEFAULT 0,
    pushed_at       TEXT NOT NULL
);

CREATE TABLE tbl_tombstone (
    article_id     TEXT PRIMARY KEY,
    created_at     TEXT NOT NULL,
    expires_at     TEXT NOT NULL,
    -- LWW between Create-after-Delete and Delete-before-Create races.
    lamport_ts     INTEGER NOT NULL DEFAULT 0,
    -- LWW tiebreak when two peers delete with the same lamport_ts.
    source_node_id BLOB
);

CREATE TABLE tbl_projection_matrix (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    encrypted_matrix BLOB NOT NULL,
    iv               BLOB NOT NULL,
    created_at       TEXT NOT NULL
);

CREATE TABLE tbl_comment (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    article_id        TEXT NOT NULL,
    text              TEXT NOT NULL,
    created_at        TEXT NOT NULL,
    comment_id        TEXT,
    source_node_id    TEXT,
    ciphertext        BLOB,
    iv                BLOB,
    encrypted         INTEGER NOT NULL DEFAULT 0,
    lamport_ts        INTEGER NOT NULL DEFAULT 0,
    deleted_at        TEXT,
    delete_lamport_ts INTEGER,
    delete_node_id    TEXT
);

CREATE TABLE tbl_compaction_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    compacted_at        TEXT NOT NULL,
    cp_before           INTEGER,
    cp_after            INTEGER NOT NULL,
    events_removed      INTEGER NOT NULL,
    snapshot_file_name  TEXT,
    reason              TEXT
);

CREATE TABLE tbl_migration_marker (
    key      TEXT NOT NULL PRIMARY KEY,
    value    TEXT NOT NULL,
    set_at   TEXT NOT NULL
);

-- Snapshot restore (network-wide) state -----------------------------------

CREATE TABLE tbl_restore_replay_shield (
    peer_node_id                     TEXT NOT NULL PRIMARY KEY,
    ignore_events_before_lamport_ts  INTEGER NOT NULL,
    shield_event_id                  TEXT NOT NULL,
    created_at                       TEXT NOT NULL
);

CREATE TABLE tbl_restore_event_state (
    event_id          TEXT NOT NULL PRIMARY KEY,
    state             TEXT NOT NULL,
    superseded_by     TEXT,
    rejected_locally  INTEGER NOT NULL DEFAULT 0,
    applied_at        TEXT,
    error_message     TEXT,
    -- Counts repeated hash mismatches when retrying snapshot fetches so
    -- persistently corrupt events can be marked rejected instead of looping.
    hash_fail_count   INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);

-- DEK rotation state ------------------------------------------------------

CREATE TABLE tbl_dek_rotation_state (
    event_id              TEXT NOT NULL PRIMARY KEY,
    state                 TEXT NOT NULL,
    proposed_event_id     TEXT,
    rotation_ts           TEXT NOT NULL,
    applied_at            TEXT,
    error_message         TEXT,
    last_processed_id_article          INTEGER,
    last_processed_id_article_version  INTEGER,
    last_processed_id_media            INTEGER,
    last_processed_id_conflict_version INTEGER,
    last_processed_id_comment          INTEGER,
    created_at            TEXT NOT NULL,
    updated_at            TEXT NOT NULL
);

-- tbl_user must precede tbl_agent (agent has FK -> user) ------------------

CREATE TABLE tbl_user (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name  TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role          TEXT NOT NULL DEFAULT 'user',
    key_slot_id   INTEGER,
    is_active     INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    last_login_at TEXT
);

CREATE TABLE tbl_agent (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL,
    description      TEXT,
    key_prefix       TEXT NOT NULL,
    key_hash         TEXT NOT NULL UNIQUE,
    encrypted_dek    BLOB NOT NULL,
    dek_iv           BLOB NOT NULL,
    -- KDF version: 0 = legacy SHA256, 1 = HKDF-SHA256 with per-agent salt
    -- (`salt` column below). New agents always use v=1; v=0 still verifies on
    -- read for back-compat with agents created before the migration.
    kdf_version      INTEGER NOT NULL DEFAULT 0,
    salt             BLOB,
    status           TEXT NOT NULL DEFAULT 'A',
    created_at       TEXT NOT NULL,
    last_accessed_at TEXT,
    request_count    INTEGER NOT NULL DEFAULT 0,
    owner_user_id    INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE RESTRICT
);

-- Tables depending on tbl_user -------------------------------------------

CREATE TABLE tbl_folder_acl_entry (
    user_id   INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    folder_id TEXT    NOT NULL REFERENCES tbl_folder(id) ON DELETE CASCADE,
    effect    TEXT    NOT NULL CHECK(effect IN ('allow', 'deny')),
    created_at TEXT   NOT NULL,
    PRIMARY KEY(user_id, folder_id, effect)
);

-- Tables depending on tbl_folder -----------------------------------------

CREATE TABLE tbl_article (
    id                      TEXT PRIMARY KEY,
    title                   TEXT NOT NULL,
    tree_path               TEXT NOT NULL,
    embedding_projection    BLOB,
    embedding_model_version TEXT,
    embedding_pending       INTEGER NOT NULL DEFAULT 1,
    status                  TEXT NOT NULL DEFAULT 'A',
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    deleted_at              TEXT,
    lamport_ts              INTEGER NOT NULL DEFAULT 0,
    source_node_id          TEXT,
    folder_id               TEXT REFERENCES tbl_folder(id)
);

-- Tables depending on tbl_article ----------------------------------------

CREATE TABLE tbl_concept_tag (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    name                    TEXT NOT NULL UNIQUE,
    created_at              TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    embedding               BLOB,
    embedding_model_version TEXT
);

CREATE TABLE tbl_article_concept_tag (
    article_id     TEXT NOT NULL REFERENCES tbl_article(id),
    concept_tag_id INTEGER NOT NULL REFERENCES tbl_concept_tag(id),
    PRIMARY KEY (article_id, concept_tag_id)
);

CREATE TABLE tbl_concept_tag_edge (
    tag_id_a   INTEGER NOT NULL REFERENCES tbl_concept_tag(id) ON DELETE CASCADE,
    tag_id_b   INTEGER NOT NULL REFERENCES tbl_concept_tag(id) ON DELETE CASCADE,
    article_id TEXT    NOT NULL REFERENCES tbl_article(id)     ON DELETE CASCADE,
    PRIMARY KEY (tag_id_a, tag_id_b, article_id),
    CHECK (tag_id_a < tag_id_b)
) WITHOUT ROWID;

CREATE TABLE tbl_article_body (
    article_id    TEXT PRIMARY KEY REFERENCES tbl_article(id),
    ciphertext    BLOB NOT NULL,
    iv            BLOB NOT NULL,
    encrypted_dek BLOB NOT NULL,
    dek_iv        BLOB NOT NULL
);

CREATE TABLE tbl_article_version (
    id             TEXT PRIMARY KEY,
    article_id     TEXT NOT NULL,
    version_number INTEGER NOT NULL,
    title          TEXT NOT NULL,
    tree_path      TEXT NOT NULL,
    ciphertext     BLOB NOT NULL,
    iv             BLOB NOT NULL,
    encrypted_dek  BLOB NOT NULL,
    dek_iv         BLOB NOT NULL,
    created_at     TEXT NOT NULL,
    updated_by     TEXT,
    FOREIGN KEY (article_id) REFERENCES tbl_article(id)
);

CREATE TABLE tbl_conflict_version (
    id             TEXT PRIMARY KEY,
    article_id     TEXT NOT NULL REFERENCES tbl_article(id),
    source_node_id TEXT NOT NULL,
    lamport_ts     INTEGER NOT NULL,
    ciphertext     BLOB NOT NULL,
    iv             BLOB NOT NULL,
    encrypted_dek  BLOB NOT NULL,
    dek_iv         BLOB NOT NULL,
    created_at     TEXT NOT NULL,
    expires_at     TEXT NOT NULL,
    metadata_json  TEXT
);

CREATE TABLE tbl_media (
    id             TEXT PRIMARY KEY,
    article_id     TEXT REFERENCES tbl_article(id),
    file_name      TEXT NOT NULL,
    content_type   TEXT NOT NULL,
    file_size      INTEGER NOT NULL,
    encrypted_dek  BLOB NOT NULL,
    dek_iv         BLOB NOT NULL,
    iv             BLOB NOT NULL,
    status         TEXT NOT NULL DEFAULT 'A',
    lamport_ts     INTEGER NOT NULL DEFAULT 0,
    source_node_id TEXT,
    created_at     TEXT NOT NULL,
    deleted_at     TEXT
);

CREATE TABLE tbl_hard_delete_audit (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at       TEXT NOT NULL,
    user_id           INTEGER,
    agent_id          INTEGER,
    source_node_id    TEXT,
    entity_type       TEXT NOT NULL,
    entity_identifier TEXT NOT NULL,
    entity_title      TEXT,
    deleted_articles  INTEGER NOT NULL DEFAULT 0,
    deleted_folders   INTEGER NOT NULL DEFAULT 0,
    deleted_media     INTEGER NOT NULL DEFAULT 0,
    -- Lets the IsHardDeletedAsync gate survive event-log compaction (which
    -- purges old hard_delete events from tbl_event). Old rows default to 0.
    lamport_ts        INTEGER NOT NULL DEFAULT 0
);


-- Indexes ----------------------------------------------------------------

CREATE UNIQUE INDEX idx_folder_path_active  ON tbl_folder(path) WHERE status = 'A';
CREATE INDEX idx_folder_path                ON tbl_folder(path);
CREATE INDEX idx_folder_parent_path         ON tbl_folder(parent_path);
CREATE INDEX idx_folder_status              ON tbl_folder(status);
CREATE INDEX idx_folder_parent_status       ON tbl_folder(parent_path, status) WHERE status = 'A';
CREATE INDEX idx_folder_cascade_op          ON tbl_folder(cascade_delete_op_id) WHERE cascade_delete_op_id IS NOT NULL;

CREATE INDEX idx_article_tree_path          ON tbl_article(tree_path);
CREATE INDEX idx_article_status             ON tbl_article(status);
CREATE INDEX idx_article_folder_id          ON tbl_article(folder_id);
CREATE INDEX idx_article_updated_active     ON tbl_article(updated_at DESC) WHERE status = 'A';
CREATE INDEX idx_article_embedding_pending  ON tbl_article(id) WHERE status = 'A' AND embedding_pending = 1;

CREATE UNIQUE INDEX ix_concept_tag_name_nocase ON tbl_concept_tag(name COLLATE NOCASE);
CREATE INDEX ix_act_concept_tag_id          ON tbl_article_concept_tag(concept_tag_id);
CREATE INDEX ix_concept_tag_edge_pair       ON tbl_concept_tag_edge(tag_id_a, tag_id_b);
CREATE INDEX ix_concept_tag_edge_article    ON tbl_concept_tag_edge(article_id);
CREATE INDEX ix_concept_tag_edge_tag_b      ON tbl_concept_tag_edge(tag_id_b, tag_id_a);

CREATE UNIQUE INDEX idx_article_version_article ON tbl_article_version(article_id, version_number DESC);

CREATE INDEX idx_conflict_version_article   ON tbl_conflict_version(article_id);
CREATE INDEX idx_conflict_version_expires   ON tbl_conflict_version(expires_at);

CREATE INDEX idx_media_article_id           ON tbl_media(article_id);
CREATE INDEX idx_media_status               ON tbl_media(status);
CREATE INDEX idx_media_article_active       ON tbl_media(article_id, status) WHERE status = 'A';

CREATE INDEX ix_audit_log_created_at        ON tbl_audit_log(created_at);

CREATE INDEX idx_event_node_seq             ON tbl_event(node_id, sequence_num);
CREATE INDEX idx_event_lamport_ts           ON tbl_event(lamport_ts);
CREATE INDEX idx_event_article_id           ON tbl_event(article_id);
CREATE INDEX idx_event_hard_delete_entity   ON tbl_event(event_type, entity_id) WHERE event_type = 'hard_delete';
-- General type-only filter (audit reports, EventLogRepository.GetRecentAsync).
-- The partial idx_event_hard_delete_entity above can't serve generic type filters.
CREATE INDEX idx_event_type                 ON tbl_event(event_type);

CREATE INDEX idx_tombstone_expires          ON tbl_tombstone(expires_at);

CREATE INDEX idx_tbl_comment_article        ON tbl_comment(article_id);
CREATE UNIQUE INDEX idx_comment_comment_id  ON tbl_comment(comment_id) WHERE comment_id IS NOT NULL;
CREATE INDEX idx_comment_article_active_created ON tbl_comment(article_id, created_at) WHERE deleted_at IS NULL;

CREATE INDEX idx_folder_acl_entry_user      ON tbl_folder_acl_entry(user_id);
CREATE INDEX idx_folder_acl_entry_folder    ON tbl_folder_acl_entry(folder_id);

CREATE INDEX idx_hard_delete_audit_occurred_at ON tbl_hard_delete_audit(occurred_at);
-- IsHardDeletedAsync gate lookup by entity + lamport_ts (post-compaction safe).
CREATE INDEX idx_hard_delete_audit_entity      ON tbl_hard_delete_audit(entity_identifier, lamport_ts);

CREATE INDEX idx_compaction_log_date        ON tbl_compaction_log(compacted_at DESC);

CREATE INDEX idx_restore_replay_shield_peer ON tbl_restore_replay_shield(peer_node_id);
CREATE INDEX idx_restore_event_state_state  ON tbl_restore_event_state(state);

CREATE INDEX idx_dek_rotation_state_state   ON tbl_dek_rotation_state(state);


-- Triggers ---------------------------------------------------------------

-- Keep tbl_concept_tag_edge in sync on tag attach
CREATE TRIGGER trg_act_insert_edge
AFTER INSERT ON tbl_article_concept_tag
BEGIN
    INSERT OR IGNORE INTO tbl_concept_tag_edge(tag_id_a, tag_id_b, article_id)
    SELECT
        IIF(NEW.concept_tag_id < other.concept_tag_id, NEW.concept_tag_id, other.concept_tag_id),
        IIF(NEW.concept_tag_id > other.concept_tag_id, NEW.concept_tag_id, other.concept_tag_id),
        NEW.article_id
    FROM tbl_article_concept_tag other
    WHERE other.article_id = NEW.article_id
      AND other.concept_tag_id <> NEW.concept_tag_id;
END;

-- Keep tbl_concept_tag_edge in sync on tag detach
CREATE TRIGGER trg_act_delete_edge
AFTER DELETE ON tbl_article_concept_tag
BEGIN
    DELETE FROM tbl_concept_tag_edge
    WHERE article_id = OLD.article_id
      AND (tag_id_a = OLD.concept_tag_id OR tag_id_b = OLD.concept_tag_id);
END;
