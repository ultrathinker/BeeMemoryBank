-- Per-user favorite ("starred") articles, pinned above the folder tree in the sidebar.
--
-- Node-local like tbl_user and tbl_agent: favorites belong to a user, users are not
-- replicated between nodes, so favorites must never appear in a sync event payload.
--
-- sort_order semantics: NULL on every row of a user means that user's list is in
-- automatic alphabetical order (the default for a new user). The first manual move
-- materializes the current alphabetical order into explicit sort_order values for ALL
-- of that user's rows at once, so a list is either fully automatic or fully manual --
-- never a half-ordered mix. Clearing every row back to NULL returns it to alphabetical.
--
-- ON DELETE CASCADE matters: foreign keys are enforced (PRAGMA foreign_keys=ON), so
-- without it a hard-deleted article or user would fail on a leftover favorite row.
CREATE TABLE tbl_favorite (
    user_id    INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE CASCADE,
    article_id TEXT    NOT NULL REFERENCES tbl_article(id) ON DELETE CASCADE,
    sort_order INTEGER,
    created_at TEXT    NOT NULL,
    PRIMARY KEY (user_id, article_id)
);

-- The primary key covers lookups by user, but the ON DELETE CASCADE on article_id has no usable
-- index without this one — a bulk hard-delete would scan the whole table once per article.
CREATE INDEX IF NOT EXISTS idx_favorite_article ON tbl_favorite(article_id);

