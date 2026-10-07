# ADR 0007: A full node can be called by blind copies (the "hub"), with two trust modes

## Status
Accepted (release 2.3.0, card BMB-159)

## Context
Blind copies (the Windows, macOS and Android blind apps; shared code in `libs/BeeMemoryBank.Blind.AppCore`
and `libs/BeeMemoryBank.Blind.PhoneClient`) never listen: they call a node. Until now the only node a
blind copy could be told to call was a Docker blind node, because the network records a way to reach a node
only for that case:

- `BlindListener.IsCallable(address, tls_spki, key)` accepted a row only with an `https` origin **and** a
  32-byte SPKI pin, and `tls_spki` was written by exactly one path, `BlindNodeManager.AddAsync` (a
  `BMBBLIND1.` pair code). The pairing computer therefore listed only Docker blind nodes.
- A full node (the main server behind a reverse proxy with a Let's Encrypt certificate) already serves
  everything a blind copy needs: `GET /api/blind/replica` (the signed blind package, only to a peer whose
  NodeId carries the blind mark) and protocol-3 sync under `/api/sync/*`. What was missing was a way to say
  "blind copies may call this node", and a trust model for a certificate nobody can pin by hand.

## Decision

### Two trust modes for a callable node, stored on its whitelist row and replicated
| mode | meaning | stored |
|---|---|---|
| `pin` | the node's TLS key is pinned (SPKI SHA-256, base64url, 32 bytes). Used by a Docker blind node (self-signed) and by a full node with its own local-CA certificate | `tls_trust = 'pin'`, `tls_spki = <pin>` |
| `public-ca` | the address is an `https` origin whose certificate must validate through the operating system's normal chain (a real certificate: Let's Encrypt behind a reverse proxy). No pin | `tls_trust = 'public-ca'`, `tls_spki = NULL` |
| none | not callable | `tls_trust = NULL`, `tls_spki = NULL` |

A row with `tls_trust IS NULL` and a pin is read as `pin` (rows written by older builds, and snapshots from
them). A row that says `public-ca` **and** carries a pin is contradictory and is not callable (fail closed);
so is any trust value this build does not know.

`BlindListener.IsCallable(address, tlsSpki, publicKey, trust)` is: https origin AND the key (if given) is 32
bytes AND (`pin`: valid pin, `public-ca`: no pin). One check serves the pairing list, pairing, the sealed
pairing record and the Admin screen.

### Wire: sync protocol 3 is unchanged
`whitelist_add` and `whitelist_update` payloads get one optional field, `tls_trust` (`"pin"`, `"public-ca"`,
and on update only `"none"`). System.Text.Json ignores members it does not know, the event signature covers
the payload text as sent, and `SyncProtocolVersion` is not touched, so an older node keeps applying the
event (tested with the previous payload shape).

Update semantics follow the neighbouring fields: `tls_trust` absent = trust unchanged. When the new mode
removes the pin (`public-ca`, `none`) the event also carries `tls_spki: ""`. An older node, which does not
know `tls_trust`, applies `tls_spki` as "set" and stores the empty string, which `SpkiPinRegistry` skips —
so after "pinned → normal certificate" an older node stops pinning the stale key instead of failing every
sync after the certificate is renewed. A node that does not know the mode treats the row as not callable
(it never evaluates callability: only the pairing computer does).

The mode is the authority over the pin, everywhere a row is written or read. An event with a `tls_trust` this build does not know
stores that value as written (so it travels on to builds that know it) and **no pin**: the row is non-callable and is never read as
`pin`; the pin fallback is only for an event that carries no `tls_trust` at all (an older sender). `SpkiPinRegistry` loads a pin only
for a row whose effective mode is `pin` (`BlindTrust.PinOf`), and the paths that write rows from a package manifest, a seed, a restore
record or the phone's package keep a pin only in pin mode — a pin left beside `public-ca` can neither be required of a renewed
certificate nor accepted in its place.

