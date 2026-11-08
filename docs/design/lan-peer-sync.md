# Direct sync between paired nodes on the LAN: design plan

Status: **approved by the owner on 2026-11-07, with all recommendations D1 to D9 and one change: the LAN port is a setting
(section 5.1).** The decisions are [ADR 0011](../adr/0011-direct-lan-sync.md). Releases 1 and 2 of section 17 and the shared
parts of release 3 are implemented on `feat/lan-peer-sync` and ship together, with the platform ports of release 3 (the Admin
"Devices" section, the Android and iPhone full apps, the desktop shell and the blind desktop apps); section 19 records where the
code differs in detail from the plan below.

Base of every `file:line` below: the tree of `feat/setup-join-two-step` (code of 2.7.5 plus the branch). Line numbers
drift; the file and the symbol named next to them are what counts. Words: **MUST** is a contract other parts rely on; **SHOULD** is the
recommended way.

## Contents

1. The problem, seen on a real network
2. What the code does today
3. Goals and non-goals
4. The model: trust is permanent, doors are temporary, a peer has channels
5. The LAN listener: one port, three surfaces
6. Trusted networks (where the listener listens)
7. Channels: where a peer's addresses come from, and in which order they are tried
8. Announcing a node's own endpoints (the `node_endpoints` event)
9. TLS trust that survives a new certificate
10. Reachability states and alarms
11. Data model and migrations
12. Compatibility with 2.7.5 nodes
13. Decisions for the owner
14. Security review
15. UI: one "Devices" section, the join flow, manual addresses
16. Mobile, blind nodes, servers
17. Phasing, effort, tests
18. Separate small item: "withheld" is not "dropped" in the push log
19. Implementation notes

---

## 1. The problem, seen on a real network

A Mac joined a Windows PC (2.7.5) with a join code over the LAN. Afterwards:

