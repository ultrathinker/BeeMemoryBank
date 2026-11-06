-- 043_password_hint.sql
--
-- The password hint a person writes when they create or replace a password, and the node-level flags of the recovery-key
-- step a new node shows once its first administrator exists (release 2.7.5, BMB-335 and BMB-336). There is no e-mail
-- recovery in Bee Memory Bank, so a person must always have two ways back in: the recovery key, and a hint they wrote
-- themselves. Both live here, node-local.
--
-- tbl_user.password_hint is plain text for the reason the username and the password hash are: the sign-in page has to
-- show it while the vault is LOCKED, before any password (and therefore any key) is available. It is never encrypted
-- with the vault, never written to a sync event (tbl_user is not replicated), never put in a snapshot's plaintext log,
-- never in audit-log text, and never in a log line. NULL for every user that existed before this migration and for
-- every internal caller (seeds, tools, tests) that has no person to write one.
--
-- recovery_key_required is set when /api/init/standalone or /api/init/join completes: the node has a first
-- administrator and that person has not yet been shown a recovery key. recovery_key_acknowledged_at is set when a
-- superadmin confirms they saved it (POST /api/keys/recovery-acknowledge); while it is NULL and recovery_key_required
-- is 1, the Web UI sends a superadmin session to the gate before any other page. A node that already existed before
-- this migration keeps recovery_key_required at 0, so an upgrade never locks anyone out: it gets the dismissible
-- "no recovery key has been issued" notice instead, and recovery_key_notice_dismissed_at remembers that an operator
-- has seen and dismissed that notice.

ALTER TABLE tbl_user ADD COLUMN password_hint TEXT;

ALTER TABLE tbl_node_identity ADD COLUMN recovery_key_required INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_node_identity ADD COLUMN recovery_key_acknowledged_at TEXT;
ALTER TABLE tbl_node_identity ADD COLUMN recovery_key_notice_dismissed_at TEXT;