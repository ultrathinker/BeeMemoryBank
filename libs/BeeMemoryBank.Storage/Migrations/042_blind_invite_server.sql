-- 042_blind_invite_server.sql
--
-- A SERVER blind node can be invited too (release 2.7, `bmb blind join <invitation>`, docs/sync.md "Invitations"). It redeems the invitation like an app copy, but instead of a
-- backup key it carries its own pair code, and the owner's confirmation then adds and seeds it the way pasting that code on the Blind nodes page does.
--
-- kind: 'device' (an app copy: a phone or a desktop, the backup key is sealed for it) or 'server' (a blind node host: its pair code is used to add and seed it). Every row of 041 is a device.
-- pair_code_enc: the server node's pair code under the master DEK for the time between the redemption and the owner's decision, framed and bound to the invitation like backup_key_enc. It
-- holds the one-time secret that lets the owner's node seed the server node, so the trigger clears it in every final state, whatever code moved the row there, exactly as it clears the backup key.

ALTER TABLE tbl_blind_invite ADD COLUMN kind TEXT NOT NULL DEFAULT 'device' CHECK (kind IN ('device', 'server'));
ALTER TABLE tbl_blind_invite ADD COLUMN pair_code_enc BLOB;

DROP TRIGGER trg_blind_invite_final_clears_key;

CREATE TRIGGER trg_blind_invite_final_clears_key AFTER UPDATE OF state ON tbl_blind_invite
WHEN NEW.state IN ('confirmed', 'refused', 'expired') AND (NEW.backup_key_enc IS NOT NULL OR NEW.pair_code_enc IS NOT NULL)
BEGIN
    UPDATE tbl_blind_invite SET backup_key_enc = NULL, pair_code_enc = NULL WHERE invite_id = NEW.invite_id;
END;
