-- Content-addressed blob store — CONTRACT phase. Counterpart of 016 (expand).
--
-- 016 copied every article body and version ciphertext into tbl_blob and pointed the rows at it
-- by hash, but kept the inline `ciphertext` columns so the previous binary could still run and
-- the deployment could be rolled back. That was verified on the production node (every row
-- resolved to identical bytes through either path) before this file was written. This migration
-- drops the inline columns, and with them the last of the three copies each body used to have.
-- After this there is NO rollback to a pre-016 binary without restoring a database backup.
--
-- The guard comes first. It refuses to drop anything while a single row would lose its only copy
-- of the ciphertext: the INSERT below produces one NULL row for every body or version whose hash
-- points at no blob, and the NOT NULL constraint turns that into a failed statement — which fails
-- the whole migration (constraint errors are never treated as idempotent by MigrationRunner),
-- rolls back, and stops the node from starting with the columns still intact. Zero such rows,
-- zero inserts, and the drop proceeds.

CREATE TEMP TABLE bmb_contract_guard (must_not_exist INTEGER NOT NULL);

INSERT INTO bmb_contract_guard
SELECT NULL FROM tbl_article_body b
WHERE b.ciphertext IS NOT NULL
  AND (b.ciphertext_hash IS NULL OR NOT EXISTS (SELECT 1 FROM tbl_blob bl WHERE bl.hash = b.ciphertext_hash));

INSERT INTO bmb_contract_guard
SELECT NULL FROM tbl_article_version v
WHERE v.ciphertext IS NOT NULL
  AND (v.ciphertext_hash IS NULL OR NOT EXISTS (SELECT 1 FROM tbl_blob bl WHERE bl.hash = v.ciphertext_hash));

DROP TABLE bmb_contract_guard;

-- SQLite's DROP COLUMN rewrites the table in place and keeps its indexes (idx_article_version_article
-- among them) — unlike the create/copy/rename dance, which would have needed them recreated.
-- "no such column" on a re-run is treated as already-applied by MigrationRunner.
ALTER TABLE tbl_article_body    DROP COLUMN ciphertext;
ALTER TABLE tbl_article_version DROP COLUMN ciphertext;

-- The dropped columns held ~60MB on the production node; the pages go to the freelist and the
-- file only shrinks on VACUUM, which cannot run inside this transaction. MigrationRunner does it
-- right after this migration commits — see the marker it looks for:
-- bmb:vacuum-after
