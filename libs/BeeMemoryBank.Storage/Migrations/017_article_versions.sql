CREATE TABLE IF NOT EXISTS tbl_article_version (
    id TEXT PRIMARY KEY,
    article_id TEXT NOT NULL,
    version_number INTEGER NOT NULL,
    title TEXT NOT NULL,
    tags TEXT NOT NULL DEFAULT '[]',
    tree_path TEXT NOT NULL,
    ciphertext BLOB NOT NULL,
    iv BLOB NOT NULL,
    encrypted_dek BLOB NOT NULL,
    dek_iv BLOB NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (article_id) REFERENCES tbl_article(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_article_version_article ON tbl_article_version(article_id, version_number DESC);
