-- Phase 1: system folders
-- Reserved name '_Drafts' is treated as a protected folder once created:
-- service-layer code refuses Rename/Move/Delete on rows with is_system=1 and
-- TreeService omits empty system folders from /api/tree responses.

ALTER TABLE tbl_folder ADD COLUMN is_system INTEGER NOT NULL DEFAULT 0;
