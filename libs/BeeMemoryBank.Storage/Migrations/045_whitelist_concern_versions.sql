-- 045_whitelist_concern_versions.sql
--
-- One version per concern on a whitelist row (release 2.8, the same unreleased version as 044, so no extra upgrade step). Until now a row
-- had one version, that of the newest whitelist event that won it, and every event was judged against it. Two kinds of history therefore
-- ended differently on different nodes, depending on the order the events arrived in:
--  - a revoke and an update about the same peer that crossed (the update made by a node that had not seen the revoke yet, with a higher
--    Lamport time): a node that got the update first kept the peer, since the older revoke lost to the update's version;
--  - two updates of different fields that crossed (a demotion and a rename): a node that got the newer one first dropped the older one, and
--    when that was the demotion it kept the peer as a superadmin.
-- Now membership (status: a whitelist_add makes it active, a whitelist_revoke revoked), is_superadmin, display_name, api_address and the TLS
-- trust (tls_spki with tls_trust) each carry the version of the newest event that wrote THAT concern, and an event is judged per concern
-- (EventApplier.Whitelist.cs). lamport_ts/source_node_id stay, as the newest of the five (a join raises its clock past it).
--
-- Every concern starts at the row's version (its Lamport time and node): what an update or a revoke is compared against does not change at the
-- upgrade, ties included, and nothing is revoked or promoted by this migration. The fields (not membership) start UNDATED (undated_fields, a
-- bit per field as WhitelistConcerns numbers them: superadmin 2, name 4, address 8, TLS 16), because this node cannot name the event that wrote
-- each of them (the row's version may be that of a later rename; a join or a restore may have written the value). An add that is not a re-add
-- does not change an undated field, as an older build never let an add change an active row; an event that writes the field dates it. The
-- one-off repair at the next start (WhitelistOrderRepair) dates a field, and moves a concern to an older version, only where the node's own
-- log shows its writer.
--
-- Additive: eleven columns with defaults on a table of the baseline, nothing redefined. tbl_whitelist travels in no join (a join writes its
-- own rows from the host's answer) and its new columns are local bookkeeping of the replicated state, like lamport_ts.
--
-- Run again (a ledger rolled back below 045 over a database that has the columns: a test does it, an operator might), the ALTERs are
-- skipped as already applied, and the UPDATE touches only a row whose five versions are all still unset: one written before 045. Every
-- writer of this version gives at least one concern the row's version, so the versions the repair or the applier set are left as they are
-- (a row that is all at version 0 with no node is marked undated again, as this version marks it when it writes it).

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

UPDATE tbl_whitelist SET
    member_lamport_ts = lamport_ts,     member_source_node_id = source_node_id,
    superadmin_lamport_ts = lamport_ts, superadmin_source_node_id = source_node_id,
    name_lamport_ts = lamport_ts,       name_source_node_id = source_node_id,
    address_lamport_ts = lamport_ts,    address_source_node_id = source_node_id,
    tls_lamport_ts = lamport_ts,        tls_source_node_id = source_node_id,
    undated_fields = 30
WHERE member_lamport_ts = 0 AND superadmin_lamport_ts = 0 AND name_lamport_ts = 0 AND address_lamport_ts = 0 AND tls_lamport_ts = 0
  AND member_source_node_id IS NULL AND superadmin_source_node_id IS NULL AND name_source_node_id IS NULL
  AND address_source_node_id IS NULL AND tls_source_node_id IS NULL;
