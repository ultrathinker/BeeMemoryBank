-- 049_node_key_history.sql
--
-- Node identity re-key (release 2.12.0, plan WS-D). Migration 048 is reserved for a parallel track.
--
-- 1. tbl_node_key_history: one row per node_key_rotated event this node applied (its own included): the key the rotation RETIRED
--    (public_key), the key it introduced, the event's own lamport_ts as the last lamport the retired key may sign (valid_until_lamport),
--    the author's compaction checkpoint and the ids of the author's own events above it (own_event_ids, base64 of sorted 16-byte
--    values), and the two signatures over the transition statement (old_key_proof by the retired key, new_key_proof by the new one).
--    The counter of a node's key is MAX(counter) of its rows (0 when it has none), so tbl_whitelist and the boot mirror keep their
--    columns. Replicated, monotonic (a network restore merges it with insert-or-ignore) and part of the control set of blind packages.
-- 2. tbl_node_key_conflict: node-local. Two different valid rotations of one node from the same (old key, counter): the node's events
--    are refused until a node_key_confirm from another superadmin settles which rotation holds.

CREATE TABLE IF NOT EXISTS tbl_node_key_history (
    node_id               TEXT    NOT NULL,
    counter               INTEGER NOT NULL,
    public_key            BLOB    NOT NULL,
    new_public_key        BLOB    NOT NULL,
    valid_until_lamport   INTEGER NOT NULL,
    rotation_event_id     TEXT    NOT NULL,
    checkpoint_lamport_ts INTEGER NOT NULL DEFAULT 0,
    own_event_ids         TEXT    NOT NULL DEFAULT '',
    old_key_proof         BLOB    NOT NULL,
    new_key_proof         BLOB    NOT NULL,
    created_at            TEXT    NOT NULL,
    PRIMARY KEY (node_id, counter)
);

CREATE INDEX IF NOT EXISTS ix_node_key_history_public_key ON tbl_node_key_history (public_key);

CREATE TABLE IF NOT EXISTS tbl_node_key_conflict (
    node_id          TEXT    NOT NULL PRIMARY KEY,
    counter          INTEGER NOT NULL,
    old_public_key   BLOB    NOT NULL,
    first_event_id   TEXT    NOT NULL,
    first_new_key    BLOB    NOT NULL,
    second_event_id  TEXT    NOT NULL,
    second_new_key   BLOB    NOT NULL,
    detected_at      TEXT    NOT NULL,
    settled_event_id TEXT,
    settled_at       TEXT
);
