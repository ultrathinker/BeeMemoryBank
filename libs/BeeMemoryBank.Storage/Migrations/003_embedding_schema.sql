-- Secret projection matrix for embeddings (encrypted with master DEK)
CREATE TABLE tbl_projection_matrix (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    encrypted_matrix BLOB NOT NULL,
    iv BLOB NOT NULL,
    created_at TEXT NOT NULL
)
