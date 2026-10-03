# Blind node: what it is made of

A blind node stores and relays the mesh's ciphertext and cannot read any of it: no master DEK, no master password,
no sessions, no users, no authoring of events. Until 1.0.16 it was the full `BeeMemoryBank.Api` started with
`BMB_ROLE=blind`, so the container carried all of the application and the role switches were the only thing between a
request and the code that reads. Since BMB-91 it is a program of its own, and the library code it shares with the
Android blind app is one small assembly.

| Piece | Project | What it is |
|-------|---------|-----------|
| host | `server/BeeMemoryBank.BlindNode` | own `Program`, container registrations, pipeline, startup tasks; **links** the 76 Api source files a blind node needs (`linked-api-files.props`) |
| shared core | `libs/BeeMemoryBank.Blind` | **links** 250 of the 347 source files of Core / Storage / Sync / Crypto / Search (64 % of their lines) under their original names, plus `BlindComposition.cs` (the registrations); embeds every database migration, byte for byte |
| phone client | `libs/BeeMemoryBank.Blind.PhoneClient` | the 13 more library files only the Android blind app needs (replica/pull client, backup seal ...); references `Blind` |
| CLI | `server/BeeMemoryBank.BlindCli` | `bmb`, only `bmb blind ...`; links the full CLI's three blind command files; no library reference |
| console | `server/BeeMemoryBank.BlindConsole` | unchanged: one embedded page and a proxy to the local node |
| Android app | `mobile/BeeMemoryBank.BlindMobile` | references `Blind` + `Blind.PhoneClient` only |
| image | `docker/blind` | the three programs published per `TARGETARCH`, plus restic |
| guards | `tests/BeeMemoryBank.BlindNode.Tests`, `tests/BeeMemoryBank.BlindMobile.Tests` | the blind-only integration tests of `Integration.Tests`, linked and run against this host; route matrix; composition, linked-set and migration guards; the Android boundary guards |
| tools | `tools/blind-link`, `tools/blind-e2e` | recomputes the linked sets by compiling; Docker end-to-end, upgrade/rollback rehearsal on a copy of a live node's data, real-phone check |

Documents: [STARTUP-CONTRACT.md](STARTUP-CONTRACT.md), [ROUTES.golden.txt](ROUTES.golden.txt), [COMPOSITION.md](COMPOSITION.md),
[PARITY.md](PARITY.md), [ADR-0001-link-first.md](ADR-0001-link-first.md).

## The rule that keeps it honest

*What is not linked cannot be in the container.* `tools/blind-link/exclude.txt` lists the Api files a blind node must
never contain (MCP, agent auth, caller scope, chat, update, DEK rotation, the new device's side of a restore); the
closure tool refuses to link them and the host fails to compile if anything needs one. `BlindNodeCompositionTests`
checks the result from the outside: the assemblies the host references, the types it contains, the services it
registers, the hosted services it runs. `BlindRouteMatrixTests` pins the 47 routes it serves. `BlindLinkedSetTests` holds the
checked-in linked sets to the exclusion list.

The honest limit: the originals are not changed, so the blind assembly still contains `SessionService`, the `Crypto` primitives
and the replication code (they are dependencies of the sync code). What it does not contain is any route, middleware or
service that lets a person or an agent open a session or read content. See [COMPOSITION.md](COMPOSITION.md#known-limits-stated-plainly).

## Recomputing the linked sets

```
python tools/blind-link/blind_link.py          # host + Blind: linked-api-files.props, linked-lib-files.props
python tools/blind-link/blind_link.py --phone  # Blind.PhoneClient (needs Blind's props to be current)
python tools/blind-link/registrations.py       # which registrations of AddStorage/AddCore/AddSync survive
python tools/blind-link/stats.py               # how much of each library is linked
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
