-- 040_baseline.sql
--
-- The schema of Bee Memory Bank at migration 040, the last of the 39 migrations of 2.6.0 and 2.6.1. From 2.6.2 on this is the only
-- migration file: a new database gets it once, a database that already ran the old 39 is adopted without running anything
-- (MigrationRunner / LegacyChainAdoption). Migrations after it are numbered 041 and up.
--
-- GENERATED from a database built by the old chain (tests/BeeMemoryBank.Storage.Tests/LegacyChain/BaselineGenerator.cs;
-- BMB_UPDATE_BASELINE=1 rewrites this file) and kept exactly that by BaselineTests. Do not edit by hand.
-- Order: tables, the rows the old migrations seeded, full-text tables, indexes, views, triggers.

-- table tbl_folder
CREATE TABLE tbl_folder (
    id                      TEXT PRIMARY KEY,
    path                    TEXT NOT NULL,
    name                    TEXT NOT NULL,
    parent_path             TEXT,
    status                  TEXT NOT NULL DEFAULT 'A',
    lamport_ts              INTEGER NOT NULL DEFAULT 0,
    source_node_id          TEXT,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    deleted_at              TEXT,
    cascade_delete_op_id    TEXT,
    is_system               INTEGER NOT NULL DEFAULT 0,
    remote_subscription_id  TEXT,
    remote_origin_id        TEXT
);

-- table tbl_key_slot
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

-- table tbl_node_identity
CREATE TABLE tbl_node_identity (
    node_id                 TEXT PRIMARY KEY,
    display_name            TEXT NOT NULL,
    ed25519_public_key      BLOB NOT NULL,
    ed25519_private_key     BLOB NOT NULL,
    ed25519_private_key_iv  BLOB,
    ed25519_private_key_v   INTEGER NOT NULL DEFAULT 0,
    can_generate_embeddings INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT NOT NULL,
    sentinel_value          BLOB,
    initial_sync_completed  INTEGER NOT NULL DEFAULT 1,
    dek_epoch               INTEGER NOT NULL DEFAULT 1
, session_expire_hours INTEGER NOT NULL DEFAULT 48, session_sliding_expiration INTEGER NOT NULL DEFAULT 1, brand_name TEXT, master_password_changed_elsewhere_at TEXT, master_password_changed_by_node      TEXT, master_password_changed_locally_at TEXT);

-- table tbl_whitelist
CREATE TABLE tbl_whitelist (
    node_id                  TEXT PRIMARY KEY,
    display_name             TEXT NOT NULL,
    ed25519_public_key       BLOB NOT NULL,
    api_address              TEXT,
    can_generate_embeddings  INTEGER NOT NULL DEFAULT 0,
    status                   TEXT NOT NULL DEFAULT 'A',
    auto_accept_restore      INTEGER NOT NULL DEFAULT 0,
    auto_accept_dek_rotation INTEGER NOT NULL DEFAULT 0,
    is_superadmin            INTEGER NOT NULL DEFAULT 0,
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL,
    deleted_at               TEXT
, lamport_ts INTEGER NOT NULL DEFAULT 0, source_node_id TEXT, last_protocol_version INTEGER, last_protocol_seen_at TEXT, tls_spki TEXT, tls_trust TEXT, last_capabilities TEXT, last_app_version TEXT, last_capabilities_seen_at TEXT);

-- table tbl_audit_log
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

-- table tbl_event
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
, apply_state INTEGER NOT NULL DEFAULT 0, hold_reason TEXT, held_at TEXT, origin_superadmin INTEGER, local_type TEXT, local_article_id TEXT, origin_peer TEXT, origin_seq INTEGER);

-- table tbl_sync_position
CREATE TABLE tbl_sync_position (
    remote_node_id    TEXT PRIMARY KEY,
    last_sequence_num INTEGER NOT NULL,
    updated_at        TEXT NOT NULL
);

-- table tbl_sync_push_position
CREATE TABLE tbl_sync_push_position (
    remote_node_id  TEXT PRIMARY KEY,
    last_pushed_seq INTEGER NOT NULL DEFAULT 0,
    pushed_at       TEXT NOT NULL
, reported_seq INTEGER);

-- table tbl_tombstone
CREATE TABLE tbl_tombstone (
    article_id     TEXT PRIMARY KEY,
    created_at     TEXT NOT NULL,
    expires_at     TEXT NOT NULL,
    lamport_ts     INTEGER NOT NULL DEFAULT 0,
    source_node_id BLOB
);

