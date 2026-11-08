-- 044_peer_channels.sql
--
-- Direct sync between paired nodes on the LAN (release 2.8, docs/design/lan-peer-sync.md section 11, ADR 0011). Two new tables, nothing of the
-- baseline changed: an additive migration.
--
-- tbl_peer_endpoint is REPLICATED: what each node says about itself in its own signed node_endpoints event (the addresses its LAN listener serves the
-- sync surface on, and its TLS anchors: the pin of its leaf key and the public key of its local CA). One row per node, last writer wins on the
-- Lamport time of THAT node's own events only (it never competes with whitelist_update's whole-row version). lamport_ts 0 is a row learned at a
-- join, before the node announced anything: any announcement supersedes it. It travels in join snapshots (SnapshotTables.Replicated); blind nodes
-- relay the events and need no rows.
--
-- tbl_peer_channel is LOCAL: this node's own view of each way to reach a peer - the peer's primary address (tbl_whitelist.api_address), an address it
-- announced, one an mDNS record showed, one a person typed for it on this node - and how each went: when it was last seen, when it last worked
-- (the one that worked last is tried first), when and why it last failed. Never replicated, never in a snapshot (it is emptied like every table not
-- listed in SnapshotTables). Manual addresses pass PeerAddressPolicy, as every other address of a peer does.
--
-- IF NOT EXISTS: a ledger rolled back below 044 over a database that has the tables (a test does it, an operator might) runs it again harmlessly.

CREATE TABLE IF NOT EXISTS tbl_peer_endpoint (
    node_id         TEXT PRIMARY KEY,
    endpoints_json  TEXT NOT NULL,
    tls_leaf_spki   TEXT,
    tls_ca_key      TEXT,
    port            INTEGER,
    announce_seq    INTEGER NOT NULL DEFAULT 0,
    lamport_ts      INTEGER NOT NULL DEFAULT 0,
    event_id        TEXT,
    updated_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tbl_peer_channel (
    peer_node_id    TEXT NOT NULL,
    address         TEXT NOT NULL,
    source          TEXT NOT NULL,
    scope           TEXT NOT NULL,
    added_at        TEXT NOT NULL,
    last_seen_at    TEXT,
    last_ok_at      TEXT,
    last_fail_at    TEXT,
    last_fail_kind  TEXT,
    fail_streak     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (peer_node_id, address, source)
);

CREATE INDEX IF NOT EXISTS idx_peer_channel_peer ON tbl_peer_channel(peer_node_id);
