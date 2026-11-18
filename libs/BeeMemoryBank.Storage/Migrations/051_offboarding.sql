-- 051_offboarding.sql
--
-- The removal of a superadmin as a process (release 2.12.0, plan WS-E and R1). Migrations 049 and 050 belong to parallel tracks.
--
-- 1. tbl_user.account_state: 'active', or 'offboarding' while the person is being removed. Such an account cannot sign in and the API
--    gives it no rights at all; the row stays (the Users page shows "being removed, step k of n") until the process closes it.
-- 2. tbl_offboarding: one row per removal process. On the accounts node it is the working record (origin 'local'); a shared full node
--    holds the copy it learned from the replicated offboarding_state event (origin 'replicated') and may take the process over when
--    the accounts node is lost. The target role and folder rules of a demotion live only in the local row; no event carries them.
-- 3. tbl_offboarding_node: what each step waits for, per node (S1 identity re-key, S2 shared-doors list, S4 rotation), and whether
--    that node confirmed it or was revoked (a revoked node is never waited for). Node-local.
-- 4. tbl_removed_person: the tombstones of person_removed (person id, outcome, time; no names, no folders). Replicated and monotonic:
--    a network restore merges them, never drops them, so a restore cannot bring a removed person back.

ALTER TABLE tbl_user ADD COLUMN account_state TEXT NOT NULL DEFAULT 'active';

CREATE TABLE IF NOT EXISTS tbl_offboarding (
    process_id        TEXT NOT NULL PRIMARY KEY,
    person_id         TEXT NOT NULL,
    user_id           INTEGER,
    accounts_node_id  TEXT NOT NULL,
    driver_node_id    TEXT NOT NULL,
    target            TEXT NOT NULL CHECK (target IN ('delete', 'demote')),
    target_role       TEXT,
    target_rules_json TEXT,
    allow_open_role   INTEGER NOT NULL DEFAULT 0,
    step              INTEGER NOT NULL DEFAULT 0 CHECK (step BETWEEN 0 AND 6),
    state_json        TEXT NOT NULL DEFAULT '{}',
    review_json       TEXT,
    started_by        TEXT NOT NULL DEFAULT '',
    started_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL,
    closed_at         TEXT,
    outcome           TEXT CHECK (outcome IS NULL OR outcome IN ('deleted', 'demoted')),
    version           INTEGER NOT NULL DEFAULT 0,
    origin            TEXT NOT NULL DEFAULT 'local' CHECK (origin IN ('local', 'replicated'))
);

CREATE INDEX IF NOT EXISTS ix_offboarding_person ON tbl_offboarding (person_id);

CREATE TABLE IF NOT EXISTS tbl_offboarding_node (
    process_id   TEXT NOT NULL,
    node_id      TEXT NOT NULL,
    step         INTEGER NOT NULL CHECK (step IN (1, 2, 4)),
    state        TEXT NOT NULL CHECK (state IN ('waiting', 'confirmed', 'revoked')),
    baseline     TEXT,
    confirmed_at TEXT,
    PRIMARY KEY (process_id, node_id, step)
);

CREATE TABLE IF NOT EXISTS tbl_removed_person (
    person_id  TEXT NOT NULL PRIMARY KEY,
    outcome    TEXT NOT NULL CHECK (outcome IN ('deleted', 'demoted')),
    removed_at TEXT NOT NULL,
    event_id   TEXT
);
