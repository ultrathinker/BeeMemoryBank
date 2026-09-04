-- Records that the master password was changed on ANOTHER node, so this node can say so.
--
-- Changing the master password rewraps the Master DEK under a new KEK in tbl_key_slot, and key
-- slots are node-local: they are neither synced nor carried in a join snapshot. So "change the
-- master password" is a per-node operation that reads like a network-wide one. Every other node
-- keeps accepting the OLD password — including at its own /api/join, which is the endpoint that
-- hands out mesh membership — and nobody is told.
--
-- The fix is deliberately NOT to ship key material between nodes. A peer that learns "the password
-- changed at T, on node N" knows enough to act; an admin then enters the new password on this node
-- by hand, which is the only way it can rewrap a local slot without the password crossing the
-- wire. These two columns hold that notice until they do.
--
-- Both nullable and both cleared by a local password change: the notice describes a gap between
-- this node and the rest of the mesh, so closing the gap is what removes it.

ALTER TABLE tbl_node_identity ADD COLUMN master_password_changed_elsewhere_at TEXT;
ALTER TABLE tbl_node_identity ADD COLUMN master_password_changed_by_node      TEXT;
