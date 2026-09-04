-- Content-addressed blob store — EXPAND phase.
--
-- Why: every article write logs an event whose JSON payload embeds the full encrypted body as
-- base64. Those exact bytes are already stored in tbl_article_body (while current) or
-- tbl_article_version (once superseded), so the event log holds a third copy, inflated 33% by
-- base64. Measured on the production node: tbl_event.payload = 42 MB of a 151 MB database, and
-- every one of the 1342 bodies embedded in events had a counterpart already stored elsewhere.
-- Addressing the bytes by hash lets an event reference them instead of carrying them, which also
-- stops the log from growing in proportion to content — the reason this matters at 100k articles.
--
-- EXPAND / CONTRACT: this migration only ADDS. The legacy `ciphertext` columns stay, populated,
-- so the previous binary still runs against a migrated database and a rollback is possible.
-- Migration 017 drops them once this has proven itself in production. Do not merge the two.
--
-- sha256() is not built into SQLite; it is registered by DbConnectionFactory.CreateConnection
-- (alongside unicode_contains) precisely so this backfill can be expressed in SQL, because
-- MigrationRunner only runs .sql resources. It exists for this file — application code hashes in
-- C#. One consequence: this migration cannot be replayed from the sqlite3 CLI.

CREATE TABLE IF NOT EXISTS tbl_blob (
    -- Lowercase hex SHA-256 of `data`. Hex rather than a BLOB so hashes stay readable in a
    -- sqlite3 shell and compare as ordinary TEXT; 64 bytes against bodies measured in kilobytes.
    hash       TEXT    PRIMARY KEY,
    data       BLOB    NOT NULL,
    size       INTEGER NOT NULL,
    -- Read by the garbage collector, which refuses to sweep anything younger than a grace period:
    -- a writer inserts the blob before it commits the row that references it, and without the
    -- grace window a sweep landing in between would delete a blob that is about to be referenced.
    created_at TEXT    NOT NULL
);

-- Nullable on purpose. NULL means "this row predates the blob store, read the inline ciphertext"
-- — the repositories fall back on it, so a half-migrated database is still fully readable.
ALTER TABLE tbl_article_body    ADD COLUMN ciphertext_hash TEXT;
ALTER TABLE tbl_article_version ADD COLUMN ciphertext_hash TEXT;

-- No foreign key to tbl_blob. Deliberate: a dangling hash must degrade to "content unavailable"
-- for that one article, never to a constraint violation that fails an unrelated write or blocks
-- the whole import. Referential integrity here is maintained by the GC's grace period and by the
-- pusher shipping blobs before events, not by the engine.
INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at)
SELECT sha256(ciphertext), ciphertext, LENGTH(ciphertext), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM tbl_article_body
WHERE ciphertext IS NOT NULL;

INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at)
SELECT sha256(ciphertext), ciphertext, LENGTH(ciphertext), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM tbl_article_version
WHERE ciphertext IS NOT NULL;

UPDATE tbl_article_body    SET ciphertext_hash = sha256(ciphertext) WHERE ciphertext IS NOT NULL;
UPDATE tbl_article_version SET ciphertext_hash = sha256(ciphertext) WHERE ciphertext IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_article_body_hash    ON tbl_article_body(ciphertext_hash);
CREATE INDEX IF NOT EXISTS idx_article_version_hash ON tbl_article_version(ciphertext_hash);

-- tbl_conflict_version also stores a ciphertext BLOB, and is deliberately left alone: conflict
-- copies are few, are never referenced by an event, and converting them would widen the blast
-- radius of this change for no measurable gain. The GC therefore does not need to consider it —
-- its bytes are simply not in the blob store.
