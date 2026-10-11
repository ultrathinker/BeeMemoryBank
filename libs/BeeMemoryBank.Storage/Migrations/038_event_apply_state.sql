ALTER TABLE tbl_event ADD COLUMN apply_state INTEGER NOT NULL DEFAULT 0;  -- 0 applied, 1 held, 2 relayed, 3 quarantined after receipt
ALTER TABLE tbl_event ADD COLUMN hold_reason TEXT;                        -- 'locked' | 'key' | 'order' while held
ALTER TABLE tbl_event ADD COLUMN held_at TEXT;
ALTER TABLE tbl_event ADD COLUMN origin_superadmin INTEGER;               -- receipt verdict (6.5), held rows only
ALTER TABLE tbl_event ADD COLUMN local_type TEXT;                         -- opened view's type (full nodes)
ALTER TABLE tbl_event ADD COLUMN local_article_id TEXT;                   -- opened view's article id (full nodes)
ALTER TABLE tbl_event ADD COLUMN origin_peer TEXT;                        -- the peer that delivered it (pull source or pusher)
ALTER TABLE tbl_event ADD COLUMN origin_seq INTEGER;                      -- its sequence number in that peer's log
CREATE INDEX IF NOT EXISTS ix_event_held ON tbl_event (apply_state, sequence_num) WHERE apply_state <> 0;
