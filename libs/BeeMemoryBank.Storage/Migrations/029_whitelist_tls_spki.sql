-- Blind nodes (BMB-43, plan 4.4): the pinned TLS key of a peer that serves HTTPS with a
-- self-signed certificate — a blind node, whose certificate lives in its own data volume and is
-- vouched for by nobody but the pair code it printed.
--
-- base64url(SHA-256(SubjectPublicKeyInfo)), unpadded. Replicated: it travels in whitelist_add / whitelist_update
-- (field tls_spki) and in snapshots with the rest of the row, because every full node dials the
-- blind node — the phone as much as the PC that paired it — and each must pin the same key.
-- NULL = no pin: the peer's certificate is checked the ordinary way.

ALTER TABLE tbl_whitelist ADD COLUMN tls_spki TEXT;
