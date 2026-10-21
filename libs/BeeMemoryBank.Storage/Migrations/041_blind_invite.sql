-- 041_blind_invite.sql
--
-- The invitations to a blind copy that THIS full node made and the redemptions they received (release 2.7, docs/sync.md "Invitations").
-- Local to the node: the table is not in SnapshotTables.Replicated, so no join snapshot, blind package or baseline carries a row of it,
-- and a blind node holds the (empty) table only because the migrations are shared.
--
-- state: issued -> redeemed -> confirming -> confirmed | refused | expired (and issued -> expired when it runs out or is withdrawn, redeemed -> refused | expired
-- when the owner refuses or does not answer). 'confirming' is written BEFORE the device is enrolled (whitelist_add and the seal) and 'confirmed' AFTER, so a
-- crash in between leaves a row that says "enrolment may be half done" and a reconciler finishes it or undoes it; a device is only ever a peer while its row is
-- confirming or confirmed. enroll_event_id is the id the enrolment's whitelist_add is (or will be) logged under, fixed before the first externally visible
-- step: after a crash the log itself says whether the event exists, and an undo revokes by that event - only what this invitation added, even when the local
-- whitelist row was never written, and never an entry another flow added.
-- The secret is never stored, only its SHA-256. backup_key_enc is the copy's backup key encrypted under the master DEK for the time
-- between the redemption and the owner's decision; the trigger below clears it in every final state, whatever code moved the row there.

CREATE TABLE tbl_blind_invite (
    invite_id           TEXT NOT NULL PRIMARY KEY,
    secret_hash         TEXT NOT NULL,
    label               TEXT NOT NULL,
    source_name         TEXT NOT NULL,
    address             TEXT NOT NULL,
    trust               TEXT NOT NULL,
    pin                 TEXT,
    created_by          TEXT NOT NULL,
    created_at          TEXT NOT NULL,
    expires_at          TEXT NOT NULL,
    state               TEXT NOT NULL CHECK (state IN ('issued', 'redeemed', 'confirming', 'confirmed', 'refused', 'expired')),
    redeemed_at         TEXT,
    redeemer_node_id    TEXT,
    redeemer_key        TEXT,
    redeemer_name       TEXT,
    redeemer_platform   TEXT,
    redeemer_version    TEXT,
    redeemer_seen_from  TEXT,
    backup_key_enc      BLOB,
    confirm_deadline    TEXT,
    confirming_at       TEXT,
    enroll_event_id     TEXT,
    decided_at          TEXT,
    decided_by          TEXT
);

CREATE INDEX ix_blind_invite_state ON tbl_blind_invite (state, expires_at);

CREATE TRIGGER trg_blind_invite_final_clears_key AFTER UPDATE OF state ON tbl_blind_invite
WHEN NEW.state IN ('confirmed', 'refused', 'expired') AND NEW.backup_key_enc IS NOT NULL
BEGIN
    UPDATE tbl_blind_invite SET backup_key_enc = NULL WHERE invite_id = NEW.invite_id;
END;
