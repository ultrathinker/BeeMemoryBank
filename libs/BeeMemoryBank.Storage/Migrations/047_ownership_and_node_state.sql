-- 047_ownership_and_node_state.sql
--
-- People ids, device ownership, and node state (release 2.12.0, offboarding). Migration 046 is reserved for a parallel track.
--
-- 1. tbl_user.person_id: stable UUID per local user row (not replicated; carried in ownership metadata).
--    Backfilled with random UUIDv4 values for every existing user.
-- 2. tbl_whitelist ownership (the 6th whitelist concern): shared / personal / unknown, owner_person_id, owner_label,
--    ownership_lamport_ts, ownership_source_node_id, plus node-local needs_review flag.
--    Existing blind rows default locally to shared (needs_review = 0); non-revoked non-blind rows (active or unconfirmed) default to unknown (needs_review = 1);
--    revoked non-blind rows stay unknown (needs_review = 0). Stored whitelist events are replayed afterward by WhitelistOwnershipRepair.
-- 3. tbl_blind_invite: initial ownership carried from the maker's invitation until redeemed and confirmed.
-- 4. tbl_node_state: materialised latest node_state control event per node (replicated in snapshots via SnapshotTables.Replicated).

ALTER TABLE tbl_user ADD COLUMN person_id TEXT NOT NULL DEFAULT '';

UPDATE tbl_user
SET person_id = lower(
    hex(randomblob(4)) || '-' ||
    hex(randomblob(2)) || '-4' ||
    substr(hex(randomblob(2)), 2) || '-' ||
    substr('89ab', 1 + abs(random() % 4), 1) ||
    substr(hex(randomblob(2)), 2) || '-' ||
    hex(randomblob(6)))
WHERE person_id = '' OR person_id IS NULL;

ALTER TABLE tbl_whitelist ADD COLUMN ownership TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE tbl_whitelist ADD COLUMN owner_person_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN owner_label TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN ownership_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN ownership_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN needs_review INTEGER NOT NULL DEFAULT 0;

UPDATE tbl_whitelist
SET ownership = CASE
        WHEN length(node_id) = 36
         AND substr(lower(node_id), 1, 4) = 'b11d'
         AND substr(lower(node_id), 15, 1) = '8'
         AND substr(lower(node_id), 20, 1) IN ('8', '9', 'a', 'b')
            THEN 'shared'
        ELSE 'unknown'
    END,
    needs_review = CASE
        WHEN length(node_id) = 36
         AND substr(lower(node_id), 1, 4) = 'b11d'
         AND substr(lower(node_id), 15, 1) = '8'
         AND substr(lower(node_id), 20, 1) IN ('8', '9', 'a', 'b')
            THEN 0
        WHEN status <> 'R' THEN 1
        ELSE 0
    END
WHERE ownership = 'unknown'
  AND owner_person_id IS NULL
  AND owner_label IS NULL
  AND ownership_lamport_ts = 0
  AND ownership_source_node_id IS NULL
  AND needs_review = 0;

ALTER TABLE tbl_blind_invite ADD COLUMN ownership TEXT NOT NULL DEFAULT 'shared' CHECK (ownership IN ('shared', 'personal', 'unknown'));
ALTER TABLE tbl_blind_invite ADD COLUMN owner_person_id TEXT;
ALTER TABLE tbl_blind_invite ADD COLUMN owner_label TEXT;

CREATE TABLE IF NOT EXISTS tbl_node_state (
    node_id              TEXT PRIMARY KEY,
    app_version          TEXT NOT NULL DEFAULT '',
    platform             TEXT NOT NULL DEFAULT '',
    runtime_kind         TEXT NOT NULL DEFAULT '',
    identity_key_fp      TEXT NOT NULL DEFAULT '',
    dek_fp               TEXT,
    attention_flags_json TEXT NOT NULL DEFAULT '[]',
    extra_json           TEXT,
    lamport_ts           INTEGER NOT NULL DEFAULT 0,
    event_id             TEXT,
    updated_at           TEXT NOT NULL
);
