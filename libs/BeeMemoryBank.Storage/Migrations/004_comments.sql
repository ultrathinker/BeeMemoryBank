CREATE TABLE IF NOT EXISTS tbl_comment (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    article_id  TEXT NOT NULL,
    text        TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tbl_comment_article ON tbl_comment(article_id)
