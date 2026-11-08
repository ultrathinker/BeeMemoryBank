# ADR 0011: Paired nodes sync directly on the LAN, on trusted networks, through the sync surface only

## Status

Accepted (2026-11-07, the owner approved the plan and its recommendations D1 to D9, with one change: the LAN port is
a setting). Released in 2.8.0. The design and its implementation notes are in
[docs/design/lan-peer-sync.md](../design/lan-peer-sync.md).

## Context

A Mac joined a Windows PC with a join code on the LAN, then never synced with it directly: the Mac
stored the address of the join DOOR (closed after 15 minutes) and dialled it forever with an 18 s timeout, and the PC
stored the Mac with no address at all. Direct sync between two paired desktops existed only as a side effect of
"Devices on my network", which exposes the whole web UI. Two laptops with no server and no blind node would never sync.

## Decision

1. **Trust is permanent, doors are temporary, a peer has channels.** The pairing (node id, Ed25519 key, superadmin
   flag) lives in `tbl_whitelist` as before. The join code and its token stay a 15-minute door. The ways to reach a
   peer are a list (its primary address, what it announces about itself, what mDNS shows, what a person typed on this
   node), tried in order, remembering the one that worked last. Identity is never the address: an address is accepted
   only when the TLS key matches the peer's anchor and the node answers with the expected id and key.
2. **One LAN listener, one port, three surfaces (D6).** The sync surface (the node-key-authenticated peer routes and
   nothing else), the join door, and the web surface ("Devices on my network") share one port. A stored address of the
   form `https://192.168.1.220:5311` becomes a permanent sync address as soon as that node runs this version.
3. **The port is a setting** (owner's change to D6): default 5311, `BMB_LAN_PORT` overrides it for a run. Every surface,
   the mDNS record, the announced endpoints and the join code use the port the listener really holds. Addresses peers
   already store are not rewritten: an updated peer follows the new port through the announcement and mDNS; a 2.7.5
   peer keeps dialling the old one (a refused connection, answered at once on a LAN) until it is updated or its address
   for this node is changed by hand.
4. **Only on trusted networks.** The listener binds the addresses of trusted networks only, not `0.0.0.0` (the join
   door and the web surface excepted, as before). Windows: a network Windows calls Private or domain is trusted, Public
   is not, and the firewall rule is limited to `profile=private,domain remoteip=localsubnet`. macOS (and a future Linux
   desktop), **D1**: a per-network trust list keyed by a fingerprint of the IPv4 prefix and the router's IP and MAC
   address; the network a device was paired on is trusted by itself; any other private network is untrusted until the
   person says yes. Virtual adapters are never used; a VPN only with the person's opt-in for it (**D7**).
5. **The join door may open on an untrusted network (D2)**: it is an explicit action guarded by the token.
6. **`node_endpoints`, a clear Control event (D3)**, signed by the node about itself only (`payload.node_id` must be the
   author), carries at most eight https endpoints of trusted networks (private and VPN addresses) and the node's TLS
   anchors. It is written to its own register (`tbl_peer_endpoint`), last writer wins on the author's own Lamport time,
   never on the whitelist row's version. It travels **only between nodes that advertise `peer-endpoints-v1`**: the pull
   leaves it out for a token without the capability, the push withholds it from such a peer and moves the cursor over
   it. A locked, encrypted 2.7.5 node, whose relay inbox refuses a type it does not know, can therefore never stall on it.
7. **TLS trust survives a new certificate (D4).** The leaf keeps its key when it is issued again (new names, renewal);
   a new key only when it is missing or damaged. In addition a peer may be pinned by its local CA's public key
   (`ca-pin`): a leaf that key signed is accepted. The CA key comes from the join and from `node_endpoints`; a pinned row
   is upgraded locally to accept either, and the replicated `tls_spki` stays for older nodes. Nothing ever falls back to
   "accept any certificate". Each request of a session carries the anchor of the peer it is for, so peer A's address is
   never accepted with peer B's key.
8. **mDNS privacy (D5, v1)**: TXT `nodeId`, `ver`, `https`, `surface`; the node's name only while the web surface is on.
9. **One "Devices" section in Admin (D8)**, merging seven rows; **LAN sync on by default after the update (D9)**, with a
   one-time notice.

## Consequences

- Two paired laptops on a home Wi-Fi sync with nothing else; a DHCP change heals through mDNS or the announcement.
- A peer that is elsewhere is "not on this network" or "reachable through X", not "not answering"; a full peer silent
  for three days by any route is an alarm, like a blind node.
- The pre-authentication exposure of a desktop node on a trusted LAN is the sync handshake (identity, sentinel,
  challenge, authenticate), rate limited per address, with small bodies and bounded connections.
- Migration 044 (additive): snapshots, join archives and blind packages say schema 44, so a 2.7.5 node refuses to join
  from or be seeded by an updated one, as with every migration. Blind nodes are updated with the full nodes.
- Announcements spread only through updated nodes: until a server or blind node is updated, its relaying does not
  carry them, and mDNS and manual addresses cover the LAN.
