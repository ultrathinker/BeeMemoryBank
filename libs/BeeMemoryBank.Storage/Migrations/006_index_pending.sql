-- WP-11: index_pending mirrors embedding_pending exactly (see 001_initial_schema.sql), but
-- drives the independent search-index background processor (PendingIndexProcessor) instead of
-- embedding generation. Every article starts pending, is cleared once PendingIndexProcessor has
-- folded its content into the in-memory inverted index, and is re-flagged whenever body content
-- changes (see ArticleService.UpdateAsync).
ALTER TABLE tbl_article ADD COLUMN index_pending INTEGER NOT NULL DEFAULT 1;

CREATE INDEX idx_article_index_pending ON tbl_article(id) WHERE status = 'A' AND index_pending = 1;
