-- WP-11: durable tombstone bookkeeping for encrypted-at-rest search index segments (WP-09's
-- tbl_search_index_manifest). BeeMemoryBank.Search.Indexing.IndexBuilder tracks tombstones
-- in-memory only (SealedSegment.Tombstones); without a durable copy, an article updated or
-- deleted in one process lifetime -- tombstoning its occurrence in an already-persisted segment,
-- in memory only -- would have that stale content silently resurrected as "live" the next time
-- the segment is reloaded from disk after a restart, since nothing on disk remembered the
-- tombstone. This table closes that gap.
--
-- Local-only cache metadata, exactly like tbl_search_index_manifest: never synced, never
-- authoritative, safe to lose (losing a row just means one stale result might transiently
-- reappear until the next full index rebuild, not silent data corruption).
CREATE TABLE tbl_search_segment_tombstone (
    segment_id TEXT NOT NULL,
    article_id TEXT NOT NULL,
    PRIMARY KEY (segment_id, article_id)
);
