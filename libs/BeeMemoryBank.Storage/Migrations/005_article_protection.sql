-- 005_article_protection.sql
-- Per-article second-layer ("protected") encryption metadata.
-- protected: 1 when the body holds a passphrase-encrypted BMBENC1 blob. Derived from body content
--   on every write, so this column is a synced cache for UI (lock badge) — never the source of truth.
-- protection_hint: optional plaintext reminder phrase, shown on the lock screen BEFORE unlock.
-- ADD COLUMN is idempotent here: MigrationRunner treats a "duplicate column name" error as already-applied.
ALTER TABLE tbl_article ADD COLUMN protected INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_article ADD COLUMN protection_hint TEXT;