-- table tbl_projection_matrix
CREATE TABLE tbl_projection_matrix (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    encrypted_matrix BLOB NOT NULL,
    iv               BLOB NOT NULL,
    created_at       TEXT NOT NULL
);

-- table tbl_comment
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

-- table tbl_compaction_log
CREATE TABLE tbl_compaction_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    compacted_at        TEXT NOT NULL,
    cp_before           INTEGER,
    cp_after            INTEGER NOT NULL,
    events_removed      INTEGER NOT NULL,
    snapshot_file_name  TEXT,
    reason              TEXT
);

-- table tbl_migration_marker
CREATE TABLE tbl_migration_marker (
    key      TEXT NOT NULL PRIMARY KEY,
    value    TEXT NOT NULL,
    set_at   TEXT NOT NULL
);

-- table tbl_restore_replay_shield
CREATE TABLE tbl_restore_replay_shield (
    peer_node_id                     TEXT NOT NULL PRIMARY KEY,
    ignore_events_before_lamport_ts  INTEGER NOT NULL,
    shield_event_id                  TEXT NOT NULL,
    created_at                       TEXT NOT NULL
);

-- table tbl_restore_event_state
CREATE TABLE tbl_restore_event_state (
    event_id          TEXT NOT NULL PRIMARY KEY,
    state             TEXT NOT NULL,
    superseded_by     TEXT,
    rejected_locally  INTEGER NOT NULL DEFAULT 0,
    applied_at        TEXT,
    error_message     TEXT,
    hash_fail_count   INTEGER NOT NULL DEFAULT 0,
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);

-- table tbl_dek_rotation_state
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
, chain_encrypted_new_dek TEXT, chain_iv                TEXT);

-- table tbl_user
CREATE TABLE tbl_user (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name  TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role          TEXT NOT NULL DEFAULT 'user',
    key_slot_id   INTEGER,
    is_active     INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    last_login_at TEXT,
    security_stamp TEXT NOT NULL DEFAULT '',
    chat_access   INTEGER NOT NULL DEFAULT 1
);

-- table tbl_folder_acl_entry
CREATE TABLE tbl_folder_acl_entry (
    user_id   INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    folder_id TEXT    NOT NULL REFERENCES tbl_folder(id) ON DELETE CASCADE,
    effect    TEXT    NOT NULL CHECK(effect IN ('allow', 'deny')),
    created_at TEXT   NOT NULL,
    is_read_only INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(user_id, folder_id, effect)
);

-- table tbl_article
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
    folder_id               TEXT REFERENCES tbl_folder(id),
    remote_subscription_id  TEXT,
    remote_origin_id        TEXT,
    remote_version          INTEGER,
    remote_updated_by       TEXT,
    protected               INTEGER NOT NULL DEFAULT 0,
    protection_hint         TEXT
, index_pending INTEGER NOT NULL DEFAULT 1);

-- table tbl_concept_tag
CREATE TABLE tbl_concept_tag (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    name                    TEXT NOT NULL UNIQUE,
    created_at              TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    embedding               BLOB,
    embedding_model_version TEXT
);

-- table tbl_article_concept_tag
CREATE TABLE tbl_article_concept_tag (
    article_id     TEXT NOT NULL REFERENCES tbl_article(id),
    concept_tag_id INTEGER NOT NULL REFERENCES tbl_concept_tag(id),
    PRIMARY KEY (article_id, concept_tag_id)
);

-- table tbl_concept_tag_edge
CREATE TABLE tbl_concept_tag_edge (
    tag_id_a   INTEGER NOT NULL REFERENCES tbl_concept_tag(id) ON DELETE CASCADE,
    tag_id_b   INTEGER NOT NULL REFERENCES tbl_concept_tag(id) ON DELETE CASCADE,
    article_id TEXT    NOT NULL REFERENCES tbl_article(id)     ON DELETE CASCADE,
    PRIMARY KEY (tag_id_a, tag_id_b, article_id),
    CHECK (tag_id_a < tag_id_b)
) WITHOUT ROWID;

-- table tbl_article_body
CREATE TABLE tbl_article_body (
    article_id    TEXT PRIMARY KEY REFERENCES tbl_article(id),
    iv            BLOB NOT NULL,
    encrypted_dek BLOB NOT NULL,
    dek_iv        BLOB NOT NULL
, ciphertext_hash TEXT);

