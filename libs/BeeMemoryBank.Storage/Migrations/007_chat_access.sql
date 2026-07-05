-- Per-user AI chat access flag (node-local). When false, this user may not use the AI
-- chat feature (web UI); superadmins bypass this check entirely regardless of its value.
-- It is also ANDed with the node-wide chat_globally_enabled toggle in chat.db: both must
-- be true for a regular user to chat. Read/written only by the local API; never enters any
-- sync event payload, EventApplier, or tbl_event. Default 1 so existing users (and all new
-- users created before the setting existed) keep chat access until an admin turns it off.

ALTER TABLE tbl_user ADD COLUMN chat_access INTEGER NOT NULL DEFAULT 1;
