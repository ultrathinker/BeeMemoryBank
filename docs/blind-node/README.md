# Blind node: what it is made of

A blind node stores and relays the mesh's ciphertext and cannot read any of it: no master DEK, no master password,
no sessions, no users, no authoring of events. Until 1.0.16 it was the full `BeeMemoryBank.Api` started with
`BMB_ROLE=blind`, so the container carried all of the application and the role switches were the only thing between a
request and the code that reads. Since BMB-91 it is a program of its own. Since the vault split (BMB-99, 2.0.1) the libraries
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
| host | `server/BeeMemoryBank.BlindNode` | own `Program`, container registrations, pipeline, startup tasks; **links** the 74 Api source files a blind node needs (`linked-api-files.props`) |
| shared libraries | `libs/BeeMemoryBank.{Core,Crypto,Search,Storage,Sync}` | what every node needs: store, relay and apply ciphertext, the sync protocol, the external-key identity, pairing and TLS pinning, backups, the recovery set a node receives and serves; `NodeDependencyInjection` in each (`AddNodeStorage` / `AddNodeCore` / `AddNodeSync`); every database migration is embedded in Storage |
| phone client | `libs/BeeMemoryBank.Blind.PhoneClient` | the 14 library files only the Android blind app (and the full node's restore of a phone backup) need (replica and pull clients, backup seal ...); on the shared libraries; the Linux host does not contain it |
| blind app core | `libs/BeeMemoryBank.Blind.AppCore` | platform-neutral Android/desktop blind-app state, pairing, receive-only composition and backup orchestration; it depends only on Core, Crypto, Storage, Sync and PhoneClient, never Vault or an OS UI API |
| vault (full node only) | `libs/BeeMemoryBank.Vault` | sessions, master-key handling, content crypto, recovery creation and opening, the search index, article/user/role/tree/import services, full-node repositories, `AddCore`/`AddStorage`/`AddSync`; **no blind program references it** |
| CLI | `server/BeeMemoryBank.BlindCli` | `bmb`, only `bmb blind ...`; links the full CLI's three blind command files; no library reference |
| console | `server/BeeMemoryBank.BlindConsole` | unchanged: one embedded page and a proxy to the local node |
| Android app | `mobile/BeeMemoryBank.BlindMobile` | thin MAUI/Android adapters over `Blind.AppCore` (Keystore, committed SharedPreferences and WorkManager), plus the shared libraries + `Blind.PhoneClient`; never the Vault |
| Windows app | `desktop/BeeMemoryBank.BlindDesktop`, `desktop/BeeMemoryBank.BlindDesktop.Windows` | a quiet tray app and one small window over `Blind.AppCore` (Avalonia; the host is cross-platform, the macOS head reuses it). The Windows assembly implements the AppCore seams: DPAPI secrets, atomic JSON state, AC power and network cost from the Windows APIs, HKCU Run autostart. In-process timer scheduler (15-minute sync, hourly long job); it is a client of a listening node like the Android app: it starts no web server, no child process and listens on no port; no Vault, Hosting, full Desktop or Node reference |
| iPhone app | `mobile/BeeMemoryBank.BlindIos` | the same blind copy on iOS (MAUI, `net10.0-ios`, built on a Mac): thin iOS adapters over `Blind.AppCore` (Keychain items readable after first unlock on this device only, the data folder with file protection and no device backup, BGTaskScheduler rounds, a local "no contact" notification), plus the shared libraries + `Blind.PhoneClient`; never the Vault. Source-only for now; see [IOS.md](IOS.md) and [IOS-DISTRIBUTION.md](IOS-DISTRIBUTION.md). |
| image | `docker/blind` | the three programs published per `TARGETARCH`, plus restic; released as `ghcr.io/ultrathinker/beememorybank-blind` (amd64 + arm64, `.github/workflows/docker-publish.yml`), run by `docker/blind/compose.image.yaml` |
| guards | `tests/BeeMemoryBank.BlindNode.Tests`, `tests/BeeMemoryBank.BlindMobile.Tests`, `tests/BeeMemoryBank.BlindDesktop.Tests`, `tests/BeeMemoryBank.BlindIos.Tests` | the blind-only integration tests of `Integration.Tests`, linked and run against this host; route matrix; composition, linked-set and migration guards; the Android, Windows and iPhone boundary guards (the Windows one scans the published folder, the iPhone one the built `.app` and the golden list `IOS-APP.golden.txt`) |
| tools | `tools/blind-link`, `tools/blind-e2e`, `tools/ios-e2e` | recomputes the linked Api set by compiling; Docker end-to-end, upgrade/rollback rehearsal on a copy of a live node's data, real-phone check; the Windows app's end-to-end check is the opt-in `DesktopEndToEndTests` (`BMB_E2E_DESKTOP=1`) |

Documents: [STARTUP-CONTRACT.md](STARTUP-CONTRACT.md), [ROUTES.golden.txt](ROUTES.golden.txt), [COMPOSITION.md](COMPOSITION.md),
[PARITY.md](PARITY.md), [ADR-0001-link-first.md](ADR-0001-link-first.md), [ADR-0002-vault-split.md](ADR-0002-vault-split.md).

## The rule that keeps it honest

*What is not in a referenced assembly cannot be in the container.* Two mechanisms, one per layer:

* the host still LINKS the Api source files it needs (`linked-api-files.props`); `tools/blind-link/exclude.txt` lists the Api files a blind node
  must never contain (MCP, agent auth, caller scope, chat, update, DEK rotation, the new device's side of a restore, the full snapshot encrypt/restore,
  the PC-side management of blind nodes), and `BlindLinkedSetTests` holds the checked-in set to it;
* the libraries are referenced as assemblies, and the one that holds the vault is simply not referenced. `BlindBoundaryTests` (host) and
  `AppBoundaryTests` (Android app) read the metadata of every application assembly of the output and fail if one defines, references or calls
  a vault type or a member that hands out the master data key (`docs/vault-split/vault-types.txt` is the generated list of vault types;
  `VaultBoundaryListTests` keeps it exact and holds the scanner to controls). `BlindNodeCompositionTests` checks the closure, the services and
  the hosted services; `BlindRouteMatrixTests` pins the 47 routes it serves.

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
  package.
* **Serving.** `GET /api/blind/replica` serves a format-2 `full` package, or with `?have=<baseline id>` 304 (still current) or a
  `baseline` package; `GET /api/blind/restore/package` a `full` package with blobs. With no sealed baseline yet both answer 503 with
  `Retry-After`, never a format-1 package. The routes are the same 47 ([ROUTES.golden.txt](ROUTES.golden.txt)).
* **Status.** Neither the status, the console page nor `bmb blind status` shows a note count any more (from 2.6.0, activated or
  not): they show the baseline's age and key generation, the delta count and size, the stored blobs and bytes, and an alarm when the
  baseline is older than 7 days while deltas arrive. The anchor is reported without a digest verdict ("checked by a restoring full
  node"): there is no content to compute one over.
* **Backups** add `baselines/current/` to every restic snapshot; a backup folder is a restore source.