-- table tbl_article_version
CREATE TABLE tbl_article_version (
    id             TEXT PRIMARY KEY,
    article_id     TEXT NOT NULL,
    version_number INTEGER NOT NULL,
    title          TEXT NOT NULL,
    tree_path      TEXT NOT NULL,
    iv             BLOB NOT NULL,
    encrypted_dek  BLOB NOT NULL,
    dek_iv         BLOB NOT NULL,
    created_at     TEXT NOT NULL,
    updated_by     TEXT, ciphertext_hash TEXT,
    FOREIGN KEY (article_id) REFERENCES tbl_article(id)
);

-- table tbl_conflict_version
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

-- table tbl_media
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
, kind TEXT NOT NULL DEFAULT 'image', ciphertext_sha256 TEXT, uploaded_by TEXT NULL);

-- table tbl_hard_delete_audit
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
    lamport_ts        INTEGER NOT NULL DEFAULT 0
);

-- table tbl_remote_account
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

-- table tbl_remote_subscription
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

-- table tbl_remote_api_token
CREATE TABLE tbl_remote_api_token (
    id            TEXT PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    token_hash    TEXT NOT NULL,                  -- SHA-256 hex
    label         TEXT,
    created_at    TEXT NOT NULL,
    last_used_at  TEXT,
    expires_at    TEXT NOT NULL                   -- ISO-8601
);

-- table tbl_search_index_manifest
CREATE TABLE tbl_search_index_manifest (
    segment_id     TEXT PRIMARY KEY,
    file_path      TEXT NOT NULL,
    doc_count      INTEGER NOT NULL,
    dek_epoch      INTEGER NOT NULL,
    format_version INTEGER NOT NULL,
    created_at     TEXT NOT NULL
);

-- table tbl_search_index_key
CREATE TABLE tbl_search_index_key (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    wrapped_key BLOB NOT NULL,
    iv          BLOB NOT NULL,
    dek_epoch   INTEGER NOT NULL,
    created_at  TEXT NOT NULL
);

-- table tbl_search_segment_tombstone
CREATE TABLE tbl_search_segment_tombstone (
    segment_id TEXT NOT NULL,
    article_id TEXT NOT NULL,
    PRIMARY KEY (segment_id, article_id)
);

-- table tbl_article_chunk_embedding
CREATE TABLE tbl_article_chunk_embedding (
    article_id    TEXT    NOT NULL REFERENCES tbl_article(id) ON DELETE CASCADE,
    chunk_index   INTEGER NOT NULL,
    projection    BLOB    NOT NULL,
    scale         REAL    NOT NULL,
    model_version TEXT    NOT NULL,
    PRIMARY KEY (article_id, chunk_index)
);

-- table tbl_role
CREATE TABLE tbl_role (
    name         TEXT PRIMARY KEY COLLATE NOCASE,
    display_name TEXT NOT NULL,
    description  TEXT,
    is_system    INTEGER NOT NULL DEFAULT 0,
    base_policy  TEXT NOT NULL DEFAULT 'closed' CHECK(base_policy IN ('open', 'closed')),
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
);

-- table tbl_role_folder_acl_entry
CREATE TABLE tbl_role_folder_acl_entry (
    role_name    TEXT    NOT NULL COLLATE NOCASE REFERENCES tbl_role(name) ON DELETE CASCADE ON UPDATE CASCADE,
    folder_id    TEXT    NOT NULL REFERENCES tbl_folder(id) ON DELETE CASCADE,
    effect       TEXT    NOT NULL CHECK(effect IN ('allow', 'deny')),
    is_read_only INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT    NOT NULL,
    PRIMARY KEY(role_name, folder_id, effect)
);

-- table tbl_favorite
CREATE TABLE tbl_favorite (
    user_id    INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    article_id TEXT    NOT NULL REFERENCES tbl_article(id) ON DELETE CASCADE,
    sort_order INTEGER,
    created_at TEXT    NOT NULL,
    PRIMARY KEY (user_id, article_id)
);

-- table tbl_sync_quarantine
CREATE TABLE tbl_sync_quarantine (
    event_id            TEXT    NOT NULL PRIMARY KEY,
    event_type          TEXT    NOT NULL,
    origin_node_id      TEXT    NOT NULL,
    failure_count       INTEGER NOT NULL DEFAULT 0,
    first_failed_at_utc TEXT    NOT NULL,
    last_failed_at_utc  TEXT    NOT NULL,
    last_error          TEXT    NOT NULL
, deferred_failure_count INTEGER NOT NULL DEFAULT 0, last_failure_kind TEXT NOT NULL DEFAULT 'permanent');

