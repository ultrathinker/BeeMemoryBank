-- Blind nodes (BMB-43, plan 6.3): node-local bookkeeping for the replicated recovery boxes of
-- migration 027. Neither table travels to peers.

-- A recovery_box_retire whose covering strong box is not active here yet, or whose target box has not
-- arrived yet. A retire must never be dropped for that (plan 6.3): it waits here and is applied the
-- moment both are present, from the recovery_box_set applier. One row per (event, target box).
-- Local on purpose: a node seeded from a snapshot taken while a retire was pending does not inherit
-- it, and the next cleanup on the superadmin PC issues it again (cleanup runs whenever more than the
-- strong box is left).
CREATE TABLE IF NOT EXISTS tbl_recovery_box_pending_retire (
    event_id          TEXT    NOT NULL,
    box_id            TEXT    NOT NULL,
    covering_box_id   TEXT    NOT NULL,
    lamport_ts        INTEGER NOT NULL,
    source_node_id    TEXT    NOT NULL,
    received_at       TEXT    NOT NULL,
    PRIMARY KEY (event_id, box_id)
);
CREATE INDEX IF NOT EXISTS ix_recovery_box_pending_retire_covering ON tbl_recovery_box_pending_retire (covering_box_id);

-- Cleanup at login tries this PC's password on other devices' boxes. A box is immutable and the PC's
-- password lives in exactly one key slot row (a password change writes a new row), so the answer for
-- (box, slot) never changes: remember it and never pay the derivation twice. No password material.
CREATE TABLE IF NOT EXISTS tbl_recovery_box_check (
    box_id            TEXT    NOT NULL,
    slot_id           INTEGER NOT NULL,
    opens             INTEGER NOT NULL,
    dek_fingerprint   TEXT,
    checked_at        TEXT    NOT NULL,
    PRIMARY KEY (box_id, slot_id)
);
