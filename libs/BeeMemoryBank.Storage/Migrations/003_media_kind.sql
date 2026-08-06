-- Distinguishes inline images (referenced in an article's markdown body) from generic
-- file attachments (shown in a separate list below the article, never inlined). Existing
-- rows are all inline images, hence the default.
ALTER TABLE tbl_media ADD COLUMN kind TEXT NOT NULL DEFAULT 'image';
