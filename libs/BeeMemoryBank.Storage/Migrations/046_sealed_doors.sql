-- 046_sealed_doors.sql
--
-- Sealed doors, door format v2 (release 2.12, docs/adr/0012-sealed-doors.md). Until now every way in (a key slot, an auto-unlock agent)
-- held the master DEK wrapped with AES-256-GCM under the door's KEK: only somebody holding the door's secret could ever write that door
-- again. A v2 door holds the DEK SEALED to an X25519 public key the KEK derives (DoorBox): the box sits in the same column
-- (encrypted_master_dek, encrypted_dek), 93 bytes framed 0x02 || eph_pk || nonce || ct+tag, and door_public_key keeps the public key, so
-- the door can be sealed again from the public key alone. iv keeps a copy of the box nonce (the column is NOT NULL); it is not used to open.
--
-- tbl_key_slot:
--   door_version       1 = the wrap of every release before, 2 = a sealed box. Every row written before this migration is 1.
--   door_public_key    the door's X25519 public key; NULL for a v1 door.
--   dek_fp             DekFingerprint of the master DEK the door yields, written whenever the door is written with the DEK in hand and
--                      filled for a v1 door when it opens. NULL = unknown: the boot store then keeps a database-key wrapper for every
--                      master DEK the session holds, as before (the pruning rule of ADR 0012).
--   owner_user_id      the person a door belongs to. Filled here for every 'user' slot from tbl_user.key_slot_id; recovery slots stay
--                      without an owner (nobody can tell today who a recovery key was given to).
--   created_by_user_id who made the door, when the writer knows; NULL for every row before.
--   label              a free text label (a device name for an auto-unlock or quick-unlock door); NULL before.
-- A new slot type, quick_unlock, needs no schema: a secret-keyed door like the auto-unlock ones (the iPhone keeps only the secret).
--
-- tbl_agent: door_public_key, dek_fp and created_by_user_id with the same meaning. kdf_version = 2 marks a sealed agent door (the KEK is the
-- HKDF of kdf_version 1); 0 and 1 stay what they were.
--
-- Additive: nine columns with defaults or NULL, two triggers, one UPDATE that touches only rows without an owner. Run again (a ledger rolled
-- back below 046), the ALTERs are skipped as already applied, the triggers exist, and the UPDATE fills only what is still empty. The
-- triggers that count unlock material (trg_umv_key_slot_update fires on any update; trg_umv_agent_update on the columns a door change
-- always writes) need no change.

ALTER TABLE tbl_key_slot ADD COLUMN door_version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE tbl_key_slot ADD COLUMN door_public_key BLOB;
ALTER TABLE tbl_key_slot ADD COLUMN dek_fp TEXT;
ALTER TABLE tbl_key_slot ADD COLUMN owner_user_id INTEGER;
ALTER TABLE tbl_key_slot ADD COLUMN created_by_user_id INTEGER;
ALTER TABLE tbl_key_slot ADD COLUMN label TEXT;

ALTER TABLE tbl_agent ADD COLUMN door_public_key BLOB;
ALTER TABLE tbl_agent ADD COLUMN dek_fp TEXT;
ALTER TABLE tbl_agent ADD COLUMN created_by_user_id INTEGER;

-- From now on the database keeps the owner of a user door itself: whatever writes tbl_user.key_slot_id (sign-up, provisioning at sign-in, a
-- password change, a join, the legacy-slot migration) makes that person the owner of the slot it points at.
CREATE TRIGGER IF NOT EXISTS trg_key_slot_owner_user_insert AFTER INSERT ON tbl_user
WHEN NEW.key_slot_id IS NOT NULL
BEGIN
    UPDATE tbl_key_slot SET owner_user_id = NEW.id WHERE slot_id = NEW.key_slot_id AND slot_type = 'user';
END;

CREATE TRIGGER IF NOT EXISTS trg_key_slot_owner_user_update AFTER UPDATE OF key_slot_id ON tbl_user
WHEN NEW.key_slot_id IS NOT NULL
BEGIN
    UPDATE tbl_key_slot SET owner_user_id = NEW.id WHERE slot_id = NEW.key_slot_id AND slot_type = 'user';
END;

UPDATE tbl_key_slot
SET owner_user_id = (SELECT u.id FROM tbl_user u WHERE u.key_slot_id = tbl_key_slot.slot_id ORDER BY u.id LIMIT 1)
WHERE slot_type = 'user' AND owner_user_id IS NULL
  AND EXISTS (SELECT 1 FROM tbl_user u WHERE u.key_slot_id = tbl_key_slot.slot_id);
