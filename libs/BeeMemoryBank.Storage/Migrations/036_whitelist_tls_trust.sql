-- A full node can be called by blind copies (a "hub", ADR 0007, BMB-159): how a blind copy trusts the TLS
-- endpoint of a node it is told to call.
--
--   'pin'       the node's TLS key is pinned in tls_spki (a blind node's self-signed key, or a full node's own).
--   'public-ca' no pin: the certificate must validate through the operating system's chain (a Let's Encrypt
--               certificate behind a reverse proxy).
--   NULL        not set. A row with a pin and no mode is read as 'pin' (rows written by an older build).
--
-- Replicated: it travels in whitelist_add / whitelist_update (field tls_trust, optional — an older node ignores
-- it) and in snapshots with the rest of the row. Existing pins are the 'pin' mode.

ALTER TABLE tbl_whitelist ADD COLUMN tls_trust TEXT;

UPDATE tbl_whitelist SET tls_trust = 'pin' WHERE tls_spki IS NOT NULL AND tls_spki <> '';