Package manifest (`blind-manifest.json`, format `bmb-blind-package-v1`) peers get the same optional
`tls_trust` member, so a blind copy restored from a package, a Docker blind node seeded from a hub and a
restore keep the mode. Older readers ignore it.

### Where it is set: Admin > Trusted Nodes, "Let blind copies call this node"
`PUT /api/whitelist/{nodeId}/hub` (superadmin, non-agent, master password re-authentication, vault unlocked),
next to "Change URL". Body: `address` (https origin), `trust` (`pin` | `public-ca` | `off`), `password`,
optional `expectedPin`. The server verifies before it saves, like "Change URL":

1. the address is a path-less `https://host[:port]`;
2. a probe GET `/api/sync/identity`, with the redirect and HTTP fallback refused, must return the row's
   NodeId **and** the row's Ed25519 key (stronger than "Change URL": the call code carries that key);
3. `public-ca`: the TLS handshake must validate against the operating-system chain; otherwise a clear error
   says what was wrong (expired, name mismatch, unknown authority/self-signed) and what to do;
4. `pin`: the probe records the SPKI the node presents (trust on first use), the dialog says so; if the admin
   supplied `expectedPin` it must equal the presented key. If the presented certificate would also validate
   through the system chain, the answer says so — pinning a public-CA certificate breaks at the next renewal
   whenever the certificate key changes (certbot's default), and "Normal certificate" is the right choice;
5. then `whitelist_update` is published (version first, as every whitelist write), the row is updated and
   `SpkiPinRegistry` invalidated. An audit-log entry records who enabled/disabled it.

`off` publishes `tls_trust: "none"`. Blind nodes (NodeId with the blind mark) are refused here: their pin
comes from their pair code. There is no CLI command: the CLI has no neighbouring whitelist/address command.

The row belongs to the node being called, so it is set from another full node's Admin (one that lists the server in its
trusted nodes — the owner's PC); a node has no whitelist row for itself and cannot mark itself. Pairing happens on the
same kind of node ("Blind nodes → Add an Android blind copy"), which is why the pairing list reads whitelist rows.

The Blind nodes page lists the callable full nodes ("hubs") with their mode; the pairing page lists every
callable node with its mode in the drop-down.

### Blind apps: trust from the call code
The call code (`bmb-blind-call:?...`) is versioned by its content:

- `pin` — unchanged text and MAC (`bmb-blind-call-v1`): `a`, `n`, `s` (pin), `k`, `m`. Old codes keep working
  in new apps; new pin codes keep working in old apps.
- `public-ca` — `a`, `n`, `t=public-ca`, `k`, `m`, **no** `s`, MAC label `bmb-blind-call-v2` over
  address, NodeId, trust and key. Old apps reject it (no pin) — fail closed — and say it is not a connection
  code.
- A code with an unknown `t`, or `t=public-ca` together with `s`, is refused.

`BlindHttpClientProvider` builds the handler from the code: `pin` → the SPKI-pinned handler as today;
`public-ca` → standard validation (`SslPolicyErrors.None`, so the host name must match), `https` only,
no redirects, no `http` fallback, and every request must go to the code's own origin (the certificate check
alone would accept any CA-valid site). The platform projects (`BlindMobile`, `BlindDesktop*`) build no
handlers of their own, so one change serves Windows, macOS and Android.

The sealed pairing record `android-backup:<id>` carries the producer's address and pin; it gets version 3
for `public-ca` (trust in the record and in the statement the pairing superadmin signs). A version-2 record
is unchanged, so `pin` pairings are byte-for-byte what they were; an older node cannot read a version-3
record and fails closed.

