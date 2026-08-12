-- 005_fts5_metadata_index.sql
-- (renumbered from 004 during integration: migration 004 was already taken by a
-- sibling work package, WP-09's tbl_search_index_manifest, merged first.)
--
-- FTS5 inverted index over plaintext metadata so search can stop doing per-row
-- unicode_contains full scans (see WP-06 brief). Indexed columns are plaintext BY
-- DESIGN (docs/adr/0005-plaintext-metadata.md): article title + tree_path, folder
-- name + path, concept-tag name. Article bodies stay encrypted and are explicitly
-- out of scope here — adding a derived index over already-plaintext metadata
-- changes nothing about the encryption threat model.
--
-- Design notes:
--   * External-content mode (content='tbl_*'): the FTS index holds only inverted
--     terms keyed by the base table's implicit rowid, never a second copy of the
--     plaintext. The trade-off is the sync triggers below must use the FTS5
--     'delete' special command on UPDATE/DELETE rather than mirrored writes.
--   * Sync is done entirely in SQL AFTER INSERT/UPDATE/DELETE triggers so every
--     write path (web UI, MCP tools, RemoteEventApplier sync, CLI import) keeps
--     the index consistent with zero C# glue and nothing to forget.
--   * UPDATE triggers are scoped with `OF <cols>` to only the indexed columns.
--     The frequent embedding/status/updated_at UPDATEs that don't touch search
--     text must not re-index a 100k-row table on every touch. Soft-delete
--     (status flip) intentionally does NOT fire the trigger — the FTS index keeps
--     reflecting every row currently in the base table; the query side filters
--     status when it joins back (that wiring is a separate follow-up WP).
--   * Backfill uses the FTS5 'rebuild' special command. Unlike a plain
--     INSERT...SELECT it discards-then-rebuilds from the content table, so it is
--     safe to re-run: CREATEs below are skipped via MigrationRunner's existing
--     "already exists" idempotency on a ghost-hunter re-apply, and 'rebuild'
--     never appends duplicate terms.
--   * rowid: tbl_article and tbl_folder have TEXT PRIMARY KEYs but still carry
--     an implicit integer rowid; tbl_concept_tag.id IS INTEGER PRIMARY KEY (the
--     rowid alias). All three therefore key the FTS index on rowid uniformly.

-- fts_article: over tbl_article.title + tbl_article.tree_path ---------------

CREATE VIRTUAL TABLE fts_article USING fts5(
    title,
    tree_path,
    content='tbl_article'
);

CREATE TRIGGER trg_fts_article_ai AFTER INSERT ON tbl_article BEGIN
    INSERT INTO fts_article(rowid, title, tree_path)
    VALUES (NEW.rowid, NEW.title, NEW.tree_path);
END;

CREATE TRIGGER trg_fts_article_ad AFTER DELETE ON tbl_article BEGIN
    INSERT INTO fts_article(fts_article, rowid, title, tree_path)
    VALUES ('delete', OLD.rowid, OLD.title, OLD.tree_path);
END;

CREATE TRIGGER trg_fts_article_au AFTER UPDATE OF title, tree_path ON tbl_article BEGIN
    INSERT INTO fts_article(fts_article, rowid, title, tree_path)
    VALUES ('delete', OLD.rowid, OLD.title, OLD.tree_path);
    INSERT INTO fts_article(rowid, title, tree_path)
    VALUES (NEW.rowid, NEW.title, NEW.tree_path);
END;

-- fts_folder: over tbl_folder.name + tbl_folder.path ------------------------

CREATE VIRTUAL TABLE fts_folder USING fts5(
    name,
    path,
    content='tbl_folder'
);

CREATE TRIGGER trg_fts_folder_ai AFTER INSERT ON tbl_folder BEGIN
    INSERT INTO fts_folder(rowid, name, path)
    VALUES (NEW.rowid, NEW.name, NEW.path);
END;

CREATE TRIGGER trg_fts_folder_ad AFTER DELETE ON tbl_folder BEGIN
    INSERT INTO fts_folder(fts_folder, rowid, name, path)
    VALUES ('delete', OLD.rowid, OLD.name, OLD.path);
END;

CREATE TRIGGER trg_fts_folder_au AFTER UPDATE OF name, path ON tbl_folder BEGIN
    INSERT INTO fts_folder(fts_folder, rowid, name, path)
    VALUES ('delete', OLD.rowid, OLD.name, OLD.path);
    INSERT INTO fts_folder(rowid, name, path)
    VALUES (NEW.rowid, NEW.name, NEW.path);
END;

-- fts_tag: over tbl_concept_tag.name (id INTEGER PRIMARY KEY == rowid) ------

CREATE VIRTUAL TABLE fts_tag USING fts5(
    name,
    content='tbl_concept_tag'
);

CREATE TRIGGER trg_fts_tag_ai AFTER INSERT ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(rowid, name)
    VALUES (NEW.rowid, NEW.name);
END;

CREATE TRIGGER trg_fts_tag_ad AFTER DELETE ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(fts_tag, rowid, name)
    VALUES ('delete', OLD.rowid, OLD.name);
END;

CREATE TRIGGER trg_fts_tag_au AFTER UPDATE OF name ON tbl_concept_tag BEGIN
    INSERT INTO fts_tag(fts_tag, rowid, name)
    VALUES ('delete', OLD.rowid, OLD.name);
    INSERT INTO fts_tag(rowid, name)
    VALUES (NEW.rowid, NEW.name);
END;

-- Backfill: rebuild all three indexes from their content tables. On a fresh DB
-- this indexes the (empty) base tables; on an existing node upgrading from an
-- earlier schema it populates the index from the rows already in production.

INSERT INTO fts_article(fts_article) VALUES ('rebuild');
INSERT INTO fts_folder(fts_folder) VALUES ('rebuild');
INSERT INTO fts_tag(fts_tag) VALUES ('rebuild');
