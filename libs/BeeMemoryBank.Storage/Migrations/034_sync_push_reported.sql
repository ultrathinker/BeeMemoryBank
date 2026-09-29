-- How far a peer says it has read our event log, as it reports it itself
-- (POST /api/sync/report-position) — as opposed to last_pushed_seq, which /api/sync/events also
-- advances when it merely SERVES a page, before that peer has applied anything. A blind node's log
-- trimmer cuts up to a position every active peer has actually acknowledged, so it must read this
-- column and not the delivery watermark (Codex round 2, security #3). NULL means "never reported",
-- which pins the log whole for that peer, exactly as a missing row did before.
ALTER TABLE tbl_sync_push_position ADD COLUMN reported_seq INTEGER;