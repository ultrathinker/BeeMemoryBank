-- Add metadata_json column to conflict versions for manual conflict recovery.
-- Stores title, tags, and treePath of the losing version so users can review conflicts.
ALTER TABLE tbl_conflict_version ADD COLUMN metadata_json TEXT;
