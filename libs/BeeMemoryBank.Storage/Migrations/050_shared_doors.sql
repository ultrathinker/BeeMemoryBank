-- 050_shared_doors.sql
--
-- Shared-node superadmin doors (release 2.12.0, offboarding). Migrations 048-049 are reserved for parallel tracks.
--
-- 1. tbl_user.replicated: 1 for mirror rows created on a shared node from superadmin_doors_set; 0 for local users.
-- 2. tbl_accounts_home: singleton replicated setting (accounts_home_set) naming the node_id where people are managed.
-- 3. tbl_shared_doors_state: singleton local state for self-ownership, publisher versioning/digest, pending/applied doors list.

ALTER TABLE tbl_user ADD COLUMN replicated INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS tbl_accounts_home (
    id             INTEGER PRIMARY KEY CHECK (id = 1),
    node_id        TEXT    NOT NULL,
    lamport_ts     INTEGER NOT NULL DEFAULT 0,
    source_node_id TEXT,
    event_id       TEXT,
    updated_at     TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS tbl_shared_doors_state (
    id                           INTEGER PRIMARY KEY CHECK (id = 1),
    self_ownership               TEXT    NOT NULL DEFAULT 'unknown',
    self_ownership_lamport_ts    INTEGER NOT NULL DEFAULT 0,
    self_ownership_source_node_id TEXT,
    last_issued_version          INTEGER NOT NULL DEFAULT 0,
    last_published_digest        TEXT,
    latest_received_version      INTEGER NOT NULL DEFAULT 0,
    latest_event_id              TEXT,
    latest_author_node_id        TEXT,
    latest_payload_json          TEXT,
    pending_apply                INTEGER NOT NULL DEFAULT 0,
    applied_version              INTEGER NOT NULL DEFAULT 0,
    applied_at                   TEXT,
    primary_person_id            TEXT,
    last_error                   TEXT,
    accounts_home_changed        INTEGER NOT NULL DEFAULT 0
);
