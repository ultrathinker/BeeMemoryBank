CREATE TABLE IF NOT EXISTS tbl_folder_restriction (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NULL REFERENCES tbl_user(id),
    agent_id INTEGER NULL REFERENCES tbl_agent(id),
    folder_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE(user_id, folder_id),
    UNIQUE(agent_id, folder_id)
);
CREATE INDEX IF NOT EXISTS idx_folder_restriction_user ON tbl_folder_restriction(user_id);
CREATE INDEX IF NOT EXISTS idx_folder_restriction_agent ON tbl_folder_restriction(agent_id);
