ALTER TABLE tbl_agent ADD COLUMN last_accessed_at TEXT;
ALTER TABLE tbl_agent ADD COLUMN request_count INTEGER NOT NULL DEFAULT 0;
