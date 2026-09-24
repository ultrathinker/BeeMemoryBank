-- NOTE ON THE FILE NAME: despite "chat" in the name, this migration creates the GENERIC table
-- tbl_node_data_key. chat.db is its first (and today only) user, but the DEK-rotation rewrap that
-- re-seals these rows lives in BeeMemoryBank.Sync, which must not know about chat
-- (ChatIsolationGuardTests) — so the table is keyed by name and the rewrap walks every row without
-- knowing what each one protects. Migration files are never renamed (MigrationRunner tracks the
-- (version, filename) pair), so the name stays.
--
-- Node data keys: random 32-byte AES keys, each wrapped under the master DEK, that encrypt data a
-- host keeps OUTSIDE this database. The first (and today only) one is key_name = 'chat', which
-- encrypts everything in the API's chat.db (message content, tool-call arguments, attachments, LLM
-- provider API keys).
--
-- chat.db is a separate SQLite file that no DEK rotation can touch atomically. When its rows were
-- sealed directly under the master DEK, every rotation left the whole chat history, every
-- attachment and every stored provider key undecryptable. With this indirection a rotation only
-- has to re-wrap the rows of this table, and it does so inside the same main-database transaction
-- that re-wraps every other key-bearing row (DekRewrapper), on the initiator and on every applying
-- peer alike — so a data key can never end up sealed under a DEK the node no longer has. The table
-- is generic on purpose: the rewrap walks every row without knowing what each key protects.
--
-- Node-local, like the stores it protects: never synced (no event is ever logged for it), never
-- shipped in a peer/join package (not in SnapshotTables.Replicated, so the filter empties it),
-- wiped by a node reset. A joining node creates its own keys the first time it needs them.
--
-- wrapped_key is DekManager.WrapDek framing (version byte || ciphertext || tag) with AAD
-- "bmb-node-data-key-v1:" || key_name, so one row's wrapped key cannot be passed off as another's;
-- iv is its 12-byte GCM nonce.
CREATE TABLE IF NOT EXISTS tbl_node_data_key (
    key_name    TEXT    NOT NULL PRIMARY KEY,
    wrapped_key BLOB    NOT NULL,
    iv          BLOB    NOT NULL,
    created_at  TEXT    NOT NULL
);
