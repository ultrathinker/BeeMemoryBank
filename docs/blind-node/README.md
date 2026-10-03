# Blind node: what it is made of

A blind node stores and relays the mesh's ciphertext and cannot read any of it: no master DEK, no master password,
no sessions, no users, no authoring of events. Until 1.0.16 it was the full `BeeMemoryBank.Api` started with
`BMB_ROLE=blind`, so the container carried all of the application and the role switches were the only thing between a
request and the code that reads. Since BMB-91 it is a program of its own.

| Piece | Project | What it is |
|-------|---------|-----------|
| host | `server/BeeMemoryBank.BlindNode` | own `Program`, container registrations, pipeline, startup tasks; **links** the Api source files a blind node needs (`linked-api-files.props`) |
| CLI | `server/BeeMemoryBank.BlindCli` | `bmb`, only `bmb blind ...`; links the full CLI's three blind command files; no library reference |
| console | `server/BeeMemoryBank.BlindConsole` | unchanged: one embedded page and a proxy to the local node |
| image | `docker/blind` | the three programs published per `TARGETARCH`, plus restic |
| guards | `tests/BeeMemoryBank.BlindNode.Tests` | the blind-only integration tests of `Integration.Tests`, linked and run against this host; route matrix; composition guards |
| tools | `tools/blind-link`, `tools/blind-e2e` | recomputes the linked set by compiling; Docker end-to-end and upgrade rehearsal against a real full node |

Documents: [STARTUP-CONTRACT.md](STARTUP-CONTRACT.md), [ROUTES.golden.txt](ROUTES.golden.txt),
[ADR-0001-link-first.md](ADR-0001-link-first.md), [PARITY.md](PARITY.md).

## The rule that keeps it honest

*What is not linked cannot be in the container.* `tools/blind-link/exclude.txt` lists the Api files a blind node must
never contain (MCP, agent auth, caller scope, chat, update, DEK rotation, the new device's side of a restore); the
closure tool refuses to link them and the host fails to compile if anything needs one. `BlindNodeCompositionTests`
checks the result from the outside: the assemblies the host references, the types it contains, the services it
registers, the hosted services it runs. `BlindRouteMatrixTests` pins the 47 routes it serves.

## Recomputing the linked set

```
python tools/blind-link/blind_link.py          # rewrite linked-api-files.props
python tools/blind-link/blind_link.py --check  # fail if the checked-in props differ
```

## Running the end-to-end check

```
docker build -f docker/blind/Dockerfile -t bmb-blind:dev .
python tools/blind-e2e/e2e_docker.py --image bmb-blind:dev --full-exe <BeeMemoryBank.Api.exe> --work <scratch dir> [--first-image <older image>]
```

It starts the image, starts a real full node with its own data directory, pairs, seeds, syncs, serves a restore
package, restarts. With `--first-image` it first runs the older image, then swaps the container to the new one on the
same volumes and checks that identity, TLS pin, data and the paired peer's sync survive.
