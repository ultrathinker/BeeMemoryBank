ALTER TABLE tbl_comment ADD COLUMN comment_id TEXT;
ALTER TABLE tbl_comment ADD COLUMN source_node_id TEXT;

-- Generate UUID v4 for existing comments
UPDATE tbl_comment SET comment_id = (
    lower(hex(randomblob(4))) || '-' ||
    lower(hex(randomblob(2))) || '-4' ||
    lower(substr(hex(randomblob(2)),2)) || '-' ||
    lower(substr('89ab', abs(random()) % 4 + 1, 1)) ||
    lower(substr(hex(randomblob(2)),2)) || '-' ||
    lower(hex(randomblob(6)))
) WHERE comment_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_tbl_comment_cid ON tbl_comment(comment_id);