- The Mac stored the PC's address as the join code's address, `https://192.168.1.220:5311`. That is the **join door**
  (`desktop/BeeMemoryBank.Node/Front/LanJoinListener.cs`), which serves five routes and closes itself after the snapshot download
  (15 minutes at most). The Mac's sync loop then dialled a closed door forever. First it got "no sync server at this address
  (404)" while the door was still up. After that every cycle hung for about 18 s with `Operation timed out (192.168.1.220:5311)`
  (in the Mac's log, repeated every minute).
- The PC stored the Mac with **no address**. A joiner never sends one (`InitEndpoints.cs:207`, `apiAddress = null`), so the PC never dials it.
- The two computers on the same desk therefore never synced directly. Everything went through the server and
  the blind node. With no server and no blind node (only a home LAN), **two paired laptops would never sync at all**, unless
  "Devices on my network" were switched on at the host. That switch exposes the whole web UI to the LAN, which is a different
  decision.

The product conflates two things:

- (a) showing the web UI to devices on the LAN (opt-in, sensible);
- (b) letting already-paired nodes sync directly over the LAN.

Today (b) exists only as a side effect of (a).

## 2. What the code does today

### 2.1 Listeners of a desktop node (Windows app, Mac app, Windows service)

All three network-facing listeners share **one port, TCP 5311, bound on `0.0.0.0`, IPv4 only**.

| Listener | File | Serves | When |
|---|---|---|---|
| Loopback front | `desktop/BeeMemoryBank.Node/Front/NodeFront.cs` (`Program.cs:400,503`) | HTTP `127.0.0.1:5310`. YARP sends `/api/**`, `/mcp`, `/health`, `/node/update/**` to the Api child and everything else to the Web child (`NodeFront.cs:154-211`). Strips `X-Internal-Key` and the identity headers from every request (`:232-273`). `/node/*` is loopback-only (`:280-295`). | Always |
| Join door `LanJoinListener` | `Front/LanJoinListener.cs` | HTTPS `0.0.0.0:5311`, exactly `POST /api/join`, `POST /api/join/abort`, `POST /api/sync/challenge`, `POST /api/sync/authenticate`, `GET /api/sync/snapshot/for-join` (`:59-66`), with a one-time token (`:127,330-338`) | Admin → Connect a device, 15 min (`:46`), closes after the snapshot plus 2 min for the abort (`:52`) |
| "Devices on my network" `LanFrontListener` | `Front/LanFrontListener.cs`, `NodeFrontBuilder.BuildNetworkFront` (`NodeFront.cs:492-513`) | HTTPS `0.0.0.0:5311`, the **whole** front: web UI, API, MCP | The per-profile setting `NodeNetworkSettings.DevicesOnMyNetwork` (`libs/BeeMemoryBank.Infrastructure/Network/NodeNetworkSettings.cs:11`, file `network.settings.json`, off by default) |
| Legacy `BMB_HTTPS_ENABLED=1` | `NodeFront.cs:107-133`, `Program.cs:407-409,519-560` | The loopback front itself also listens on `0.0.0.0:5311` | Environment variable |

What a LAN caller can reach: the front strips the internal key, so every caller on 5311 is keyless. It reaches exactly
`PublicSurface` (`libs/BeeMemoryBank.Hosting.AspNetCore/PublicSurface.cs:32-139`, enforced by `PublicSurfaceMiddleware`, first in
the Api pipeline). That includes the peer sync routes (`:64-88`: identity, sentinel, challenge, authenticate, events, report-position,
blobs, blobs/check, blobs/get, probe-relay, my-standing, snapshot/for-join, invite/*, leave), the blind, join, restore and
remote-account routes, `/health`, `/mcp`. With the full front it also reaches the **web UI** (sign-in page, password attempts).

The Api and Web children listen on `127.0.0.1:0` (`Program.cs:780,810`).

### 2.2 TLS

- `LocalCaService` holds two certificates. The CA is ECDSA P-256, valid 10 years, its key in DPAPI or the Keychain, the
  certificate in `certs/ca.crt`. The leaf is ECDSA P-256, valid 90 days, `certs/leaf.crt`, key `local-leaf`
  (`LocalCaService.cs:290-421`).
- The leaf's SANs are `localhost`, loopback, the machine name, `.local`, and every up, non-virtual LAN IPv4 (`:372-383,448-503`).
- The leaf is **re-minted with a NEW key pair** (`:345`) whenever the SAN set changes (any DHCP change), fewer than 30 days remain,
  or a file is missing (`:116-153`). `CachedLeafCert.Get()` checks this on every handshake (`NodeFront.cs:403-428`).
- A peer is trusted by **SPKI pin of the leaf**: `WhitelistEntry.TlsSpki`/`TlsTrust` (`pin` / `public-ca`), replicated in
  `whitelist_add` and `whitelist_update`.
  - `SpkiPinRegistry` looks pins up by **`Host:Port` of the row's `ApiAddress`** (`SpkiPinRegistry.cs:136-153`, 30 s cache).
  - A per-request `ExplicitPin` overrides it (`:31`).
- The joiner stores the host's pin from the join code (`InitEndpoints.cs:488`).
- **No code re-pins a peer when its leaf key changes.** After a DHCP change at the host, every peer that pinned it fails TLS until
  someone changes the row by hand. Today this rarely bites only because LAN sync rarely happens.

### 2.3 Peers and addresses

- `tbl_whitelist` (`040_baseline.sql:58-71`) has **one** address per peer, `api_address`, replicated by the superadmin-only events
  `whitelist_add` / `whitelist_update` (`EventPayloads.cs:279-324`).
- There is one runtime writer, `PeerAddressService` (`ChangeAsync`, `ApplyProofAsync`, `AdmitAsync`). Every writer goes through
  `PeerAddressPolicy` (private LAN and loopback allowed; link-local and metadata refused), guarded by
  `PeerAddressWritersInventoryTests` and `docs/design/peer-address-writers.golden.txt`.
- Join, joiner side:
  - The joiner stores the host at the typed URL or the join-code URL (`InitEndpoints.cs:478`).
  - It imports the host's other peers with their addresses (`:452`).
  - It sends no address of its own (`:207`; the same for mobile `NodeSetupService.cs:96` and `bmb join` `JoinCommand.cs:141`).
- Join, host side: the host stores the joiner as an address-less, "calls us" peer (`JoinEndpoints.cs:107-113,184`).

### 2.4 The sync loop

- `SyncScheduler` (60 s; push-on-save wakes it) walks the active rows **with a non-empty `ApiAddress`**, in display-name order,
  **strictly one after another** (`SyncScheduler.cs:233,245,262-282`).
  - The backoff sets are `UnreachablePeers` (doubling pause up to 15 min), `ClosedPeers` (blind, 401/403), `PausedPeers` (503 +
    `X-BMB-Paused`).
  - A dead LAN address costs the whole OS connect timeout (about 18 s on macOS) in every cycle in which it is tried.
- A session is dialler-driven and bidirectional: identity → challenge/auth → pull → report-position → push (`SyncClient.cs`).
  A node never pushes to a peer that only calls it; that peer pulls.

### 2.5 Discovery

- `MdnsAnnouncer` announces `_beememorybank._tcp.local`: instance = node id; TXT `nodeId`, `name`, `ver`, `https`; no pin, no
  address (`MdnsAnnouncer.cs`, `MdnsConstants.cs`).
  - On a desktop node it announces **only while "Devices on my network" is on and the listener really listens**
    (`ApiServices.cs:113-133`, `Program.cs:796-799`).
- `MdnsBrowser` exists. Its only user is the Setup page's "Find devices on my network" (`Setup.cshtml.cs:240-253`).
- Mobile has no mDNS. The Android app deliberately does not reference Infrastructure. iOS has `NSLocalNetworkUsageDescription`
  but no `NSBonjourServices`.
- The Mac app has both keys and `NSAllowsLocalNetworking` (`scripts/pack-macos-full.sh:385-395`).

### 2.6 Firewall and network category

- Windows has two rules:
  - `NetshLanFirewall` (`Front/LanFirewall.cs:53-120`) adds `"BeeMemoryBank device connect"` (`remoteip=localsubnet`, **no
    profile**), through UAC, only on a button press.
  - `FirewallService.EnsureInboundTcpRule` (legacy path) adds a rule with **no profile and no remote restriction**.
- The MSI has an optional feature with `Scope="any"` (`installers/windows/msi/Package.wxs:79-103`).
- macOS changes nothing; the system asks on the first incoming connection.
- **No code anywhere looks at the network category** (Windows Private/Public, or anything on macOS).

### 2.7 Status and alarms

- A full peer has a status, never an alarm. `PeerContactView.Build` (`server/BeeMemoryBank.Api/Models/SyncModels.cs:157-200`)
  says `not_answering` when it has an address, outbound calls fail, and nothing came in since.
  - Other states: `calls_us` (no address), `answering`, `unknown`.
  - Shown in Trusted Nodes, the header sync widget and the network map.
- Blind nodes have real alarms (`BlindAlarmService`, `/node/alarms`, tray notifications).
- Nothing distinguishes "not on this network" from "broken". `my-standing` already tells the caller, for each peer, when the
  responder last saw it (`MyStanding.peers[].last_protocol_seen_at`), but nothing uses that for reachability.

## 3. Goals and non-goals

Goals (agreed with the owner):

- **G1.** The join code and its token stay temporary (15 min). The trust a successful join creates is permanent.
- **G2.** After pairing, direct LAN sync works by itself, without "Devices on my network". Only the node-key-authenticated peer
  sync surface is exposed, never the web UI. It runs only on networks the OS (or the user) considers private.
- **G3.** A peer has a list of addresses (channels), tried in order, remembering the last one that worked. The sources are
  self-announcement, mDNS and manual entry. Several NICs are just more candidates. A wrong address is harmless, because identity
  is the node key.
- **G4.** TLS trust survives a re-issued certificate.
- **G5.** No false "not answering" for a laptop that is simply elsewhere. "Not on this network, reachable via relay" is different
  from a real problem.
- **G6.** The web UI on the LAN stays a separate opt-in.
- **G7.** One simple "Devices" section in Admin.

Non-goals (this plan):

- NAT traversal or hole punching between homes. Internet reachability stays a reverse proxy, a tunnel or a VPN
  (`docs/internet-access.md`). A VPN address can be a channel (decision D7).
- Phones listening. Phones stay clients (they dial; iOS cannot listen in the background anyway).
- Changing the sync protocol of a session (pull/push/auth stay byte-for-byte as in 2.7.5).

## 4. The model

Three separate things:

| Thing | Lifetime | Where |
|---|---|---|
| **Pairing trust**: the peer's node id, Ed25519 key, superadmin flag | permanent until revoked | `tbl_whitelist` (replicated) |
| **Doors**: the join code, the token, "Devices on my network" | temporary, or an explicit opt-in | the LAN listener's surfaces (section 5) |
| **Channels**: the ways to reach a peer (LAN addresses, internet address, relays) | change all the time; learned, not configured | `tbl_peer_endpoint` (replicated, announced by the peer itself) + `tbl_peer_channel` (local) (section 11) |

Identity is never the address. A session is accepted only when the TLS key matches the peer's trust anchor (section 9) and the
node answers `/api/sync/identity` with the expected id and key, then signs the challenge (already true: `SyncClient.cs`,
`PeerKeyMismatchException`). Hence any address may be tried, and a list of candidates is safe.

## 5. The LAN listener: one port, three surfaces

**Decision (D6, recommended): keep port 5311 and merge the two LAN listeners into one `LanListener` with three surfaces.**

- **Sync surface**: on whenever the node is a desktop node, initialized, not in invisible mode, and at least one current network
  is trusted (section 6). The Docker image and servers have no LAN listener; their reverse proxy is their door. The allow-list
  is:
  `GET /api/sync/identity`, `GET /api/sync/sentinel`, `POST /api/sync/challenge`, `POST /api/sync/authenticate`, `GET|POST
  /api/sync/events`, `POST /api/sync/report-position`, `POST /api/sync/blobs`, `POST /api/sync/blobs/check`, `POST
  /api/sync/blobs/get`, `GET /api/sync/my-standing`, `POST /api/sync/leave`.
  - It is a strict subset of `PublicSurface`. **Not** included: the web UI, `/mcp`, `/api/join*`, `snapshot/for-join` (join door
    only), `probe-relay`, `invite/*`, `/api/blind/*`, restore, remote accounts.
  - It MUST be one list in code (`LanSurfaces.Sync`), with a test that the list is a subset of `PublicSurface` and that each
    route other than identity, sentinel, challenge and authenticate requires a sync token.
- **Join surface**: the join door's five routes and its token logic, unchanged (`LanJoinListener`), added while a Connect-a-device
  session is open.
- **Web surface**: everything, as `LanFrontListener` does today, added while "Devices on my network" is on.

Why the same port:

- **Existing pairs heal by themselves.** The address the Mac stored, `https://192.168.1.220:5311`, becomes a permanent sync
  address as soon as the PC runs the new build: no migration and no re-pairing.
- One firewall rule, one port in the docs, one mDNS SRV.

`BMB_HTTPS_ENABLED=1` keeps its meaning: the loopback front then holds 5311 with everything, which includes the sync surface.

### 5.1 The LAN port is a setting (the owner's change to D6)

- One port for every surface, as D6 says, but chosen per profile: `network.settings.json` member `lanPort` (null = 5311),
  set on the Network settings card ("Port"); `BMB_LAN_PORT` overrides it for a run (two profiles on one computer, a Windows
  service, a test), and the card says so. 1024 to 65535, not 5300, 5301 or 5310 (the node's own loopback ports).
- What follows the port: the LAN listener (all three surfaces, rebound at once on a change, the join door closed first
  because its code names the old port), the join code (`LanJoinSession.Port`), the mDNS SRV record (the announcer reads
  the port the listener really holds from `network.listening.json`), the announced endpoints (`node_endpoints`, every URL
  carries the port, plus a `port` member), the join exchange, the Windows firewall rule (per port; the card offers to allow
  the new one) and the legacy `BMB_HTTPS_ENABLED` front.
- Already stored peer addresses are not rewritten (they are replicated `api_address` values, or a join code's). An updated
  peer heals by itself: its planner tries the announced and mDNS endpoints when the stored one fails (a refused connection
  on a LAN is immediate), and remembers the one that worked. A **2.7.5 peer** knows only the stored address and keeps
  failing at the old port until it is updated, or its address for this node is changed by hand (Trusted Nodes, "Change
  URL"); its status says "not answering" meanwhile. The card says this next to the port field.

Implementation shape:

- One Kestrel app on 5311, a request filter that checks the route against the union of the active surfaces, and YARP to the
  Api child (as `LanJoinListener` does now).
- The full-front surface keeps its Web cluster.
- Surface changes need no rebind.
- A change of the set of trusted interfaces rebinds (section 6).
- `/node/*` stays loopback-only.

Limits that MUST apply to the LAN listener before authentication (section 14):

- per-IP token buckets on `challenge` and `authenticate`;
- small body limits on every route except `POST events` / `POST blobs`, which require a token first (the current limits are
  hundreds of MB, `NodeFront.cs:118`);
- `MaxConcurrentConnections`, header timeouts and minimum data rates.

## 6. Trusted networks

The listener binds **to specific local addresses, not `0.0.0.0`**: the IPv4 addresses of interfaces that are up, not virtual or
tunnel (unless D7), and on a trusted network. It rebinds on `NetworkChange.NetworkAddressChanged` / `NetworkAvailabilityChanged`
(debounced, about 2 s). Leaving a café's Wi-Fi or joining one takes effect within seconds.

**Windows: the OS category.**

- Read `INetworkListManager` (netprofm COM, usable from `net10.0` by COM interop; the Node project is plain `net10.0`).
- An interface whose connection profile is **Private or DomainAuthenticated** is trusted. **Public is not.**
- Windows asks "make this PC discoverable?" when it joins a new network, and the user can change the category in Settings.
- Belt and braces: the firewall rule (section 6.2) is limited to `profile=private,domain` and `remoteip=localsubnet`, so even a
  detection error cannot open 5311 on a Public network.

**macOS and Linux desktop: no OS category exists. Decision D1.** Recommended: a **per-network trust list**, keyed by a network
fingerprint:

- The fingerprint is a hash of the interface's IPv4 prefix plus the default gateway's IP and MAC (from the ARP table).
- No Wi-Fi SSID: since macOS 14, reading it needs Location permission.
- The network on which a pairing happens is trusted automatically (the user just used it to pair).
- On any other private-range network (RFC 1918 / CGNAT excluded / ULA later) the Mac app asks once, with a native notification
  or tray item: "Sync with your paired devices on this network?" The choices are **Yes / Not this network**.
- Unknown networks are untrusted until answered.
- The list is per profile, next to `network.settings.json` (`network.trusted.json`), never in a snapshot.

Alternatives for D1:

- (b) Trust every RFC 1918 network. Simple, but a café's 10.x Wi-Fi counts as private.
- (c) Listen on every private-range network and rely on authentication only. Simplest; the exposure is only the sync surface
  (section 14).

The settings card (section 15) MUST show which networks are trusted and allow "forget this network". On Windows the card says
which category Windows gives the current network, and how to change it.

### 6.2 Firewall

- **Windows app** (per-user Velopack install, no elevation):
  - The first `Listen` on a non-loopback address makes Windows show its own "Allow access" dialog for `bmbd`. That dialog is the
    consent, and it creates an app rule.
  - The existing UAC button (`NetshLanFirewall`) stays for the case where the user dismissed it. Its rule changes to
    `profile=private,domain remoteip=localsubnet`.
  - The Admin card detects "no rule / blocked" and offers it.
  - A real inbound test needs a second machine. A paired peer can test via `probe-relay` (exists) and report "your firewall
    blocks direct sync" to the status.
- **Windows service**: the MSI feature, changed to `Scope="localsubnet"` and the private and domain profiles.
- **Legacy `EnsureInboundTcpRule`** (`BMB_HTTPS_ENABLED=1`): keeps its old wide scope (owner's decision, 2026-11-08; section 19).
- **macOS**: the signed app gets the system "Accept incoming connections?" prompt on the first listen when the application
  firewall is on. Nothing to do. The card shows a hint if no peer has ever reached this node on the LAN.

## 7. Channels: where a peer's addresses come from, and the order

The **candidate sources, all active at once**:

| Source | What | Lifetime | Phase |
|---|---|---|---|
| `primary` | today's `api_address` (replicated, admin-set; also the join URL) | until changed | exists |
| `announced` | the endpoints the peer signs about itself (`node_endpoints`, section 8) | replaced by the next announcement | 2 |
| `mdns` | an instance of `_beememorybank._tcp` whose TXT `nodeId` is this peer, resolved to IPv4:port | 10 min after last seen | 1 (desktop), 3 (mobile) |
| `manual` | one or more addresses typed for this peer on THIS node (`192.168.1.20`, `desktop.local`, `https://desktop.lan:5311`) | until removed | 2 |
| `join` | the address a join succeeded through | as `manual`, kept until an announcement or mDNS supersedes it | 1 (exists as `primary`) |

**Order of a session's attempt for a peer:**

1. the channel that worked last (persisted, so it survives restarts);
2. the other **LAN** candidates whose address is on a subnet of one of this node's current interfaces, all of them **in
   parallel** (Happy-Eyeballs style: start them about 250 ms apart and take the first that completes TLS and identity), each with
   a **short connect timeout (1.5 s)**;
3. the internet candidates (`primary` when it is not a private address, announced public URLs), with the normal timeout;
4. nothing more: relays are not dialled, they are the other peers of the cycle (section 10 reads what they say about this peer).

A LAN candidate on a subnet this node is not on is **not dialled at all**: no 18 s timeouts while travelling. A candidate succeeds
only when TLS matches the peer's anchor and identity and key match. Then `last_ok_at` is set and it is used first next time.
Failures are recorded per channel, and `UnreachablePeers` backoff moves from "per peer address" to "per peer, all channels
failed".

The **per-request pin** replaces lookup by host:port. `SyncClient` sets the peer's anchor on every request (the existing
`ExplicitPin` option, extended to the CA anchor of section 9). `SpkiPinRegistry` keeps the host:port lookup only for callers that
dial a row's primary address without a peer context. A test MUST show that a manual or mDNS address of peer A is never accepted
with peer B's pin.

The **cycle**:

- still one session per peer per cycle; the first channel that answers is used;
- LAN peers first, so a home network is served before a slow internet hop;
- the sequential loop stays, with the dead-address cost now bounded at about 1.5 s;
- parallel sessions across peers is a later optimisation, not needed here.

**The address-less row** ("calls us") becomes callable as soon as it has any channel (announced, mDNS or manual). The desktop will dial
the Mac too.

## 8. Announcing a node's own endpoints: the `node_endpoints` event

A node signs a small record about itself: the endpoints at which it currently listens, and its TLS anchors. It is published
as a **new Control event type `node_endpoints`**.

```json
{ "node_id": "<author>", "seq": 17, "endpoints": [ {"url": "https://192.168.1.220:5311", "scope": "lan"},
                                                  {"url": "https://100.64.0.1:5311", "scope": "vpn"} ],
  "tls_leaf_spki": "<b64url>", "tls_ca_spki": "<b64url>", "port": 5311 }
```

- **Who may author:** any whitelisted full node, **only about itself**. The applier requires `payload.node_id == evt.NodeId`,
  as `recovery_box_set` does (`EventApplier.Recovery.cs:55-63`). Blind nodes cannot author anything (`EventInvariants`).
- **Register:** `tbl_peer_endpoint`, one row per node, last writer wins on the event's Lamport time **of the author's own
  events only**. It never competes with `whitelist_update`'s whole-row version.
  - That matters: a self-announcement sharing the whitelist row's version would make an admin's concurrent rename lose on some
    nodes and win on others (`whitelist_update` with `null = unchanged` is decided as a whole, `EventApplier.Whitelist.cs:209-215`).
    That is a divergence, not just a lost write. This is why extending `whitelist_update` with optional fields (the `tls_trust`
    precedent) is **rejected** here.
- **When:**
  - at start once the listener is up;
  - when the bound endpoint set or a TLS anchor changes (debounced 30 s);
  - otherwise at most once every 7 days as a keep-alive, so a node that joined after a compaction learns it from the log too.
  - It also travels in join snapshots: `tbl_peer_endpoint` is added to `SnapshotTables.Replicated`; older joiners ignore tables
    outside their own list.
- **Content rules (applier):**
  - at most 8 endpoints;
  - each passes `PeerAddressPolicy`; `scope=lan` requires a private-range literal or a `.local` name; `vpn` requires CGNAT or ULA;
  - pins are well formed or absent;
  - anything else drops the whole payload, as quarantine does.
- **Only on a trusted network:** only addresses of trusted interfaces are announced. A café address is never published.
- **Privacy (D3):** a Control event is clear after activation, so blind nodes and relays see these LAN addresses. ADR 0009 already
  accepts "peer display names, addresses, TLS pins" in clear (`docs/adr/0009-metadata-confidentiality.md:108-111`), and
  `api_address` is clear today. The new fields MUST be added to `docs/design/protocol4-clear-fields.golden.txt` deliberately
  (`ClearFieldGoldenTests`).
  - Alternative: send it sealed. Blind nodes then see nothing, but a locked node cannot read new endpoints until unlocked, and
    sealed events follow the content rules (held, ordered). Recommended: clear, with only private and VPN endpoints in it.
- **Compatibility is the hard constraint** (section 12):
  - A 2.7.5 node accepts and relays an unknown type, except a **locked, encrypted** 2.7.5 full node, whose relay inbox refuses
    unknown types. Refused events end the accepted prefix, and the pusher re-pushes the same page every cycle until that node is
    unlocked (`server/BeeMemoryBank.Api/Services/Sealed/SealedRelay.cs:396-404`, `SyncClient.cs:546`). A server restarted and left
    locked would stall all pushes to it behind one announcement.
  - Therefore `node_endpoints` **MUST travel only between nodes that advertise a new capability `peer-endpoints-v1`**:
    - Pull: the server filters it out of `GET /api/sync/events` for a token without the capability, the way `FillBlindPageAsync`
      filters clear content.
    - Push: the client withholds it from a peer whose identity lacks the capability, the way it withholds clear content from blind
      peers, and moves the cursor over it.
  - So no 2.7.5 node ever holds one, and no 2.7.5-to-2.7.5 push can stall on it. The cost: endpoints propagate only through
    updated nodes (the server must update for its relaying to help). Until then, mDNS and manual addresses cover the LAN.

The join also exchanges endpoints directly, both ways, as optional JSON members. Minimal APIs ignore unknown members, and no
reader in the tree sets `UnmappedMemberHandling`:

- the joiner sends its `endpoints` and `tls_ca_spki` in `POST /api/join`;
- the host returns its own in the answer.

The host stores the joiner's announced endpoints immediately, so the "calls us" row is callable from the first minute.

## 9. TLS trust that survives a new certificate

**Decision D4, recommended: both of these.**

1. **Phase 1: the leaf keeps its key.**
   - `LocalCaService` re-mints the leaf (new SANs, new validity) **with the existing key** when the key is present and readable.
   - A new key only when it is missing or corrupt, or by an explicit "rotate" (none today).
   - Every existing pin (the Mac's pin of the desktop, blind and hub pins) keeps working through DHCP changes and the 90-day renewals.
   - A one-file change with a unit test. It also helps 2.7.5 peers that pinned an updated node.
2. **Phase 2: a new trust mode `ca-pin`.**
   - The peer's local-CA SPKI is pinned. Validation builds a chain to that CA (`X509ChainTrustMode.CustomRootTrust`, name
     mismatch ignored; the key check is the node identity).
   - The CA anchor comes from the join (optional member) and from `node_endpoints`. The CA lives 10 years, its key in DPAPI or the
     Keychain.
   - A leaf rotation then never breaks anything.
   - A node holding a `pin` row whose peer announces a matching `tls_leaf_spki` plus a CA anchor upgrades the row **locally** to
     accept either. The replicated `tls_spki` stays for old nodes.

The join code keeps carrying the **leaf** pin (old joiners compare it with the presented leaf).

Explicitly not chosen: dropping TLS pinning and relying on the Ed25519 challenge alone. A man in the middle could still read the
bearer token and the clear control traffic.

## 10. Reachability states and alarms

Each peer gets one state, computed per cycle from the channels and from what other peers report:

| State | Meaning | Shown as |
|---|---|---|
| `direct_lan` | a LAN channel answered in the last 2.5 intervals | "Direct, on this network" |
| `direct_internet` | an internet channel answered | "Direct, over the internet" |
| `calls_us` | it called us recently (inbound contact) | "Calls this device" |
| `via_relay` | no direct channel answered, but a relay saw it recently: a peer's `my-standing.peers[].last_protocol_seen_at` (exists in 2.7.5), or an event authored by it arrived via another peer within the window | "Not on this network; reachable through Home-Server (2 min ago)" |
| `away` | only LAN channels exist and none is on a current subnet, and no relay evidence | "Not on this network" (grey, no alarm) |
| `old_version` | its LAN channels fail (a 2.7.5 desktop's 5311 is closed: timeout, or 404 while a door is open) and the app version it last showed (`tbl_whitelist.last_app_version`) is below the LAN-sync release | "Direct LAN sync needs an update on Desktop" |
| `not_answering` | a candidate on a current subnet or the internet fails, **and** no relay evidence for the window | "Not answering since …" (today's text) |
| `silent` | no direct contact and no relay evidence for 3 days | alarm (as for blind nodes: tray notification, notice line) |

The full-peer alarm is new and has the same thresholds and suppression as the blind one (`BlindAlarmService`: nothing in
invisible mode, nothing if this node's own last cycle is over 20 min old). It answers "a real problem" without crying wolf for a
laptop on a trip.

## 11. Data model and migrations

Migration `044_peer_channels.sql`, additive (add `44` to `MigrationRunner.AdditiveMigrations`):

```sql
-- replicated register: what each node says about itself (node_endpoints), one row per node
CREATE TABLE tbl_peer_endpoint (
  node_id TEXT PRIMARY KEY, endpoints_json TEXT NOT NULL, tls_leaf_spki TEXT, tls_ca_spki TEXT,
  announce_seq INTEGER NOT NULL, lamport_ts INTEGER NOT NULL, updated_at TEXT NOT NULL);
-- local only: this node's view of each channel (never replicated, never in a snapshot)
CREATE TABLE tbl_peer_channel (
  peer_node_id TEXT NOT NULL, address TEXT NOT NULL, source TEXT NOT NULL,   -- primary|announced|mdns|manual
  scope TEXT NOT NULL,                                                       -- lan|vpn|internet
  added_at TEXT NOT NULL, last_seen_at TEXT, last_ok_at TEXT, last_fail_at TEXT, last_fail_kind TEXT, fail_streak INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (peer_node_id, address, source));
```

- `tbl_whitelist.api_address` stays as the replicated primary address. Old nodes see only it.
- `tbl_peer_endpoint` joins `SnapshotTables.Replicated` and the blind package's allow-list only if blind nodes need it; they do
  not, they relay events.
- `tbl_peer_channel` joins `StrippedByDropping`, like the sync positions.
- Manual addresses are written by one service with the `PeerAddressPolicy` rules, and the writers inventory test and golden file
  are extended to `tbl_peer_channel`.
- Effect on 2.7.5: snapshots, join archives and blind packages will say schema **44**. A 2.7.5 node then refuses to join from an
  updated host or take its seed ("upgrade this node first"), as with every migration. A network updates its blind nodes with its
  full nodes (`docs/sync.md`, "Older nodes, and the schema number").

## 12. Compatibility with 2.7.5 nodes

The network this plan started from has a Windows desktop (2.7.5), a Docker server (2.7.5), a blind node (Docker, 2.7.5, `203.0.113.10:5610`),
the Mac (2.7.5+branch), and possibly phones.

| Situation | Behaviour |
|---|---|
| New node ↔ 2.7.5 node, any channel | Session protocol unchanged: works as today |
| 2.7.5 node dials a new desktop at `https://ip:5311` | Works: the sync surface is there (heals the Mac→desktop direction once the desktop updates) |
| New node dials a 2.7.5 desktop's LAN address | 404 or timeout as today, now `old_version` / `away` without an alarm; after the desktop updates, direct |
| `node_endpoints` | Never sent to or served to a peer without `peer-endpoints-v1` (section 8). 2.7.5 nodes never store it; no stall |
| Join, new joiner → 2.7.5 host | Extra JSON members ignored; the joiner gets no host endpoints and falls back to the join URL + mDNS |
| Join, 2.7.5 joiner → new host | Works (host ignores missing members). Refused only by the schema-number rule if the host's archive is schema 44 (as with every migration) |
| `tls_ca_spki` | Local use only; replicated `tls_spki` unchanged, so old nodes keep pinning the (now stable) leaf |
| Blind node 2.7.5 | Relays everything it relays today; never gets `node_endpoints`. Updated blind nodes advertise the capability and relay it |
| Phones 2.7.5 | Unchanged |

Degrades gracefully: until the server and the blind node update, announcements spread only directly between updated nodes. mDNS and
manual addresses still find peers on the LAN.

## 13. Decisions for the owner

| # | Question | Recommended | Alternatives |
|---|---|---|---|
| D1 | Private network on macOS (and a future Linux desktop) | Per-network trust list; the pairing network is trusted automatically; ask once for others | (b) every RFC 1918 network; (c) always, auth only |
| D2 | May the join door open on an untrusted network? | Yes, with a warning on the Connect card (an explicit action, token-guarded) | refuse until the network is trusted |
| D3 | Endpoint announcements in clear or sealed | Clear Control event, only private/VPN endpoints, golden file updated | sealed (private from blind nodes; unreadable while locked) |
| D4 | TLS anchor | Stable leaf key now; CA pin in phase 2 | CA pin only; announced leaf pins only |
| D5 | mDNS privacy | v1: TXT `nodeId`, `ver`, `https`, `surface`, no name unless the web surface is on | blinded id: HMAC(key derived from the DEK, nodeId ‖ hour); only members recognise it (phase 3) |
| D6 | Port | Keep 5311 for all surfaces | a separate sync port (existing stored addresses would not heal) |
| D7 | VPN / tailnet interfaces (a desktop on a tailnet has `100.64.0.1`) | Opt-in per interface ("use my VPN for sync"), scope `vpn` | always; never |
| D8 | Admin merge scope | One "Devices" section replacing seven rows (section 15) | keep pages, only add channels to Trusted nodes |
| D9 | Default after update | LAN sync on for trusted networks, one-time notice "Direct sync with your devices on this network is on" | off until switched on |

## 14. Security review

What an attacker on the same LAN can do against the sync surface:

- **Before authentication:**
  - `GET identity`: node id, display name, public key, app version, platform. Recommendation: on the LAN listener answer the
    name only if the web surface is on; clients do not need it to verify.
  - `GET sentinel`: a DEK-derived check value, 256-bit, no offline oracle.
  - `POST challenge`: server state per call. MUST be bounded (store cap plus per-IP bucket, e.g. 10/min, burst 20).
  - `POST authenticate`: one Ed25519 verify per call; same bucket. Succeeds only for a key in this node's whitelist.
  - Everything else needs a token issued to a whitelisted node.
  - Same exposure as today's `PublicSurface` on a server, minus join, invite, blind, restore and remote accounts.
- **Resource attacks:**
  - Kestrel limits: `MaxConcurrentConnections` (32), `MaxConcurrentUpgradedConnections` 0, `KeepAliveTimeout`,
    `RequestHeadersTimeout` 10 s, minimum data rate.
  - Body limits of 16 KB on pre-auth routes. The large push and blob bodies are allowed only on an authenticated route; this
    needs the token check before the body is read.
  - The TLS handshake costs CPU; the connection cap plus per-IP connection rate (e.g. 30/min) bound it.
- **Impersonation:**
  - mDNS spoofing can only add candidates. A fake candidate fails the pin and key check before anything is sent; the client
    sends no bearer token before TLS matches.
  - Cap candidates per peer (8 mDNS, 8 announced, 8 manual) and expire mDNS ones.
- **Malicious announcements:**
  - Signed by the subject, `author == subject`, whitelisted nodes only.
  - A compromised paired node could announce addresses of other LAN hosts and make peers send TLS ClientHellos and
    `GET /api/sync/identity` to them: a small, bounded probe.
  - Mitigations:
    - `PeerAddressPolicy` plus `PeerSocketConnector` (no link-local or metadata addresses);
    - `scope=lan` addresses are dialled only on a current subnet;
    - candidates are capped;
    - a candidate that failed identity is backed off for 1 h.
  - A compromised paired node can already do far worse (it holds the DEK).
- **Private-network detection pitfalls:**
  - Windows: a user may mark hotel Wi-Fi "Private"; the firewall rule's `localsubnet` and private profile still bound it.
  - "Unidentified network" defaults to Public, so LAN sync stays off there; the card tells the user why.
  - macOS: a fingerprint by gateway MAC can be cloned by a hostile network that impersonates the home router (same IP and MAC);
    then the exposure is the sync surface above, nothing more.
  - Virtual adapters (WSL `172.25.x`, VirtualBox `192.168.56.x`, Docker bridges) MUST be excluded, as `LocalCaService` already
    excludes them for SANs; else a guest VM could reach the listener.
- **Downgrade:** a peer announcing no CA anchor keeps the leaf pin; nothing falls back to "accept any certificate".
- **What is not opened:** the web UI, MCP, the join routes outside a join session, and `/node/*` (loopback only).

## 15. UI

### 15.1 One "Devices" section

The Admin index section "Nodes and network" (`AdminPageCatalog.cs:74-131`) today lists seven rows: Restored devices, Node
Identity, Trusted Nodes, Connect a device, Devices on my network, Invisible Mode, Blind nodes. The header widget and the network
map live elsewhere.

Proposal: **Admin → Devices**, three pages. Old routes redirect (`LegacyPageRedirect`, `LegacyAnchors`).

```
Admin › Devices                                                     [ Connect a device ]  [ Add a blind node ]
┌──────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ This device: Laptop · Mac · 2.7.6                                                                   [▾]  │
│ On this network: Home Wi-Fi (trusted) · listening for your devices on 192.168.1.42:5311                   │
│ (Restored devices waiting for confirmation: 1  [Review])                                                 │
├──────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ ● Desktop          Windows · 2.7.5   Direct, on this network (192.168.1.220) · in step                    │
│ ● Home-Server      Server · 2.7.5    Direct, over the internet · in step                                  │
│ ◐ Laptop-2         Mac · 2.7.6       Not on this network · reachable through Home-Server (5 min ago)      │
│ ● Blind node       Blind · 2.7.5     Direct · backup 2 h ago                                              │
│ ○ Pixel            Phone · 2.7.6     Calls this device · last 3 h ago                                     │
│ ⚠ Old-PC           Windows · 2.6.2   Not heard from for 4 days                                [Details]   │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
  [ Network map ]  [ Network settings ]  [ Event log & compaction ]
```

Device details (one page per peer, replacing the Trusted Nodes row actions and the Blind nodes card):

```
Desktop · node id …  · key …                                      [Revoke]
Status: Direct, on this network · last change delivered 1 min ago
Ways to reach it                                  source       last worked
  https://192.168.1.220:5311                      announced    1 min ago   ✓ (used first)
  https://desktop.local:5311                      mDNS         seen 3 min ago
  https://100.64.0.1:5311                         announced    —  (VPN, not used: VPN off)
  https://my-pc.example.org                       address      never
  [ + Add an address ]   [ Change internet address ]
Also reaches you through: Home-Server, Blind node
Rights: superadmin [x]  · auto-accept restore [ ] · auto-accept key rotation [ ]
TLS: pinned (local CA …)   · Let blind copies call this node [ … ]
(blind only) Reseed · Invitations · Alarms
```

Network settings (replacing "Devices on my network" and part of "Connect a device"):

```
Sync with your devices on this network     [on]  (only the sync connection, never the web page)
  Trusted networks: Home Wi-Fi ✓ (paired here) · Office ✓ · [forget]
  Windows: this network is "Private" ✓ / "Public": direct sync is off here — change in Windows Settings
  Firewall: allowed ✓ / blocked [Allow…]
Show this node's web page to devices on my network   [off]   (today's "Devices on my network")
  CA certificate for browsers  [Download ca.crt]  (steps)
Use my VPN for sync (Tailscale 100.64.0.1)    [off]
Invisible mode                                [off]
```

Merged or removed:

- Trusted Nodes, Blind nodes (list part), Node Identity and Restored devices → Devices / device details.
- Devices on my network and Invisible Mode → Network settings.
- Connect a device → a dialog from the Devices header (the join code page itself stays as the dialog's content).
- The Network map stays, linked from Devices and from the header widget.
- Blind invitations stay a sub-page of Add a blind node.
- Event log & compaction stays under "Backups and history" and is linked here.
- The header widget's rows use the new states.

### 15.2 Join flow wording

- On Connect a device (host): "This code works for 15 minutes on this network. Once the other device has joined, the two keep in
  step by themselves: directly whenever both are on a trusted network, and through your server or blind node when they are apart.
  Your web page stays private."
- On the joiner's success page: "Joined Desktop. They will sync directly on this network (Home Wi-Fi). On other networks they sync
  through Home-Server."
- Setup: "Find devices on my network" moves under "Connect with a code" and finds nodes by their sync announcement. A found node
  still needs its code, which the hint says. Today's hint ("only nodes with Devices on my network on") goes.

### 15.3 Manual addresses

On device details: **Add an address**.

- The input takes an IP, a host name or a URL; `:5311` and `https://` are implied.
- It is checked with `PeerAddressPolicy`, then probed: identity, key and pin must match **this** peer. "No answer" may still be
  saved, with a note.
- Stored as `manual` on this node only. Shown with a "this device only" note. Removable.
- The same on the phone's peer page later.

## 16. Mobile, blind nodes, servers

- **Android full app.**
  - It dials, never listens, and benefits at once from announced and manual channels.
  - mDNS via `NsdManager` (no Infrastructure reference needed; NSD handles multicast itself). Discovery only while the app's sync
    runs. Android 13+ needs `NEARBY_WIFI_DEVICES` only for Wi-Fi Aware, not NSD; to verify on a device.
  - A LAN channel is used only when the phone's network is the same subnet.
- **iOS full app.**
  - It dials only while open (`FullSync.cs:27-31`).
  - Add `NSBonjourServices = [_beememorybank._tcp]` and browse with `NWBrowser`. The existing `NSLocalNetworkUsageDescription`
    shows the Local Network permission prompt the first time; a refusal means no LAN channels, internet and relay only. The phone's
    status says so.
- **Blind nodes.**
  - They cannot author events, so they never announce. Their address stays the pairing address (`BlindNodeManager`), changed by
    re-pairing as today.
  - An updated blind node relays `node_endpoints` like any Control event.
  - Blind copies (phone and desktop blind apps) keep calling their hub; a desktop hub's LAN channel as a hub is a later option.
- **Docker / server nodes.**
  - No LAN listener, no category check; the published ports decide.
  - A server MAY announce its public URL (`BMB_PUBLIC_URL`) in `node_endpoints` with `scope=internet` (phase 3).
  - A Docker node on a home LAN (a home server with 5300 published) could announce a LAN endpoint by configuration;
    not in this plan.

## 17. Phasing, effort, tests

Estimates are focused developer days, including tests and docs.

**Release 1 (next: 2.7.6 or 2.8.0). Fixes the case of section 1, no protocol change, no migration.**

| Item | Effort |
|---|---|
| R1.1 Stable leaf key in `LocalCaService` (section 9.1) | 0.5 d |
| R1.2 One `LanListener` with sync / join / web surfaces on 5311, bound to trusted interfaces; Windows category via `INetworkListManager`; macOS per-network trust list with the pairing network auto-trusted (D1); rebind on network change; pre-auth limits (section 14) | 5 d |
| R1.3 Firewall rule scope `private,domain` + `localsubnet` (app button, MSI; the legacy path keeps its scope, section 19) | 1 d |
| R1.4 mDNS: announce the sync surface whenever it listens (TXT `surface`, no name unless web, D5 v1); in the sync loop, when a peer's LAN address fails, resolve the peer by `nodeId` from a cached `MdnsBrowser` scan and try that address (no table yet; in memory) | 2 d |
| R1.5 Short connect timeout for private-range addresses; skip LAN addresses on subnets this node is not on; `away` / `old_version` states without "not answering" | 1.5 d |
| R1.6 Push log "withheld" (section 18) | 0.5 d |
| R1.7 Join wording, Network settings card for the new switch, docs (`internet-access.md`, `sync.md`) | 1.5 d |

**Total about 12 d.** With R1 on the desktop and the Mac:

- the Mac's stored `https://192.168.1.220:5311` works again;
- the desktop still does not dial the Mac (no address), but the Mac dials the desktop every minute, and a session is bidirectional;
- DHCP changes heal through mDNS.

**Release 2: channels and announcements.**

| Item | Effort |
|---|---|
| R2.1 Migration 044, `tbl_peer_channel` + `tbl_peer_endpoint`, repository, writers inventory | 1.5 d |
| R2.2 Channel ordering, last-working memory, parallel LAN attempts, per-request pins in `SyncClient`, backoff per peer over all channels | 3 d |
| R2.3 `node_endpoints` event: payload, applier (author == subject, caps, policy), capability `peer-endpoints-v1`, pull and push filters, announcement triggers, golden clear-field file, snapshot table | 4 d |
| R2.4 Join exchanges endpoints and CA anchor both ways (optional members) | 1 d |
| R2.5 `ca-pin` trust mode, chain building to the pinned CA, local upgrade of rows | 2 d |
| R2.6 Manual addresses: API, device details UI, phone "Edit URL" becomes "Add an address" | 2 d |
| R2.7 Reachability states incl. `via_relay` from `my-standing` and relayed events; the full-peer `silent` alarm | 2.5 d |

**Total about 16 d.**

**Release 3: UI merge and mobile.**

| Item | Effort |
|---|---|
| R3.1 Admin "Devices" section, device details, Network settings, redirects, header widget states | 6 d |
| R3.2 Android NSD + iOS Bonjour (`NWBrowser`, permission handling) | 4 d |
| R3.3 Optional: blinded mDNS ids (D5), VPN interfaces (D7), server public URL announcement, IPv6 | 3-5 d |

**Separate:** R1.6 can ship on its own at any time.

### Test strategy

- **`BeeMemoryBank.Node.Tests`**
  - Surfaces: each route of each surface, a sync route without token, the join route outside a session, the web UI never on the
    sync surface. Extend `LanJoinListenerTests` / `LanFrontListenerTests`.
  - Binding to trusted interfaces through an injectable `ITrustedNetworks` (fake categories).
  - Rebind on change; rate limits.
- **`BeeMemoryBank.Infrastructure` tests**
  - The leaf keeps its key across a SAN change and a renewal; a new key only when the key is gone.
  - mDNS TXT content per surface.
- **`BeeMemoryBank.Sync.Tests`**
  - Channel order, last-working memory, parallel LAN attempts with a fake connector.
  - A wrong peer at an address (pin or key mismatch) never accepted.
  - Per-request pin: peer A's address with peer B's pin refused.
  - `node_endpoints` applier rules.
  - Capability filters both ways: a peer without the capability never receives the event on pull or push, and the cursor moves.
  - Reachability state table.
- **`BeeMemoryBank.Integration.Tests`**
  - Two in-process nodes with real Kestrel `LanListener`s on `127.0.0.1` with different ports (the port is configurable for
    tests).
  - Pairing by code, then direct sync with the door closed.
  - An address change of the host is healed by `node_endpoints` (R2) or by a fake mDNS source (R1).
  - Mixed version: a node with the capability switched off behaves as 2.7.5 (no `node_endpoints` reaches it, nothing stalls).
  - `ClearFieldGoldenTests` and `PeerAddressWritersInventoryTests` updated on purpose.
- **Real-network checks**
  - **Desktop ↔ Mac** on a home LAN (both builds). Switch the Windows network to Public: the listener closes. Wi-Fi off and on, a
    DHCP renewal (`ipconfig /renew`).
  - **Two profiles on one Mac** with `BMB_LAN_PORT` set differently (the desktop app supports profiles).
  - **A 2.7.5 build** (a git worktree at the `v2.7.5` tag) as the old peer.
  - A Linux container run for the Kestrel binding code (`AGENTS.md`).
- Old-version safety is tested above all on the **stall** case: a locked encrypted 2.7.5-like node (capability off, the Step B
  relay inbox) must not get `node_endpoints` from any path.

## 18. Separate small item: "withheld" is not "dropped" in the push log

`SyncClient` adds the clear-content events it withholds from an activated blind node (spec 5.7) to `totalDropped`
(`SyncClient.cs`, `totalDropped += withheld`). So the log says "Push: applied 9, dropped 991" although the blind node dropped
nothing and the 991 events were never sent (seen on a Mac that joined through a desktop, after the catch-up from the server's checkpoint; the
content reached the blind node in its baseline).

Fix:

- a separate `totalWithheld` counter, still counted for "the cursor moved, save the push position";
- the line becomes `Push: applied 9, dropped 0, withheld 991 (clear content for a blind node, carried by the baseline), skipped 0`;
- optionally, the pull line counts events already in the log as "already held" instead of "applied".

A unit test asserts the counters. No behaviour change.

## 19. Implementation notes (where the code differs in detail from the plan)

- **Code map.** Listener: `desktop/BeeMemoryBank.Node/Front/LanListener.cs` (surfaces in `LanSurfaces.cs`, the door in
  `LanJoinListener.cs`, settings in `LanNetworkSwitch.cs`, `/node/lan` in `LanControl.cs`). Networks:
  `libs/BeeMemoryBank.Infrastructure/Network/` (`LanInterfaces.cs`, `TrustedNetworks.cs`, `WindowsNetworkCategories.cs`,
  `NodeNetworkSettings.cs`, `LanListenerState.cs`). Channels: `libs/BeeMemoryBank.Sync/Channels/` (`ChannelPlanner`,
  `PeerDialer`, `PeerChannelStore`, `PeerReachability`, `LocalNetworks`), the anchor in `Sync/Blind/PeerTlsAnchor.cs` and
  `SpkiPinRegistry`. The event: `EventApplier.NodeEndpoints.cs`, `EventPayloads.cs`. Api: `Services/Lan/`
  (`NodeEndpointsAnnouncer`, `LanJoinExchange`, `LanStatusFacts`), `Endpoints/PeerChannelEndpoints.cs`. mDNS browse:
  `Infrastructure/Mdns/MdnsPeerBrowser.cs`. Migration `044_peer_channels.sql`.
- **One Kestrel server per bound address** (the web surface: one on `0.0.0.0`), not one server rebound as a whole: a network
  that comes or goes adds or removes its own server, and the connections on the others (a snapshot streaming to a joiner)
  are not cut. The sync surface is decided per connection by its LOCAL address: a request to an address of an untrusted
  network (bound for the join door, or by the web surface's `0.0.0.0`) never reaches a sync route through the sync surface.
- **Limits before authentication**: the handshake (identity, sentinel, challenge, authenticate) is limited to 60 requests per
  minute per address (the plan's 10/min would starve push-on-save, whose cycles may start every 3 s); new connections to 60
  per minute per address, before TLS; 32 connections at once, no upgraded connections, 10 s header timeout. Bodies: 16 KB
  for the handshake and any request without a bearer token, 2 MB for the other token routes, 40 MB for a push or a blob
  shipment with a token (the Api checks the token before it reads the body).
- **The CA anchor is the CA's public key, not its pin**: the member is `tls_ca_key` (base64url DER SubjectPublicKeyInfo), and
  a leaf is checked by its own signature under that key (`Crypto/LeafSignature.cs`), with its validity period. A TLS server
  leaves its root out of the handshake, and a chain built with a custom root would need the CA certificate, whose subject
  carries the computer's name, in clear. A leaf pin announced by the peer is accepted as well (`tls_leaf_spki`).
- **Validation of an announcement**: an endpoint of a scope this build does not know is left out (a newer node may announce
  more), one that breaks its scope's rule refuses the whole payload. `internet` is reserved: it must be an address the peer
  policy allows, and is then left out, not kept (no release announces one; an internet endpoint would let the announcer send
  every node's probes to a host of its choosing).
- **An address nobody gave by hand is held to the peer's own keys** (review): an announced or mDNS address is a candidate only
  when the peer has a pinned leaf or CA key (its row's pin, or what it announced or gave at the join), and only a LAN or VPN
  address (never a host name on the internet; the mDNS browse notes IP addresses only, of paired nodes only). The public-CA
  fallback of an unpinned row applies at the host of the row's own address alone (`PeerTlsAnchor.PublicCaHost`,
  `AtHost`): at any other address an ordinary certificate does not pass, so a forged mDNS record naming a host with a public
  certificate is never taken for the peer. A session whose client cannot carry the anchor dials only the addresses a person
  gave (`PeerSessions.Usable`). A typed address of an unpinned public-CA server is therefore held to the keys of its signed
  announcement, if any. **Decided by the owner (2026-11-08):** kept as it is.
- **The one-hour pause after a wrong answer** applies to announced and mDNS addresses only; a primary or typed address that
  answered wrongly (a captive portal, a certificate being replaced) is still dialled, last, so a peer with one address is
  never left undialled for an hour. "Check now" ignores the pause.
- **A relay of a locked node** (`SealedRelay`) withholds `node_endpoints` from a puller without `peer-endpoints-v1` and fills
  the page past the withheld rows, as the open log's pull does.
- **The join door** opens on every LAN and VPN address, trusted or not (D2), never on a virtual adapter's; with no such
  address `EnableAsync` fails instead of handing out a code. Its handshake routes share the sync surface's per-address limit.
  A "virtual" adapter that holds the default gateway (a Hyper-V external switch, `vEthernet (External)`; a Linux bridge with
  the host's address) is the computer's LAN, not a virtual one.
- **The legacy `BMB_HTTPS_ENABLED` firewall rule keeps its scope** (no profile, no remote bound), unlike the plan's R1.3:
  that front serves everything because its owner asked for it, and may be the internet door of a port-forwarded node. The
  app's rule (`Front/LanFirewall.cs`) and the MSI's are the bounded ones (`private,domain`, `localsubnet`). **Decided by the
  owner (2026-11-08):** the legacy rule keeps its old wide scope; R1.3 is amended accordingly.
- **What a join learned** (the joiner's endpoints in `POST /api/join`, the host's in its answer) is a row of
  `tbl_peer_endpoint` with Lamport time 0: superseded by the first announcement, never replacing one.
- **The primary address off this network**: an announced or mDNS address of a network this computer is not on is not
  dialled; the primary address and a typed one (given by a person) still are, last, with the short timeout, so a routed
  private network (a site VPN) keeps working. With the backoff of an unreachable peer that costs about two seconds now and
  then while travelling.
- **One address, no probe**: a peer with a single dialable address is not asked its identity first; the session is the
  attempt. With several, the dialler asks `/api/sync/identity` (2 s per LAN address, staggered by 250 ms, 20 s per internet
  address) and the session then runs on the winner.
- **The mDNS scan is a neutral library**, `libs/BeeMemoryBank.Mdns` (`MdnsBrowser`, `MdnsConstants`, `MdnsNodeRecord`; it
  references nothing of the product): Infrastructure (the announcer, the peer browse, the join wizard) and the desktop blind
  apps (their hub after a DHCP change) both reference it. No file of Infrastructure may reach a blind app
  (`InfrastructureBoundaryTests`), so the blind apps neither reference nor link it.
- **The mDNS browse runs in the Api child** of the desktop node, like the announcer (the node identity, invisible mode and
  the database are there); sightings are kept in memory (10 minutes), the channel rows only record how an address went.
  The Windows Firewall therefore asks about `BeeMemoryBank.Api` (multicast) as well as `bmbd` (the listener).
- **Reachability** is a new field of the status (`Reachability`, `ReachabilityText`); the old `State` keeps its values and
  gains `away` for a peer whose failing calls are explained (away, reachable through a relay, an older version), so no
  page says "not answering" for it. Relay evidence is another peer's `my-standing` (asked every 5 minutes per peer) and
  the newest event the peer authored. `old_version` is judged on the capability the peer last showed (no
  `peer-endpoints-v1`), not on a version number. The full-peer alarm is kind `peer_silent` in `/api/blind-nodes/alarms`.
- **Every probe of an address for a peer carries that peer's anchor**: the dialler, "Add an address", and also "Change URL"
  (`PeerAddressService.ChangeAsync`), so a pinned desktop at a new LAN address is recognised by its (now stable) key.
- **`PeerSessions`** (`Sync/Channels/PeerSessions.cs`) is the one helper for a session over channels; the desktop loop uses its parts,
  the Android and iPhone full apps call `PeerSessions.SyncWithPeerAsync`, and the blind copies dial their hub over the hub's
  channels with its parts (`PeerSessions.PlanAsync`, `ClientFor`, `Usable`).
- **`tbl_peer_channel` is emptied in snapshots** like every table not listed (not dropped); a re-key clears both tables.
- **Not in this release** (release 3, optional): blinded mDNS ids (D5 phase 3), a server announcing its public URL,
  IPv6 endpoints. The platform ports of release 3 (section 16) are in this release.