-- table tbl_agent
CREATE TABLE tbl_agent (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL,
    description      TEXT,
    key_prefix       TEXT NOT NULL,
    key_hash         TEXT NOT NULL UNIQUE,
    encrypted_dek    BLOB,
    dek_iv           BLOB,
    kdf_version      INTEGER NOT NULL DEFAULT 0,
    salt             BLOB,
    status           TEXT NOT NULL DEFAULT 'A',
    created_at       TEXT NOT NULL,
    last_accessed_at TEXT,
    request_count    INTEGER NOT NULL DEFAULT 0,
    owner_user_id    INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE RESTRICT
);

-- table tbl_blob
CREATE TABLE tbl_blob (
    hash       TEXT    PRIMARY KEY,
    data       BLOB    NOT NULL,
    size       INTEGER NOT NULL,
    created_at TEXT    NOT NULL
);

-- table tbl_node_data_key
CREATE TABLE tbl_node_data_key (
    key_name    TEXT    NOT NULL PRIMARY KEY,
    wrapped_key BLOB    NOT NULL,
    iv          BLOB    NOT NULL,
    created_at  TEXT    NOT NULL
);

-- table tbl_recovery_box
CREATE TABLE tbl_recovery_box (
    box_id            TEXT    NOT NULL PRIMARY KEY,
    kind              TEXT    NOT NULL CHECK (kind IN ('strong', 'device')),
    author_node_id    TEXT    NOT NULL,
    dek_fingerprint   TEXT    NOT NULL,
    epoch_hint        INTEGER NOT NULL DEFAULT 0,
    kdf_preset        TEXT    NOT NULL,
    salt              BLOB    NOT NULL,
    wrapped           BLOB    NOT NULL,
    iv                BLOB    NOT NULL,
    created_at        TEXT    NOT NULL,
    status            TEXT    NOT NULL DEFAULT 'A' CHECK (status IN ('A', 'R')),
    retired_by_box_id TEXT,
    lamport_ts        INTEGER NOT NULL DEFAULT 0,
    source_node_id    TEXT
);

-- table tbl_dek_retired_link
CREATE TABLE tbl_dek_retired_link (
    commit_id         TEXT    NOT NULL,
    author_node_id    TEXT    NOT NULL,
    old_fingerprint   TEXT    NOT NULL,
    new_fingerprint   TEXT    NOT NULL,
    wrapped           BLOB    NOT NULL,
    iv                BLOB    NOT NULL,
    created_at        TEXT    NOT NULL,
    PRIMARY KEY (commit_id, author_node_id)
);

-- table tbl_state_anchor
CREATE TABLE tbl_state_anchor (
    anchor_id         TEXT    NOT NULL PRIMARY KEY,
    author_node_id    TEXT    NOT NULL,
    dek_fingerprint   TEXT    NOT NULL,
    position_vector   TEXT    NOT NULL,
    digest            TEXT    NOT NULL,
    hmac              TEXT    NOT NULL,
    created_at        TEXT    NOT NULL,
    lamport_ts        INTEGER NOT NULL DEFAULT 0
);

-- table tbl_sealed_secret
CREATE TABLE tbl_sealed_secret (
    name              TEXT    NOT NULL PRIMARY KEY,
    dek_fingerprint   TEXT    NOT NULL,
    wrapped           BLOB    NOT NULL,
    iv                BLOB    NOT NULL,
    updated_at        TEXT    NOT NULL,
    status            TEXT    NOT NULL DEFAULT 'A' CHECK (status IN ('A', 'R')),
    lamport_ts        INTEGER NOT NULL DEFAULT 0,
    source_node_id    TEXT
);

