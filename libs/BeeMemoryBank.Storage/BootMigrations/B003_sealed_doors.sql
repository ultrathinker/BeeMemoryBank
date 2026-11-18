-- Sealed doors (vault migration 046, docs/adr/0012-sealed-doors.md): a door copy says which format it is, and a v2 door keeps the X25519
-- public key its box is sealed to. A public key, the same kind of value as the salt and the box beside it: no name, no secret. Every door
-- copied before this file is a v1 wrap.

ALTER TABLE tbl_boot_door ADD COLUMN door_version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE tbl_boot_door ADD COLUMN door_public_key BLOB;