### Tests use a test CA through the production abstraction
`public-ca` validation is `PublicCaTls.IsValid(certificate, chain, errors, anchors)`. In production the
anchors are empty and the result is `errors == None`. A test supplies its own root as an *additional trust
anchor* (the same shape as a user-installed CA), which is validated as a real chain with the real host-name
check (a missing certificate or a name mismatch is never excused). Nothing in production code accepts an invalid
certificate. The same check serves three callers: the blind apps' HTTP provider, the Admin probe
(`HubTrustProbe`) and `SpkiPinRegistry` — the handler every full node's sync client uses — for a peer with no pin,
so a full node dials a `public-ca` hub exactly as it dials any ordinary peer. The tests drive all three over real TLS
on the loopback interface, against a hub on real Kestrel.

## Security review

- **CA misissuance.** In `public-ca` mode anyone a public CA will issue a certificate for the host to can
  impersonate the hub's TLS endpoint. What that gets them is limited — see below — but it is the reason the
  `pin` mode exists, and the UI says so next to the choice.
- **What a stolen blind-copy token can fetch.** A blind copy authenticates by Ed25519 challenge-response and
  gets a one-hour bearer token. With it a caller can pull the sync event log and the signed blind package:
  **ciphertext plus the plaintext metadata by design** (ADR 0005); never the master key or a plaintext
  article body. The package signature is checked by the copy before install, so a man in the middle cannot
  hand a copy forged data; they can only withhold.
- **Bandwidth abuse.** The replica route needs a valid blind peer's token (the whitelist row is re-checked on
  every request), the package is built once per 30 minutes and cached, and this release adds a per-peer
  admission limit on `/api/blind/replica` (20 requests per 10 minutes, checked after the token and the blind mark,
  429 with `Retry-After`) so one token cannot pull the package in a loop. A first load with several resumes of a
  flaky connection stays far below it.
- **Revocation.** Revoke the lost blind copy in Admin (Trusted Nodes, or Blind nodes): the revoke event
  spreads, `ValidateActivePeerAsync` re-checks the whitelist on every request so its token stops working at
  once, and it can no longer authenticate. Turning the hub off again stops new pairings and makes the row
  not callable; copies already paired keep calling until the address stops answering them.
- **Reverse proxy.** `/api/blind/replica` and `/api/sync/*` must be forwarded to the hub's API. They are
  already on `PublicSurface` (peer routes, authenticated inside the handler) and need no new public entry;
  the documented proxy recipes get a "blind copies can call this node" block.
- **The probe is not a way to scan this server's network.** The address in "Let blind copies call this node" is connected to by
  the server, so loopback, link-local (169.254.0.0/16 and fe80::/10, the cloud metadata address included), unspecified and
  multicast addresses — also as IPv4-mapped, IPv4-compatible, NAT64 or 6to4 forms — are refused, since a blind copy cannot call a hub
  through them either. The private LAN ranges (10/8, 172.16/12, 192.168/16, fc00::/7) stay allowed: a pinned hub on a company or
  home network is a supported setup. The check is made where the socket is opened, on every address the name resolves to and again
  on the address actually connected to (a name cannot pass on one answer and connect to another); redirects stay off, no system proxy
  is used, the identity answer is size-limited, and the error text repeats nothing the target sent. The probe relay used by the
  Internet Access screens keeps its own, stricter rule (open internet only).
- **Fail closed.** Contradictory or unknown trust, a non-https address, a pin of the wrong length, a
  mismatching NodeId/key, and a certificate the system does not trust all refuse; none falls back to http or
  to an unpinned call.

## Consequences
- Migration `036_whitelist_tls_trust.sql` adds the column and backfills `pin` where a pin exists
  (`docs/migrations.golden.txt` updated; so are `docs/full-node/{ROUTES,DI}.golden.txt` for the new route and the two
  singletons, and `docs/vault-split/shared-types.txt` for the three new shared types).
- Mixed-version networks: an older node ignores the mode, keeps pinning only while a pin exists, and never
  lists the node for pairing. Upgrade the pairing computer first.
- A hub whose certificate changes: `pin` mode needs the pin set again (Admin, same dialog); `public-ca` does
  not.
