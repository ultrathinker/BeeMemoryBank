-- WP-15: chunked semantic embeddings. OnnxEmbeddingGenerator truncates any input to
-- MaxSequenceLength (256) tokens before embedding it, so a "needle" placed past that point in a
-- long article was invisible to semantic search -- it was never part of what got embedded. This
-- table holds one row per ~256-token chunk of an article (see BeeMemoryBank.Core.Embeddings.
-- ArticleChunker), so article-level semantic scoring can become the max over its chunks instead of
-- a single embedding of only the article's first ~256 tokens.
--
-- `projection` is int8-quantized (one byte per projection-matrix dimension) rather than the raw
-- float32 BLOB tbl_article.embedding_projection uses: an article can have several chunks, so the
-- in-memory cache scoring these at ~100k-article scale needs roughly 1/4 the per-vector footprint
-- float32 would cost to stay within the WP-15 RAM budget. `scale` is the per-chunk dequantization
-- factor (float = int8 * scale); see BeeMemoryBank.Core.Embeddings.Int8Quantizer.
--
-- Old full-document embedding_projection on tbl_article remains a fallback for articles that have
-- not been (re)chunked yet -- rows here are populated incrementally by the same background
-- PendingEmbeddingProcessor cycle that already writes embedding_projection, not a one-shot
-- migration-time backfill.
CREATE TABLE tbl_article_chunk_embedding (
    article_id    TEXT    NOT NULL REFERENCES tbl_article(id) ON DELETE CASCADE,
    chunk_index   INTEGER NOT NULL,
    projection    BLOB    NOT NULL,
    scale         REAL    NOT NULL,
    model_version TEXT    NOT NULL,
    PRIMARY KEY (article_id, chunk_index)
);
