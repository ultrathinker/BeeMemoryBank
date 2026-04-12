-- Add missing index for tag-based article search
CREATE INDEX IF NOT EXISTS idx_article_tag_tagid ON tbl_article_tag(tag_id);
