-- Step B (docs/design/step-b-spec.md 4.3): a version number of everything the boot store of a sealed node copies - the unlock
-- material (key slots, the wrapped DEK of agents, the users that may unlock), the whitelist columns the relay authenticates
-- with, the public identity. Each change bumps it inside the writing transaction, so the boot store's mirror sees that it is
-- behind even when a writer forgot to tell it. No new table: the counter is a row of tbl_migration_marker. Plain nodes and
-- blind hosts carry the triggers unused.

INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
VALUES ('unlock_material_version', '0', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_insert AFTER INSERT ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_update AFTER UPDATE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_key_slot_delete AFTER DELETE ON tbl_key_slot
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_agent_insert AFTER INSERT ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Not last_accessed_at or request_count: those change on every agent request.
CREATE TRIGGER IF NOT EXISTS trg_umv_agent_update
AFTER UPDATE OF encrypted_dek, dek_iv, salt, kdf_version, key_hash, status, owner_user_id ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_agent_delete AFTER DELETE ON tbl_agent
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Who may unlock depends on the owner's role and state (the superadmin rule of the unlock).
CREATE TRIGGER IF NOT EXISTS trg_umv_user_update AFTER UPDATE OF role, is_active, key_slot_id ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_user_delete AFTER DELETE ON tbl_user
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_insert AFTER INSERT ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

-- Not the protocol, capability and version observations: those change on every authentication.
CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_update
AFTER UPDATE OF display_name, ed25519_public_key, api_address, status, is_superadmin, deleted_at, tls_spki, tls_trust ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_whitelist_delete AFTER DELETE ON tbl_whitelist
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_identity_insert AFTER INSERT ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;

CREATE TRIGGER IF NOT EXISTS trg_umv_identity_update AFTER UPDATE OF node_id, display_name, ed25519_public_key ON tbl_node_identity
BEGIN
    UPDATE tbl_migration_marker SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT), set_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE key = 'unlock_material_version';
END;
