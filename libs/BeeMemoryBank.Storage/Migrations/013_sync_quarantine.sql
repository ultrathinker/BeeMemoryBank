-- M5: persists SyncEventQuarantine's failure-tracking state (BeeMemoryBank.Sync), which used to
-- live only in a static in-memory dictionary — a node restart forgot every recorded failure, so a
-- permanently-bad event that had just been quarantined would immediately start blocking the pull
-- loop again on the very next cycle after the restart an operator ran specifically to "fix" the
-- stall. See SyncEventQuarantine.cs for the full quarantine-threshold rationale; this table only
-- adds durability, the threshold/"is this event quarantined" decision still lives in code so it
-- isn't the schema-visible state — no "is_quarantined" column, no migration needed if the
-- threshold constant ever changes.
--
-- Not FK-linked to tbl_event_log: a quarantined event's row there may not even exist locally yet
-- (pull-side failures can happen before the event is durably applied), and this table must keep
-- tracking a bad event regardless of whatever local state that event's own apply attempt left
-- behind.
CREATE TABLE tbl_sync_quarantine (
    event_id            TEXT    NOT NULL PRIMARY KEY,
    event_type          TEXT    NOT NULL,
    -- The event's origin/author node (SyncEvent.NodeId), NOT necessarily the peer we pulled it
    -- from — the same EventId can arrive via gossip relay from more than one peer, and all of
    -- those attempts count toward (and see) the same row, keyed by EventId alone. Mirrors
    -- SyncEventQuarantine's pre-existing in-memory keying; see its doc comment for why.
    origin_node_id      TEXT    NOT NULL,
    failure_count       INTEGER NOT NULL DEFAULT 0,
    first_failed_at_utc TEXT    NOT NULL,
    last_failed_at_utc  TEXT    NOT NULL,
    last_error          TEXT    NOT NULL
);
