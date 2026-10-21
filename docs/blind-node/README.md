# Blind node: what it is made of

A blind node stores and relays the mesh's ciphertext and cannot read any of it: no master DEK, no master password,
no sessions, no users, no authoring of events. Until 1.0.16 it was the full `BeeMemoryBank.Api` started with
`BMB_ROLE=blind`, so the container carried all of the application and the role switches were the only thing between a
request and the code that reads. Since then it is a program of its own. Since the vault split (2.0.1) the libraries
it shares with the Android blind app are the **shared** ones only: the code that holds the master key, opens a session or reads
content is a different assembly, `BeeMemoryBank.Vault`, which no blind program references (see [ADR-0002](ADR-0002-vault-split.md)).

**Since 2.6.0 and the activation of metadata encryption** ([ADR 0009](../adr/0009-metadata-confidentiality.md)), the
mesh's blind copies hold no structure at all. The article, folder, tag and media tables are emptied at the first
adoption after the switch and stay empty; what replaces them is the original signed protocol-4 events as received
("deltas") plus the newest **baseline** — a filtered copy of the replicated tables, encrypted as a whole in a chunked
authenticated container and signed by the superadmin full node that produced it. A blind node stores and trims both
but never opens either: it cannot. Its console shows baseline age, the number and size of pending deltas and stored
bytes — never a note count — and raises an alarm when the baseline is older than a week. The remains of the former
plaintext database stay marked aside (`legacy_copies`, `metadata_in_backups`) for the owner to retire. What a relay
or a stolen blind disk can still learn (sizes, counts, timing, node ids, peer names and the trust graph) is listed in
ADR 0009; the sync side of the switch is described in
[../sync.md](../sync.md#switching-metadata-encryption-on).

| Piece | Project | What it is |
|-------|---------|-----------|
| host | `server/BeeMemoryBank.BlindNode` | own `Program`, container registrations, pipeline, startup tasks; **links** the 88 Api source files a blind node needs (`linked-api-files.props`) |
| shared libraries | `libs/BeeMemoryBank.{Core,Crypto,Search,Storage,Sync}` | what every node needs: store, relay and apply ciphertext, the sync protocol, the external-key identity, pairing and TLS pinning, backups, the recovery set a node receives and serves; `NodeDependencyInjection` in each (`AddNodeStorage` / `AddNodeCore` / `AddNodeSync`); every database migration is embedded in Storage |
| phone client | `libs/BeeMemoryBank.Blind.PhoneClient` | the 11 library files only the Android blind app (and the full node's restore of a phone backup) need (replica and pull clients, backup seal ...); on the shared libraries; the Linux host does not contain it |
| blind app core | `libs/BeeMemoryBank.Blind.AppCore` | platform-neutral blind-app state (Android, iPhone, Windows and macOS), pairing, receive-only composition and backup orchestration; several accounts (one per source mesh) with per-account limits, pauses and a one-loop scheduler, see [ACCOUNTS.md](ACCOUNTS.md); it depends only on Core, Crypto, Storage, Sync and PhoneClient, never Vault or an OS UI API |
| vault (full node only) | `libs/BeeMemoryBank.Vault` | sessions, master-key handling, content crypto, recovery creation and opening, the search index, article/user/role/tree/import services, full-node repositories, `AddCore`/`AddStorage`/`AddSync`; **no blind program references it** |
| CLI | `server/BeeMemoryBank.BlindCli` | `bmb`, only `bmb blind ...`; links the full CLI's four blind command files; no library reference |
| console | `server/BeeMemoryBank.BlindConsole` | unchanged: one embedded page and a proxy to the local node |
| Android app | `mobile/BeeMemoryBank.BlindMobile` | thin MAUI/Android adapters over `Blind.AppCore` (a Keystore alias and a SharedPreferences file per account, committed writes, one WorkManager job set for the device that walks the accounts through the device schedulers, the phone's metered-network and battery-saver answers), pages for the accounts, adding one (paste or a `bmb-invite:` link) and one account with its limits, plus the shared libraries + `Blind.PhoneClient`; never the Vault. See [ACCOUNTS.md](ACCOUNTS.md#the-android-head) |
| Windows and macOS apps | `desktop/BeeMemoryBank.BlindDesktop` (the Avalonia host, both systems), `desktop/BeeMemoryBank.BlindDesktop.Windows`, `desktop/BeeMemoryBank.BlindDesktop.MacOS` | a quiet tray / menu-bar app and one window over `Blind.AppCore`, with **several accounts**: an account list on the left, today's panel of the selected account plus its Limits on the right, "Add account" with a paste box and the preview of the invitation, the device's own limits, and a tooltip that sums the accounts up ("3 copies - 1 paused"). Each head implements the AppCore seams per account: Windows - DPAPI files under `accounts/<id>/secrets` (the 2.6 account keeps `secrets/*.dpapi` and its entropy), atomic JSON state, the connection cost and Energy Saver of the WinRT APIs, HKCU Run autostart; macOS - Keychain items `<id>/identity-seed-v1` ... in the service `com.beememorybank.blind` (the 2.6 account keeps its names), a state file per account folder, Network.framework `nw_path_monitor` (expensive or constrained) and Low Power Mode, a LaunchAgent. One device loop (`BlindDeviceScheduler`, 15-minute sync, hourly long job, one account at a time); the head is a client of a listening node like the Android app: it starts no web server, no child process and listens on no port; no Vault, Hosting, full Desktop or Node reference. See [ACCOUNTS.md](ACCOUNTS.md), "The desktop heads" |
| iPhone app | `mobile/BeeMemoryBank.BlindIos` | the same blind copy on iOS (MAUI, `net10.0-ios`, built on a Mac): thin iOS adapters over `Blind.AppCore` (Keychain items per account, readable after first unlock on this device only, the data folder and every account folder with file protection and no device backup, `NWPathMonitor` and Low Power Mode for the account switches, BGTaskScheduler rounds that serve the accounts in turn, a local "no contact" notification per account, Accounts / Add / Account pages), plus the shared libraries + `Blind.PhoneClient`; never the Vault. Source-only for now; see [IOS.md](IOS.md) and [IOS-DISTRIBUTION.md](IOS-DISTRIBUTION.md). |
| image | `docker/blind` | the three programs published per `TARGETARCH`, plus restic; released as `ghcr.io/ultrathinker/beememorybank-blind` (amd64 + arm64, `.github/workflows/docker-publish.yml`), run by `docker/blind/compose.image.yaml` |
| guards | `tests/BeeMemoryBank.BlindNode.Tests`, `tests/BeeMemoryBank.BlindMobile.Tests`, `tests/BeeMemoryBank.BlindDesktop.Tests`, `tests/BeeMemoryBank.BlindIos.Tests` | the blind-only integration tests of `Integration.Tests`, linked and run against this host; route matrix; composition, linked-set and migration guards; the Android, Windows and iPhone boundary guards (the Windows one scans the published folder, the iPhone one the built `.app` and the golden list `IOS-APP.golden.txt`) |
| tools | `tools/blind-link`, `tools/blind-e2e`, `tools/ios-e2e` | recomputes the linked Api set by compiling; Docker end-to-end, upgrade/rollback rehearsal on a copy of a live node's data, real-phone check; the desktop app's end-to-end check, with two accounts, is the opt-in `DesktopEndToEndTests` (`BMB_E2E_DESKTOP=1`) |

Documents: [STARTUP-CONTRACT.md](STARTUP-CONTRACT.md), [ROUTES.golden.txt](ROUTES.golden.txt), [COMPOSITION.md](COMPOSITION.md),
[PARITY.md](PARITY.md), [ACCOUNTS.md](ACCOUNTS.md), [FAILURE-TEXT.md](FAILURE-TEXT.md), [ADR-0001-link-first.md](ADR-0001-link-first.md), [ADR-0002-vault-split.md](ADR-0002-vault-split.md).

## Invitations (2.7): made on the full node; a server node joins with `bmb blind join`

A friend's blind copy (an app) can be added by a one-line **invitation** the owner makes on the full node the copy will call
([../sync.md](../sync.md#invitations-to-a-blind-copy-27)). The whole of it lives on the full node: `BlindInviteService` and
`BlindInviteEndpoints` are on `tools/blind-link/exclude.txt`, the repository and its types are in `BeeMemoryBank.Vault`, the three routes
(`POST /api/sync/invite/redeem`, `POST /api/sync/invite/status`, and the management routes under `/api/blind-nodes/invites`) are in the full-node
`ROUTES.golden.txt` only. **A server blind node (Docker, service) holds none of the invitation SERVICE code and answers none of the three public routes.** It holds the empty table
`tbl_blind_invite` (migrations 041 and 042), because the migrations are shared; the table is outside `SnapshotTables.Replicated`, so it never travels in a package or a baseline.
What a server node has instead is the *joining* side of one kind of invitation, below.

The device's side (an app with several accounts) is in `libs/BeeMemoryBank.Blind.AppCore`: [ACCOUNTS.md](ACCOUNTS.md#adding-an-account-from-an-invitation). A server blind node
is not an app with a screen: it joins through its command line (`bmb blind join`), and it stays single-mesh: one container serves one mesh, and several meshes on one machine are several
containers ([Several memory banks on one machine](#several-memory-banks-on-one-machine-docker)).

### A server blind node joins with `bmb blind join <invitation>` (2.7)

Until 2.7 a friend's server node was attached by the OLD pairing: the node shows a pair code (`bmb blind pair-code`), its owner pastes the code on the full node's Blind nodes page,
and the full node adds the node and seeds it. **That still works, unchanged.** The new way saves the copying of a code between two people: the owner of the full node makes an
invitation ("Invite a blind copy", one line, 15 to 60 minutes), the friend runs one command on their server, reads out a short match code, and the owner confirms it with the master password.

```bash
# the friend, on the machine of the blind node (the node must be running, and BMB_PUBLIC_ADDRESS set: the full node dials that address to send the data)
docker exec -it bmb-blind bmb blind join 'bmb-invite:?v=1&a=https%3A%2F%2F...'
# or, so that the invitation is not in the process list and the shell history:
docker exec -i  bmb-blind bmb blind join - --yes < invitation.txt
```

The command shows the invitation first (who made it, the address of the full node, the certificate rule, how long it works, and "Limits: none" - a server node has no limit of its own), asks
`Join this full node? [y/N]`, and then prints the **match code**. The friend reads that code to the owner; the owner compares it with the one on the Blind nodes page ("Invited (server)")
and confirms. The command waits (it asks every 5 seconds at first, backing off to 30) and ends with `confirmed` (exit 0), `refused` or `expired` (exit 1). `--no-wait` returns after the code;
`--status` shows where the join stands; `--cancel` gives up a join that waits. A command that is stopped (Ctrl+C) or a CLI that dies does not stop the join: it lives in the node.

How it works, and why the command holds no key:

- The CLI only talks to the **running node** over its local console API (`/api/blind/join/*`, the internal key, exactly as `bmb blind status`); it never reads a key file, and it prints
  neither the invitation nor any secret nor any key. The node (`BlindJoinService`, linked from the Api sources like the rest of the blind surface) reads and checks the text (shape, version,
  the full node's signature, expiry - with the words the apps use), asks the full node for its identity through the trust the invitation names (a pin, or the normal certificate chain; nothing
  is sent if the computer at the address is not the one named), and redeems with its OWN identity key. Instead of a backup key it sends **its pair code** - the same code `bmb blind pair-code`
  shows, whose secret stays valid for 73 hours here instead of 15 minutes (the owner has 72 hours to answer), signed under its own proof label (`bmb-invite-redeem-server-v1`).
- The owner's Confirm (master password, as for an app) then runs what pasting that code does - `BlindNodeManager.AddAsync`: the pre-flight, `whitelist_add` with the node's pin, the seed, the
  push position - inside the same state machine as an app: the row is `confirming` before the first write, a seed that fails leaves it `confirming` and the reconciler (at start and
  every minute) finishes it, and when it cannot (the owner's time is out, the pre-flight fails) it revokes what the confirmation added and ends the row. Never half done.
- **One way of being attached at a time.** A node that already holds a full node's seed refuses to join ("This node is already attached to a full node: remove it there first"). While a join waits for the
  owner, the node does not show or renew its pair code (409, "give the join up with `bmb blind join --cancel`"), and the full node refuses to add the same node from a pasted pair code while its
  invitation waits. After the join ends (refused, expired, cancelled) the old way is open again.
- **An older full node refuses cleanly.** A full node that does not know a server node's redemption refuses the whole request (400, nothing read, nothing written; a pre-2.7 one answers 404), and the
  command says: *upgrade the full node first*. The invitation text stays version 1: it carries no feature list, and the new redeem member is optional.
- The node keeps the join in `blind/join.json` of its data folder (not in the database, which the seed replaces): the invitation's id, the hub's address, the match code and where the answer stands -
  no secret, and the invitation text itself is never stored. It is written owner-only and flushed before it takes its name, like the node's other state files, and a temporary file a dying write left is
  removed at the next start. If the node is wiped, a record left behind does not keep the pair code away.
- **A lost answer does not lose the join.** The full node commits a redemption before it answers, so the answer can get lost (a dropped connection, a timeout, a proxy's 5xx) or the node can die right
  after. The node therefore writes its record (state `redeeming`) and holds the pairing secret it gave the full node BEFORE the request leaves, and forgets the secret only when the full node refuses
  the FIRST request for good (410 not valid or used, 409 known under another key, 429, 503 locked, 400/404/405 too old), when the full node says to the node's signed status question that it does
  not know the redemption, when the owner's time runs out, or on `--cancel`. A refused REPEAT of a join that has a record (the row is being confirmed: 410; the session is locked: 503; too many calls:
  429) says nothing about the first request and ends nothing - it keeps the join and points at `--status`. After an answer that did not arrive the command
  says so; then the same command with the same invitation continues (the full node answers a repeat by the same node with the same answer and takes the code the node now holds), `--status` asks the
  full node with the node's signed question (it finds the redemption, or learns that the request never arrived and ends the join), and the owner's confirmation seeds the node as ever.
- **A wipe stops a join.** `bmb blind wipe` (and the console's wipe) cancels the join that waits for the full node and waits for it to leave before anything is deleted, and refuses a new one until it is
  over, so a join that was waiting cannot write its record or its pairing state into the node the wipe has just emptied. The wipe also removes what a seed had staged in `blind-tmp`, and it takes the seed service first: a seed that is being received is stopped, one that is being applied is awaited (never cut in the middle of a switch), and none starts until the wipe is over - so a seed that was authorized before the wipe cannot adopt into the emptied node. A seed that does not finish within a minute makes the wipe refuse ("nothing was wiped; try again").
- The `/api/blind/join/*` routes are for the person at the node's console: internal key, superadmin, and **not an agent** (a superadmin's agent key is refused, as on the full node's invitation routes).

Because migrations 041 and 042 raise the schema number to 42, a 2.7 hub's seed and reseed are refused by a 2.6.x blind node ("upgrade this node first"): update the
blind node together with the hub.

**For the app that redeems an invitation (the device's client):**

- *A lost redeem answer is replayed only while the row is `redeemed`.* Once the owner has moved the invitation to `confirming` or `confirmed`, a repeated redeem gets the
  generic not-valid answer (410). The app must therefore fall back to its **signed status request** (`POST /api/sync/invite/status`, valid within ten minutes of the hub's
  clock), not to redeem retry, when it did not get its 202 and a redeem retry answers 410: the status answers `awaiting`, `confirmed`, `refused` or `expired` for the device
  that redeemed.
- *A locked hub.* While the full node is locked (after a restart, until the owner unlocks it) it authenticates nobody and serves nothing; redeem and status answer the generic
  locked outcome (503 `locked`) and the app keeps asking until the invitation runs out. While an invitation is `confirming` (the owner said yes and the hub is adding the
  device, or finishing it after an interruption) the status says `awaiting`; it says `expired` or `refused` only after the hub has taken the device back out, so an `expired`
  answer is never given for a device that is still a peer. A `confirming` row is decided at the first moment the hub is unlocked and usable.
- *Limits.* Requests that name the app's own open invitation are served from a small budget of their own, not from the shared one; an app that polls the status every few
  seconds for the time the owner takes to answer stays within it (`docs/sync.md`, "Limits of the public routes"). Honour `Retry-After` on 429 and 503.

## Several memory banks on one machine (Docker)

**One container serves one mesh in this release.** A server blind node has one data volume, one identity, one pair code, one console password and one backup repository. It has no app accounts, no device-wide limit and no invitation service of its own (it can *join* with an invitation, above); its owner can cap this one replica with two limits, described in [Limits of a server blind node](#limits-of-a-server-blind-node-27). It joins a full node with `bmb blind join` or with the old pair code, one of them at a time. To hold the encrypted copies of several memory banks on one machine, run **one container per mesh**: each with its own folder, its own
host ports, its own container name and its own volumes. Two containers must never share a data volume (the node locks its data folder; a second one on it refuses to start).

Take `docker/blind/compose.image.yaml` as it is for the first mesh (container `bmb-blind`, ports 5610 and 5611, volumes `bmb-blind-data` and `bmb-blind-backups`). For a second mesh make a
folder of its own with this `compose.yaml`; compared with the real file, only the project name, the container name, the host name, the host side of the two ports, the port in
`BMB_PUBLIC_ADDRESS` and the two volume names are different:

```yaml
# ~/bmb-blind/mesh-b/compose.yaml
name: bmb-blind-b                      # own compose project

services:
  blind:
    image: ghcr.io/ultrathinker/beememorybank-blind:${BMB_VERSION:-latest}
    container_name: bmb-blind-b        # own name (docker exec, docker logs)
    hostname: bmb-blind-b              # fixed, as in the original (restic locks)
    ports:
      # Sync port for the full nodes of mesh B: the host's LAN address, own host port, the container's 5610.
      - "${BMB_LAN_ADDR:?set BMB_LAN_ADDR to this host's LAN address, e.g. BMB_LAN_ADDR=192.168.1.20}:5620:5610"
      # Console of mesh B: host loopback only, own host port.
      - "127.0.0.1:5621:5611"
    volumes:
      - data:/app/data
      - backups:/backups
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - BMB_ROLE=blind
      - BMB_DATA_PATH=/app/data
      - BMB_BLIND_HTTPS_PORT=5610      # inside the container: unchanged
      - BMB_BLIND_LOCAL_PORT=5612      # inside the container: unchanged
      # What the pair code tells a PC to dial: the HOST port, 5620.
      - "BMB_PUBLIC_ADDRESS=https://${BMB_LAN_ADDR:?set BMB_LAN_ADDR to this host's LAN address, e.g. BMB_LAN_ADDR=192.168.1.20}:5620"
      - BMB_API_URL=http://127.0.0.1:5612
      # Optional node-owner caps in bytes. Empty or omitted means no cap; 0 pauses.
      - BMB_BLIND_MAX_STORED_BYTES=2147483648
      - BMB_BLIND_MAX_TRAFFIC_PER_DAY=209715200
    mem_limit: 1g
    cpus: 1.0
    restart: unless-stopped

volumes:
  data:
    name: bmb-blind-b-data             # own volumes
  backups:
    name: bmb-blind-b-backups
```

A third mesh follows the same way (5630 and 5631, `bmb-blind-c`). Then start each, set each up and pair each with its own mesh:

```bash
cd ~/bmb-blind/mesh-a && BMB_LAN_ADDR=192.168.1.20 docker compose up -d      # the unchanged compose.image.yaml
cd ~/bmb-blind/mesh-b && BMB_LAN_ADDR=192.168.1.20 docker compose up -d

docker exec -it bmb-blind   bmb blind init --repo-folder /backups/restic     # mesh A
docker exec -it bmb-blind-b bmb blind init --repo-folder /backups/restic     # mesh B
```

Each node has its own pair code, console (`ssh -L 5611:127.0.0.1:5611 <host>` for mesh A, `-L 5621:127.0.0.1:5621` for mesh B) and backup repository; the pair code of mesh B carries
`https://<host>:5620`, so open that port to the full nodes of mesh B and keep 5621 on the loopback. Each container adds to the host's memory need (for one node
[docker/blind/README.md](../../docker/blind/README.md) asks for at least 1 GB of RAM plus swap, or 2 GB); Docker's `mem_limit:` and `cpus:` keys on the service cap each one. Update all of
them with `docker compose pull && docker compose up -d` in each folder, and together with the full nodes of their meshes (see "Invitations" above: schema 42).

## Limits of a server blind node (2.7)

This section is the record of how the node's two limits hold; `SECURITY.md` ("Known limits") and `docker/blind/README.md` (the operator's view) state the same numbers and point here.

**The limits.** `BMB_BLIND_MAX_STORED_BYTES` caps what the node stores for the mesh and `BMB_BLIND_MAX_TRAFFIC_PER_DAY` caps what it moves in a day (the node's local day). The same values can be edited in the local console, and `bmb blind status` and the console show both with what the node holds and moved today.

- An empty or unset variable is no limit; `0` pauses that kind of transfer. A daily traffic limit below 10 MB (other than 0) is refused, and so is a value that is not a number; the node does **not start** with either, rather than serve without the cap its owner believes in.
- A variable that is set is applied again at every start, so it wins over a console edit; a variable left unset leaves a limit set on the console alone.
- They are an agreement with the person who hosts the node, **not an operating-system quota**: a process that can write the data volume can change the data or the counts.

**A reached limit is a pause.** The routes that carry the mesh's data (events, blobs, the seed, the replica and restore packages), reads and writes alike, answer `503` with `X-BMB-Paused: disk|traffic` and `Retry-After` (the next local midnight for traffic, an hour for storage), and nothing is written. Identity, sign-in, status and console still answer. A current full node shows "Paused by the blind node's owner: storage limit" (or "daily traffic limit"), asks again at the named time and raises no alarm; **an older full node** sees an ordinary `503`, backs off as for a failing node and may show the node as unreachable until the pause ends. A refusal that rests only on other requests' reservations (the day's own count would leave room) is not "reached until midnight": it is `503` with `X-BMB-Paused: busy` and a `Retry-After` of one minute, which a current full node shows as "Busy with other transfers right now; asked again in about a minute", never as the owner's pause.

**How a cap holds.**

- *The door only looks.* It runs before anybody is known (before the peer's token, the seeder's proof or the restore code is checked), so a request that has not authenticated reserves nothing and holds nothing of the budget, however large a body it announces or however slowly it sends it.
- *A reservation is taken after authentication.* The handler reserves the length the request announced, or **1 MiB** when it announced none (a chunked body), so that requests arriving together cannot all pass against the same remaining budget. The reservation follows what the request really moves, and is given back, with what really moved counted, however the request ends.
- *Small bodies are bounded first.* No handler reads a body before authentication, except on the three routes that take a small JSON body: a list of blob hashes (`blobs/check`, `blobs/get`: **256 KiB**; the largest legitimate list is about 134 KB) and a restored device's claim (`claim`: **16 KiB**). A body that announces more is refused `413` at the door with nothing read; one that does not announce its length is refused `413` the moment it passes the limit. The hash-list routes read theirs only after the peer's token is checked; a claim reads its body only when it presents a restore code. So a client that has not authenticated costs at most 16 KiB of counted traffic per claim attempt that presents a code.
- *A response that announces its length* (a replica or restore package, a range) is judged when it announces it, while the response has not started: with no room the answer is the pause instead of the download. One that does not announce its length is judged by its 1 MiB minimum and counted by what it moves.
- *Every request that holds anything has a lifetime.* Counted from when it was authenticated, it is half an hour plus the time its size needs at 64 KiB/s (a 600 MiB restore is given about three hours; a request that announced a single byte holds the 1 MiB minimum and has the half hour). A request that has not finished by then is **aborted**: its connection is ended and every read or write it still tries fails, with what moved counted. Its reservation stays until the request has really ended, so nothing goes on uncounted. A handler that is stuck in a call that cannot be cancelled and has not returned **two minutes** after the abort is settled without it: what it moved is counted as used, the rest of its reservation is given back, and what it moves afterwards is counted straight into the day. So a body that trickles, or a peer that opens requests and sends nothing, holds the room half an hour (plus the size term, and two minutes more for a stuck handler) at most; the caller asks again.
- *A request that was admitted finishes* (or, past its lifetime, is aborted): the cap itself never cuts a response off in the middle. A cap can therefore be passed by the bytes that the requests in flight *when it was reached* move beyond what was reserved for them (nothing for a body or response of announced length, the excess over the 1 MiB minimum for one whose length was not announced); a request that arrives after the cap is reached is refused.

**What the stored size counts.** The database, the media, the baselines **and what peers make the node stage and build** (a seed that arrives in parts, the package it is unpacked into, the files of a switch, replica packages), judged the same way; the body of a request that only looks something up (`blobs/check`, `blobs/get`, `claim`) is not stored.

- A seed is judged as a whole at its FIRST part, with exact numbers: the whole announced package must fit into the room (the cap less what the node holds now; the old data stays until the switch replaces it, so a reseed needs room for the old AND the new data). A part is checked again with what the node has grown by meanwhile. A seed that does not fit is answered with the same `503` / `X-BMB-Paused: disk` before a byte is staged. What a seed has staged or is applying is left out of the walk other callers' decisions rest on, so a seed's peak never pauses the phones. An interrupted upload is dropped with its staging an hour after its last part. The volume itself must have about three times the package free (the package, its unpacked copy and the new database).
- A current full node that is told to wait does not build and upload the package again before the time the node named; it keeps that in memory only, so a restart of the full node ends the wait (the cost is one package build and a first part the node refuses before it stages a byte).
- A replica package the node builds for a phone counts while it exists (half an hour at most), so a storage cap tighter than about twice the node's data can pause the node while such a package is cached: set it with room, or wait for the package to expire.

**Wrong restore codes.** A restore code is 80 random bits. Wrong codes are counted per source address, not against the codes: **five in five minutes** from one address, then `429` with `Retry-After`, before the address's next code is looked at; the same on the restore package, restore events and claim routes (a claim whose body is not valid or is over its limit counts too). A right code costs nothing. A stranger can therefore make restores wait a few minutes but cannot revoke or use up a code. The counts are in memory, so a restart forgets them; behind a proxy that hides the client's address all callers share one budget. The name of a restored computer is at most **200 characters** (the setup page, the restore client and the endpoint all hold to it), so the claim sent after the restore has committed cannot exceed the 16 KiB.

**What stays open.** The abort and the early `413` were proven with the test server, not on a real web-server connection. A request that hangs for good still costs a connection and a thread until it returns or the node restarts (what it writes after the grace is counted by the next size measurement). If the node's settings file is damaged or unreadable it is read as the defaults, so the limits count as none until the file is repaired (the node does not fail closed on local file damage). The numbers above (half an hour, 64 KiB/s, two minutes, five in five minutes, 256 KiB, 16 KiB, 200 characters) are constants in the code, not settings. A new bulk route that is in neither the endpoint table nor the door's table is not caught by the tests. The node's older protocol limits, such as 20 package requests per peer per ten minutes, still apply.

## The rule that keeps it honest

*What is not in a referenced assembly cannot be in the container.* Two mechanisms, one per layer:

* the host still LINKS the Api source files it needs (`linked-api-files.props`); `tools/blind-link/exclude.txt` lists the Api files a blind node
  must never contain (MCP, agent auth, caller scope, chat, update, DEK rotation, the new device's side of a restore, the full snapshot encrypt/restore,
  the PC-side management of blind nodes), and `BlindLinkedSetTests` holds the checked-in set to it;
* the libraries are referenced as assemblies, and the one that holds the vault is simply not referenced. `BlindBoundaryTests` (host) and
  `AppBoundaryTests` (Android app) read the metadata of every application assembly of the output and fail if one defines, references or calls
  a vault type or a member that hands out the master data key (`docs/vault-split/vault-types.txt` is the generated list of vault types;
  `VaultBoundaryListTests` keeps it exact and holds the scanner to controls). `BlindNodeCompositionTests` checks the closure, the services and
  the hosted services; `BlindRouteMatrixTests` pins the 53 routes it serves.

The honest limits are listed in [COMPOSITION.md](COMPOSITION.md#known-limits-stated-plainly).

## Recomputing the linked Api set

```
python tools/blind-link/blind_link.py          # host: linked-api-files.props (the library half of the link-first design is gone)
```

## Running the end-to-end checks

```
docker build -f docker/blind/Dockerfile -t bmb-blind:dev .
python tools/blind-e2e/e2e_docker.py --image bmb-blind:dev --full-exe <BeeMemoryBank.Api.exe> --work <scratch dir> [--first-image <older image>]
python tools/blind-e2e/fixture_rehearsal.py --data <copy of a node's /app/data> --old <older image> --new bmb-blind:dev --work <scratch dir>
python tools/blind-e2e/phone_e2e.py --image bmb-blind:dev --full-exe <BeeMemoryBank.Api.exe> --work <scratch dir> --serial <test phone> --apk <signed APK>
```

The released image runs through the same script: `--image ghcr.io/ultrathinker/beememorybank-blind:<version>`.
`scripts/smoke-docker.sh <image> blind <version>` is the quick start-up check the image workflow runs on each architecture;
it removes its container when the run ends, including an interruption, unless `BMB_SMOKE_KEEP=1` keeps it for `docker logs`.

`e2e_docker.py` starts the image and a real full node with its own data directory, pairs, seeds, syncs, serves a restore package,
restarts; with `--first-image` it first runs the older image, then swaps the container to the new one on the same volumes and checks
that identity, TLS pin, data and the paired peer's sync survive. `fixture_rehearsal.py` runs old -> new -> old again on a private volume
made from a copy of a real node's data (network `none`, so the copy cannot reach anyone) and compares identity, migration ledger,
counts and key files at every step. `phone_e2e.py` drives the Android app on a real test phone against the image.

## Sealed metadata (Step A, 2.6.0)

2.6.0 ships switched off; nothing below changes a blind node until the owner activates metadata encryption on a full node
and the activation reaches it (docs/design/step-a-spec.md, ADR 0009). After that:

* **What it stores.** Sealed events (`sealed`, protocol 4) are stored exactly as their author signed them and never opened
  (`tbl_event.apply_state = 2`, relayed); control events (whitelist, recovery, keys, activation) apply as before; a protocol-1..3
  content event is accepted and not stored. Beside the log it keeps one **sealed baseline** - an encrypted, signed copy of a full
  node's replicated tables it cannot open - in `tbl_baseline` / `tbl_baseline_blob` and `<data>/baselines/current/`
  (`BaselineStore`). It adopts a baseline only forward (Lamport time, key generation and the covered part of its own log never go
  back), only from a superadmin it trusts, and only when every blob the baseline lists is stored. It cannot open the baseline, but it
  checks what needs no key: the container must be a `BMBCH1` frame for the key the signed manifest names (header, chunk lengths that
  tile the file), so a database copy, a ZIP or a JSON dump is never taken for the sealed copy, and it serves the baseline only while
  all four files are the signed ones (hash and size are re-checked, cached per size and modification time for an hour); a changed
  baseline is not served, and sending the same baseline again puts its files back.
* **The first adoption purges** the plaintext a 2.5 blind node materialised: every row of every table not listed in
  [OPERATIONAL-TABLES.golden.txt](OPERATIONAL-TABLES.golden.txt) and every clear content event, with `secure_delete`, then `VACUUM`
  and a truncated WAL; the node says its store is opaque (`opaque_store`) only after both succeeded. A purge that did not finish (no
  space for the rebuild, a reader holding the database, a crash) is done again by the next adoption and by the log trimmer at
  start-up and on every run (once a baseline was adopted, even if its files cannot be served at the moment), and the status keeps
  `metadata_in_backups` true until then. Copies made before (an old `beememorybank.db.pre-seed`, the old database of a cutover that never finished,
  backups taken earlier) are not touched: the status lists them (`legacy_copies`, `metadata_in_backups`) and the owner retires them.
* **Seeding.** `POST /api/blind/seed` takes a format-2 package (`bmb-blind-package-v2`): a `full` one for the first seed (pair code)
  and a `baseline` one for a reseed (superadmin token; the sender ships missing blobs first). The node's log and push positions are
  kept. Everything that can be refused (an older baseline, a missing blob, a bad signature, a delta stream that breaks the format, a manifest with a missing or malformed field, a control database this node cannot import or whose rows no live applier would have
  written - a baseline or delta Lamport time outside [0, 2^53-2], a control row Lamport time past 2^53-2, a key id of the wrong shape, a box whose preset its kind may not have, an activation naming a key the package lacks -,
  a first seed that leaves no Lamport time to grant) is
  decided before the baseline is adopted and before the node's whitelist, control tables or positions change; the serving node's position moves last, after every delta
  is stored. At the activation the node asks for a reseed itself (the my-standing reseed flag), and once activated it refuses a format-1
  package. A refused seed is written to the node's log at Warning and answered to the sender with the same reason: a fixed sentence about the check that failed
  (`BlindSeedRefusals`): the sentence of the check that found the problem (a package file name, a hash, a count), never a path of this node, an address, a key, a
  pair code or the text of a parser or an I/O error (only its type is named); the name of one of the cutover's own files is the last element only
  (`blind-cutover`, `new.db`). A sentence that still looks like a path or an address leaves the node as the generic one.
* **Serving.** `GET /api/blind/replica` serves a format-2 `full` package, or with `?have=<baseline id>` 304 (still current) or a
  `baseline` package; `GET /api/blind/restore/package` a `full` package with blobs. With no sealed baseline yet both answer 503 with
  `Retry-After`, never a format-1 package. The routes are the same 53 ([ROUTES.golden.txt](ROUTES.golden.txt)).
* **Status.** Neither the status, the console page nor `bmb blind status` shows a note count any more (from 2.6.0, activated or
  not): they show the baseline's age and key generation, the delta count and size, the stored blobs and bytes, and an alarm when the
  baseline is older than 7 days while deltas arrive. The anchor is reported without a digest verdict ("checked by a restoring full
  node"): there is no content to compute one over.
* **Backups** add `baselines/current/` to every restic snapshot; a backup folder is a restore source.
