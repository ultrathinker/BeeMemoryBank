-- Sentinel value: AES-256-GCM("BeeMemoryBank", masterDEK)
-- Used to verify DEK compatibility between nodes before sync
ALTER TABLE tbl_node_identity ADD COLUMN sentinel_value BLOB;
