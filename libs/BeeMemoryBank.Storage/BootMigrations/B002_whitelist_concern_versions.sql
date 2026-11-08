-- The boot store's whitelist mirror keeps every column of the vault's tbl_whitelist (docs/design/step-b-spec.md 2.3): vault migration 045
-- added the version of each concern of a whitelist row (membership, superadmin, name, address, TLS trust) and which fields are undated. Lamport
-- times, node ids and a bit mask, the same kind of value as the lamport_ts and source_node_id the mirror already holds: no title, no path, no
-- name of a person, no key. The mirror
-- copies them with the rest of each row (BootStoreWriter); nothing that runs before unlock reads them.

ALTER TABLE tbl_whitelist ADD COLUMN member_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN member_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN superadmin_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN superadmin_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN name_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN name_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN address_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN address_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN tls_lamport_ts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tbl_whitelist ADD COLUMN tls_source_node_id TEXT;
ALTER TABLE tbl_whitelist ADD COLUMN undated_fields INTEGER NOT NULL DEFAULT 0;
