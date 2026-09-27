-- Blind nodes (BMB-43): replicated recovery material a blind node keeps without being able to open
-- any of it. Plan: D:\review\bmb-blind\plan.md, sections 5.5 and 6. All four tables are in
-- SnapshotTables.Replicated, so a node seeded or joined after the events that filled them was
-- compacted away still gets them, and they travel into every backup.
--
-- Nothing here is a secret to a peer: every wrapped value is sealed either under the master DEK or
-- under a key derived from a master password (Argon2id). A blind node stores and relays the bytes;
-- a full node that later restores from it opens them.
--
-- Rows are versioned like other replicated entities (status / lamport_ts / source_node_id, decided
-- by ConflictResolver.IncomingWins), so a late recovery_box_set can never bring back a retired box.

-- A "box": the master DEK wrapped under a key derived from a master password.
--   kind 'strong' — built only by a PC, Argon2id preset 's512t6' or 's1024t4';
--   kind 'device' — a copy of a device's ordinary key slot, preset 'd64t3'.
-- One active box per (author_node_id, kind): the one with the highest (lamport_ts, source_node_id).
-- dek_fingerprint = hex(SHA256("bmb-dek-verify" || DEK)) identifies which key is inside (epochs are
-- not unique across the mesh); epoch_hint is for sorting and UI only.
CREATE TABLE IF NOT EXISTS tbl_recovery_box (
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
CREATE INDEX IF NOT EXISTS ix_recovery_box_author_kind ON tbl_recovery_box (author_node_id, kind, status);
CREATE INDEX IF NOT EXISTS ix_recovery_box_lookup ON tbl_recovery_box (status, kind, epoch_hint DESC);

-- A chain link: the retired DEK (old_fingerprint) wrapped under the DEK that replaced it
-- (new_fingerprint). Keyed by (commit_id, author_node_id): a forged link published first cannot
-- overwrite the real one; a restore tries every link and keeps what opens and verifies.
CREATE TABLE IF NOT EXISTS tbl_dek_retired_link (
    commit_id         TEXT    NOT NULL,
    author_node_id    TEXT    NOT NULL,
    old_fingerprint   TEXT    NOT NULL,
    new_fingerprint   TEXT    NOT NULL,
    wrapped           BLOB    NOT NULL,
    iv                BLOB    NOT NULL,
    created_at        TEXT    NOT NULL,
    PRIMARY KEY (commit_id, author_node_id)
);
CREATE INDEX IF NOT EXISTS ix_dek_retired_link_new ON tbl_dek_retired_link (new_fingerprint);

-- An integrity anchor: a superadmin node that had caught up with the mesh vouches for the state,
-- position_vector = JSON {source node id -> highest position included}, digest over replicated
-- entities, hmac = HMAC(HKDF(DEK, "bmb-blind-state-v1"), ...). Only the DEK holder can produce it.
CREATE TABLE IF NOT EXISTS tbl_state_anchor (
    anchor_id         TEXT    NOT NULL PRIMARY KEY,
    author_node_id    TEXT    NOT NULL,
    dek_fingerprint   TEXT    NOT NULL,
    position_vector   TEXT    NOT NULL,
    digest            TEXT    NOT NULL,
    hmac              TEXT    NOT NULL,
    created_at        TEXT    NOT NULL,
    lamport_ts        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_state_anchor_created ON tbl_state_anchor (created_at DESC);

-- Small secrets sealed under the master DEK that a restore needs before it can open a backup:
-- 'restic:<blind node id>' (restic repository password), 'android-backup:<node id>' (backup key of
-- an Android blind node). The blind node keeps its own copy in clear locally; this sealed copy is
-- what a restore opens once it has the DEK.
CREATE TABLE IF NOT EXISTS tbl_sealed_secret (
    name              TEXT    NOT NULL PRIMARY KEY,
    dek_fingerprint   TEXT    NOT NULL,
    wrapped           BLOB    NOT NULL,
    iv                BLOB    NOT NULL,
    updated_at        TEXT    NOT NULL,
    status            TEXT    NOT NULL DEFAULT 'A' CHECK (status IN ('A', 'R')),
    lamport_ts        INTEGER NOT NULL DEFAULT 0,
    source_node_id    TEXT
);