-- table tbl_blind_state
CREATE TABLE tbl_blind_state (
    key         TEXT NOT NULL PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

-- table tbl_recovery_box_pending_retire
CREATE TABLE tbl_recovery_box_pending_retire (
    event_id          TEXT    NOT NULL,
    box_id            TEXT    NOT NULL,
    covering_box_id   TEXT    NOT NULL,
    lamport_ts        INTEGER NOT NULL,
    source_node_id    TEXT    NOT NULL,
    received_at       TEXT    NOT NULL,
    PRIMARY KEY (event_id, box_id)
);

-- table tbl_recovery_box_check
CREATE TABLE tbl_recovery_box_check (
    box_id            TEXT    NOT NULL,
    slot_id           INTEGER NOT NULL,
    opens             INTEGER NOT NULL,
    dek_fingerprint   TEXT,
    checked_at        TEXT    NOT NULL,
    PRIMARY KEY (box_id, slot_id)
);

-- table tbl_blind_restore_code
CREATE TABLE tbl_blind_restore_code (
    code_hash         TEXT    NOT NULL PRIMARY KEY,
    issued_at         TEXT    NOT NULL,
    expires_at        TEXT    NOT NULL,
    issued_by         TEXT,
    used_at           TEXT,
    claimed_node_id   TEXT,
    failed_attempts   INTEGER NOT NULL DEFAULT 0,
    revoked           INTEGER NOT NULL DEFAULT 0
);

-- table tbl_metadata_key
CREATE TABLE tbl_metadata_key (
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

-- table tbl_metadata_activation
CREATE TABLE tbl_metadata_activation (
    activation_id   TEXT    NOT NULL PRIMARY KEY,
    key_id          TEXT    NOT NULL,
    generation      INTEGER NOT NULL,
    activated_at    TEXT    NOT NULL,
    author_node_id  TEXT    NOT NULL,
    event_id        TEXT    NOT NULL,
    lamport_ts      INTEGER NOT NULL,
    applied_at      TEXT    NOT NULL
);

-- table tbl_baseline
CREATE TABLE tbl_baseline (
    baseline_id       TEXT    NOT NULL PRIMARY KEY,
    producer_node_id  TEXT    NOT NULL,
    created_at        TEXT    NOT NULL,
    adopted_at        TEXT    NOT NULL,
    lamport_at_cut    INTEGER NOT NULL,
    local_cut         INTEGER NOT NULL,     -- position in THIS node's log the baseline covers
    key_id            TEXT    NOT NULL,
    key_generation    INTEGER NOT NULL,
    container_sha256  TEXT    NOT NULL,
    container_bytes   INTEGER NOT NULL,
    blob_count        INTEGER NOT NULL,
    blob_bytes        INTEGER NOT NULL,
    manifest          BLOB    NOT NULL,     -- the signed bytes
    signature         BLOB    NOT NULL,
    is_current        INTEGER NOT NULL DEFAULT 0
);

-- table tbl_baseline_blob
CREATE TABLE tbl_baseline_blob (
    baseline_id TEXT NOT NULL,
    hash        TEXT NOT NULL,
    PRIMARY KEY (baseline_id, hash)
);

-- rows seeded by the old migrations: tbl_migration_marker
INSERT INTO "tbl_migration_marker" ("key", "value", "set_at") VALUES ('unlock_material_version', '0', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

-- rows seeded by the old migrations: tbl_role
INSERT INTO "tbl_role" ("name", "display_name", "description", "is_system", "base_policy", "created_at", "updated_at") VALUES ('superadmin', 'Superadmin', 'Full administrative and vault access. Bypasses every folder restriction.', 1, 'open', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
INSERT INTO "tbl_role" ("name", "display_name", "description", "is_system", "base_policy", "created_at", "updated_at") VALUES ('user', 'User', 'Default non-privileged role. Rules added here apply to every user who has no other role.', 1, 'open', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

-- AUTOINCREMENT counters the old migrations left
INSERT INTO sqlite_sequence (name, seq) SELECT 'tbl_agent', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'tbl_agent');

-- full-text table fts_article
CREATE VIRTUAL TABLE fts_article USING fts5(
    title,
    tree_path,
    content='tbl_article'
);

-- full-text table fts_folder
CREATE VIRTUAL TABLE fts_folder USING fts5(
    name,
    path,
    content='tbl_folder'
);

-- full-text table fts_tag
CREATE VIRTUAL TABLE fts_tag USING fts5(
    name,
    content='tbl_concept_tag'
);

-- index idx_folder_path_active
CREATE UNIQUE INDEX idx_folder_path_active  ON tbl_folder(path) WHERE status = 'A';

-- index idx_folder_path
CREATE INDEX idx_folder_path                ON tbl_folder(path);

-- index idx_folder_parent_path
CREATE INDEX idx_folder_parent_path         ON tbl_folder(parent_path);

-- index idx_folder_status
CREATE INDEX idx_folder_status              ON tbl_folder(status);

-- index idx_folder_parent_status
CREATE INDEX idx_folder_parent_status       ON tbl_folder(parent_path, status) WHERE status = 'A';

-- index idx_folder_cascade_op
CREATE INDEX idx_folder_cascade_op          ON tbl_folder(cascade_delete_op_id) WHERE cascade_delete_op_id IS NOT NULL;

-- index idx_article_tree_path
CREATE INDEX idx_article_tree_path          ON tbl_article(tree_path);

-- index idx_article_status
CREATE INDEX idx_article_status             ON tbl_article(status);

-- index idx_article_folder_id
CREATE INDEX idx_article_folder_id          ON tbl_article(folder_id);

-- index idx_article_updated_active
CREATE INDEX idx_article_updated_active     ON tbl_article(updated_at DESC) WHERE status = 'A';

-- index idx_article_embedding_pending
CREATE INDEX idx_article_embedding_pending  ON tbl_article(id) WHERE status = 'A' AND embedding_pending = 1;

-- index ix_concept_tag_name_nocase
CREATE UNIQUE INDEX ix_concept_tag_name_nocase ON tbl_concept_tag(name COLLATE NOCASE);

-- index ix_act_concept_tag_id
CREATE INDEX ix_act_concept_tag_id          ON tbl_article_concept_tag(concept_tag_id);

-- index ix_concept_tag_edge_pair
CREATE INDEX ix_concept_tag_edge_pair       ON tbl_concept_tag_edge(tag_id_a, tag_id_b);

-- index ix_concept_tag_edge_article
CREATE INDEX ix_concept_tag_edge_article    ON tbl_concept_tag_edge(article_id);

-- index ix_concept_tag_edge_tag_b
CREATE INDEX ix_concept_tag_edge_tag_b      ON tbl_concept_tag_edge(tag_id_b, tag_id_a);

-- index idx_article_version_article
CREATE UNIQUE INDEX idx_article_version_article ON tbl_article_version(article_id, version_number DESC);

-- index idx_conflict_version_article
CREATE INDEX idx_conflict_version_article   ON tbl_conflict_version(article_id);

-- index idx_conflict_version_expires
CREATE INDEX idx_conflict_version_expires   ON tbl_conflict_version(expires_at);

-- index idx_media_article_id
CREATE INDEX idx_media_article_id           ON tbl_media(article_id);

-- index idx_media_status
CREATE INDEX idx_media_status               ON tbl_media(status);

-- index idx_media_article_active
CREATE INDEX idx_media_article_active       ON tbl_media(article_id, status) WHERE status = 'A';

-- index ix_audit_log_created_at
CREATE INDEX ix_audit_log_created_at        ON tbl_audit_log(created_at);

-- index idx_event_node_seq
CREATE INDEX idx_event_node_seq             ON tbl_event(node_id, sequence_num);

-- index idx_event_lamport_ts
CREATE INDEX idx_event_lamport_ts           ON tbl_event(lamport_ts);

-- index idx_event_article_id
CREATE INDEX idx_event_article_id           ON tbl_event(article_id);

-- index idx_event_hard_delete_entity
CREATE INDEX idx_event_hard_delete_entity   ON tbl_event(event_type, entity_id) WHERE event_type = 'hard_delete';

-- index idx_event_type
CREATE INDEX idx_event_type                 ON tbl_event(event_type);

-- index idx_tombstone_expires
CREATE INDEX idx_tombstone_expires          ON tbl_tombstone(expires_at);

-- index idx_tbl_comment_article
CREATE INDEX idx_tbl_comment_article        ON tbl_comment(article_id);

-- index idx_comment_comment_id
CREATE UNIQUE INDEX idx_comment_comment_id  ON tbl_comment(comment_id) WHERE comment_id IS NOT NULL;

-- index idx_comment_article_active_created
CREATE INDEX idx_comment_article_active_created ON tbl_comment(article_id, created_at) WHERE deleted_at IS NULL;

-- index idx_folder_acl_entry_user
CREATE INDEX idx_folder_acl_entry_user      ON tbl_folder_acl_entry(user_id);

-- index idx_folder_acl_entry_folder
CREATE INDEX idx_folder_acl_entry_folder    ON tbl_folder_acl_entry(folder_id);

-- index idx_hard_delete_audit_occurred_at
CREATE INDEX idx_hard_delete_audit_occurred_at ON tbl_hard_delete_audit(occurred_at);

-- index idx_hard_delete_audit_entity
CREATE INDEX idx_hard_delete_audit_entity      ON tbl_hard_delete_audit(entity_identifier, lamport_ts);

-- index idx_compaction_log_date
CREATE INDEX idx_compaction_log_date        ON tbl_compaction_log(compacted_at DESC);

-- index idx_restore_replay_shield_peer
CREATE INDEX idx_restore_replay_shield_peer ON tbl_restore_replay_shield(peer_node_id);

-- index idx_restore_event_state_state
CREATE INDEX idx_restore_event_state_state  ON tbl_restore_event_state(state);

-- index idx_dek_rotation_state_state
CREATE INDEX idx_dek_rotation_state_state   ON tbl_dek_rotation_state(state);

-- index idx_remote_account_url
CREATE INDEX idx_remote_account_url         ON tbl_remote_account(base_url);

-- index idx_remote_subscription_account
CREATE INDEX idx_remote_subscription_account ON tbl_remote_subscription(remote_account_id);

-- index idx_folder_remote_sub
CREATE INDEX idx_folder_remote_sub          ON tbl_folder(remote_subscription_id);

-- index idx_article_remote_sub
CREATE INDEX idx_article_remote_sub         ON tbl_article(remote_subscription_id);

-- index idx_remote_api_token_hash
CREATE UNIQUE INDEX idx_remote_api_token_hash ON tbl_remote_api_token(token_hash);

-- index idx_remote_api_token_user
CREATE INDEX idx_remote_api_token_user      ON tbl_remote_api_token(user_id);

-- index idx_article_index_pending
CREATE INDEX idx_article_index_pending ON tbl_article(id) WHERE status = 'A' AND index_pending = 1;

-- index idx_role_folder_acl_folder
CREATE INDEX idx_role_folder_acl_folder ON tbl_role_folder_acl_entry(folder_id);

-- index idx_user_role
CREATE INDEX idx_user_role ON tbl_user(role COLLATE NOCASE);

-- index idx_favorite_article
CREATE INDEX idx_favorite_article ON tbl_favorite(article_id);

-- index idx_article_body_hash
CREATE INDEX idx_article_body_hash    ON tbl_article_body(ciphertext_hash);

-- index idx_article_version_hash
CREATE INDEX idx_article_version_hash ON tbl_article_version(ciphertext_hash);

-- index ix_recovery_box_author_kind
CREATE INDEX ix_recovery_box_author_kind ON tbl_recovery_box (author_node_id, kind, status);

-- index ix_recovery_box_lookup
CREATE INDEX ix_recovery_box_lookup ON tbl_recovery_box (status, kind, epoch_hint DESC);

-- index ix_dek_retired_link_new
CREATE INDEX ix_dek_retired_link_new ON tbl_dek_retired_link (new_fingerprint);

-- index ix_state_anchor_created
CREATE INDEX ix_state_anchor_created ON tbl_state_anchor (created_at DESC);

-- index ix_recovery_box_pending_retire_covering
CREATE INDEX ix_recovery_box_pending_retire_covering ON tbl_recovery_box_pending_retire (covering_box_id);

-- index ix_blind_restore_code_issued
CREATE INDEX ix_blind_restore_code_issued ON tbl_blind_restore_code (issued_at DESC);

-- index ix_metadata_key_generation
CREATE INDEX ix_metadata_key_generation ON tbl_metadata_key (generation DESC, key_id);

-- index ix_event_held
CREATE INDEX ix_event_held ON tbl_event (apply_state, sequence_num) WHERE apply_state <> 0;

-- index ux_baseline_current
CREATE UNIQUE INDEX ux_baseline_current ON tbl_baseline (is_current) WHERE is_current = 1;

-- index ix_baseline_blob_hash
CREATE INDEX ix_baseline_blob_hash ON tbl_baseline_blob (hash);

-- trigger trg_act_insert_edge
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

-- trigger trg_act_delete_edge
CREATE TRIGGER trg_act_delete_edge
AFTER DELETE ON tbl_article_concept_tag
BEGIN
    DELETE FROM tbl_concept_tag_edge
    WHERE article_id = OLD.article_id
      AND (tag_id_a = OLD.concept_tag_id OR tag_id_b = OLD.concept_tag_id);
END;

-- trigger trg_fts_article_ai
CREATE TRIGGER trg_fts_article_ai AFTER INSERT ON tbl_article BEGIN
    INSERT INTO fts_article(rowid, title, tree_path)
    VALUES (NEW.rowid, NEW.title, NEW.tree_path);
END;

-- trigger trg_fts_article_ad
CREATE TRIGGER trg_fts_article_ad AFTER DELETE ON tbl_article BEGIN
    INSERT INTO fts_article(fts_article, rowid, title, tree_path)
    VALUES ('delete', OLD.rowid, OLD.title, OLD.tree_path);
END;

-- trigger trg_fts_article_au
CREATE TRIGGER trg_fts_article_au AFTER UPDATE OF title, tree_path ON tbl_article BEGIN
    INSERT INTO fts_article(fts_article, rowid, title, tree_path)
    VALUES ('delete', OLD.rowid, OLD.title, OLD.tree_path);
    INSERT INTO fts_article(rowid, title, tree_path)
    VALUES (NEW.rowid, NEW.title, NEW.tree_path);
END;

-- trigger trg_fts_folder_ai
CREATE TRIGGER trg_fts_folder_ai AFTER INSERT ON tbl_folder BEGIN
    INSERT INTO fts_folder(rowid, name, path)
    VALUES (NEW.rowid, NEW.name, NEW.path);
END;

-- trigger trg_fts_folder_ad
CREATE TRIGGER trg_fts_folder_ad AFTER DELETE ON tbl_folder BEGIN
    INSERT INTO fts_folder(fts_folder, rowid, name, path)
    VALUES ('delete', OLD.rowid, OLD.name, OLD.path);
END;

-- trigger trg_fts_folder_au
CREATE TRIGGER trg_fts_folder_au AFTER UPDATE OF name, path ON tbl_folder BEGIN
    INSERT INTO fts_folder(fts_folder, rowid, name, path)
    VALUES ('delete', OLD.rowid, OLD.name, OLD.path);
    INSERT INTO fts_folder(rowid, name, path)
    VALUES (NEW.rowid, NEW.name, NEW.path);
END;

-- trigger trg_fts_tag_ai
CREATE TRIGGER trg_fts_tag_ai AFTER INSERT ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(rowid, name)
    VALUES (NEW.rowid, NEW.name);
END;

-- trigger trg_fts_tag_ad
CREATE TRIGGER trg_fts_tag_ad AFTER DELETE ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(fts_tag, rowid, name)
    VALUES ('delete', OLD.rowid, OLD.name);
END;

-- trigger trg_fts_tag_au
CREATE TRIGGER trg_fts_tag_au AFTER UPDATE OF name ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(fts_tag, rowid, name)
    VALUES ('delete', OLD.rowid, OLD.name);
    INSERT INTO fts_tag(rowid, name)
    VALUES (NEW.rowid, NEW.name);
END;

-- trigger trg_umv_key_slot_insert
CREATE TRIGGER trg_umv_key_slot_insert AFTER INSERT ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_key_slot_update
CREATE TRIGGER trg_umv_key_slot_update AFTER UPDATE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_key_slot_delete
CREATE TRIGGER trg_umv_key_slot_delete AFTER DELETE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_agent_insert
CREATE TRIGGER trg_umv_agent_insert AFTER INSERT ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_agent_update
CREATE TRIGGER trg_umv_agent_update
AFTER UPDATE OF encrypted_dek, dek_iv, salt, kdf_version, key_hash, status, owner_user_id ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_agent_delete
CREATE TRIGGER trg_umv_agent_delete AFTER DELETE ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_user_update
CREATE TRIGGER trg_umv_user_update AFTER UPDATE OF role, is_active, key_slot_id ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_user_delete
CREATE TRIGGER trg_umv_user_delete AFTER DELETE ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_whitelist_insert
CREATE TRIGGER trg_umv_whitelist_insert AFTER INSERT ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_whitelist_update
CREATE TRIGGER trg_umv_whitelist_update
AFTER UPDATE OF display_name, ed25519_public_key, api_address, status, is_superadmin, deleted_at, tls_spki, tls_trust ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_whitelist_delete
CREATE TRIGGER trg_umv_whitelist_delete AFTER DELETE ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_identity_insert
CREATE TRIGGER trg_umv_identity_insert AFTER INSERT ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- trigger trg_umv_identity_update
CREATE TRIGGER trg_umv_identity_update AFTER UPDATE OF node_id, display_name, ed25519_public_key ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

