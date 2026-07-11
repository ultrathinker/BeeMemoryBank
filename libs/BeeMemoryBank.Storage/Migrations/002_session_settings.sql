-- Admin-configurable web login session (cookie) lifetime and sliding-expiration toggle.
-- Defaults match the previous hardcoded values' intent (was: 8h fixed, non-sliding) but
-- with a longer default (48h) and sliding ON by default, per superadmin request.
ALTER TABLE tbl_node_identity ADD COLUMN session_expire_hours INTEGER NOT NULL DEFAULT 48;
ALTER TABLE tbl_node_identity ADD COLUMN session_sliding_expiration INTEGER NOT NULL DEFAULT 1;
