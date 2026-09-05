-- Drop the dead keyword-tag tables left behind by the "concept tags only" unification.
--
-- Keyword tags were removed in favour of concept tags (tbl_concept_tag / tbl_article_concept_tag);
-- the old tables were renamed to *_deprecated rather than dropped, and later migrations were
-- squashed, so tbl_tag / tbl_article_tag_deprecated no longer appear in any migration file at all.
-- A freshly-migrated database therefore never has them — this migration is a no-op there.
--
-- But a database created before the squash (e.g. the production node) still carries them, with
-- ~1900 rows in tbl_article_tag_deprecated pointing at a tbl_tag that no code reads. They are inert
-- (nothing selects, joins or writes them), but they show up as orphans in `PRAGMA foreign_key_check`
-- and are pure dead weight in every snapshot. Found during the migration rehearsal against a copy
-- of the production database.
--
-- IF EXISTS + child-before-parent order so this is safe on both shapes: the old DB that has them
-- and the fresh DB that does not. No data that any current code path can reach is removed.

DROP TABLE IF EXISTS tbl_article_tag_deprecated;
DROP TABLE IF EXISTS tbl_tag;
