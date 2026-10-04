# Blind node: what it is made of

A blind node stores and relays the mesh's ciphertext and cannot read any of it: no master DEK, no master password,
no sessions, no users, no authoring of events. Until 1.0.16 it was the full `BeeMemoryBank.Api` started with
`BMB_ROLE=blind`, so the container carried all of the application and the role switches were the only thing between a
request and the code that reads. Since BMB-91 it is a program of its own. Since the vault split (BMB-99, 2.0.1) the libraries
it shares with the Android blind app are the **shared** ones only: the code that holds the master key, opens a session or reads
content is a different assembly, `BeeMemoryBank.Vault`, which no blind program references (see [ADR-0002](ADR-0002-vault-split.md)).

| Piece | Project | What it is |
|-------|---------|-----------|
| host | `server/BeeMemoryBank.BlindNode` | own `Program`, container registrations, pipeline, startup tasks; **links** the 76 Api source files a blind node needs (`linked-api-files.props`) |
| shared libraries | `libs/BeeMemoryBank.{Core,Crypto,Search,Storage,Sync}` | what every node needs: store, relay and apply ciphertext, the sync protocol, the external-key identity, pairing and TLS pinning, backups, the recovery set a node receives and serves; `NodeDependencyInjection` in each (`AddNodeStorage` / `AddNodeCore` / `AddNodeSync`); every database migration is embedded in Storage |
| phone client | `libs/BeeMemoryBank.Blind.PhoneClient` | the 15 library files only the Android blind app needs (replica and pull clients, backup seal ...); on the shared libraries; the Linux host does not contain it |
| vault (full node only) | `libs/BeeMemoryBank.Vault` | sessions, master-key handling, content crypto, recovery creation and opening, the search index, article/user/role/tree/import services, full-node repositories, `AddCore`/`AddStorage`/`AddSync`; **no blind program references it** |
| CLI | `server/BeeMemoryBank.BlindCli` | `bmb`, only `bmb blind ...`; links the full CLI's three blind command files; no library reference |
| console | `server/BeeMemoryBank.BlindConsole` | unchanged: one embedded page and a proxy to the local node |
| Android app | `mobile/BeeMemoryBank.BlindMobile` | references the shared libraries + `Blind.PhoneClient`; never the Vault |
| image | `docker/blind` | the three programs published per `TARGETARCH`, plus restic |
| guards | `tests/BeeMemoryBank.BlindNode.Tests`, `tests/BeeMemoryBank.BlindMobile.Tests` | the blind-only integration tests of `Integration.Tests`, linked and run against this host; route matrix; composition, linked-set and migration guards; the Android boundary guards |
| tools | `tools/blind-link`, `tools/blind-e2e` | recomputes the linked Api set by compiling; Docker end-to-end, upgrade/rollback rehearsal on a copy of a live node's data, real-phone check |

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

`e2e_docker.py` starts the image and a real full node with its own data directory, pairs, seeds, syncs, serves a restore package,
restarts; with `--first-image` it first runs the older image, then swaps the container to the new one on the same volumes and checks
that identity, TLS pin, data and the paired peer's sync survive. `fixture_rehearsal.py` runs old -> new -> old again on a private volume
made from a copy of a real node's data (network `none`, so the copy cannot reach anyone) and compares identity, migration ledger,
counts and key files at every step. `phone_e2e.py` drives the Android app on a real test phone against the image.
