-- Phase 0: Read-only ACL for guest users
-- Adds is_read_only flag to folder ACL entries.
-- Semantics:
--   effect='deny'  + is_read_only=*  → no access at all (deny wins).
--   effect='allow' + is_read_only=0  → read + write (current behaviour, default).
--   effect='allow' + is_read_only=1  → read-only access.

ALTER TABLE tbl_folder_acl_entry ADD COLUMN is_read_only INTEGER NOT NULL DEFAULT 0;
