# Blind node: what changed against the Api in the blind role

The new host replaces `BeeMemoryBank.Api` + `BMB_ROLE=blind`. This page lists, per area, what is the same, what is
gone on purpose and how each claim is checked. Anything not listed here is unchanged.

## Container (`docker/blind`)

| | before (<= 1.0.16) | now |
|--|--|--|
| API program | `dotnet /app/api/BeeMemoryBank.Api.dll` with `BMB_ROLE=blind` | `dotnet /app/api/BeeMemoryBank.BlindNode.dll` (the role is the type of the host; `BMB_ROLE` is ignored) |
| CLI | full `bmb` (every verb, ONNX/ImageSharp/Rekey/... behind it, 279 MB) | `bmb blind ...` only (the three blind command files of the full CLI, linked; 0.6 MB) |
| console | `BeeMemoryBank.BlindConsole` | unchanged |
| ports, volumes, env, healthcheck, restic | | unchanged (`BlindDockerPackagingTests`); since 2.4 only 5610 is declared `EXPOSE` (5611 stays published on the host's loopback by the compose files) |
| distribution | built from source | also the ready-made `ghcr.io/ultrathinker/beememorybank-blind` (amd64 + arm64), `docker/blind/compose.image.yaml` |
| publish | one framework-dependent publish per program, native assets of every platform | `-r linux-x64|linux-arm64 --no-self-contained -p:UseAppHost=false` per `TARGETARCH` |

Verified by `tools/blind-e2e/e2e_docker.py` (real full node, pair, seed, sync, restore package, restart; with
`--first-image` the old image is swapped for the new one on the same volumes) and
`tools/blind-e2e/fixture_rehearsal.py` (both images on a private copy of a real node's volume, network `none`).

## CLI

| verb | old image | new image |
|------|-----------|-----------|
| `bmb blind init|status|pair-code|restore-code|backup now|list|verify|copy|jobs|wipe` | yes | yes - the same source file, `BlindCommand.cs` |
| `bmb init|join|unlock|article|snapshot|agent|dek-rotate|restore|rekey|status` | present, but meaningless on a blind volume (they act on a vault the node does not have; `unlock`, `init`, `join` would refuse or fail) | not in the binary |

`BlindCliTests` (in `BeeMemoryBank.Cli.Tests`) exercise `BlindCommand` against a scripted Api; the file is the one the
new binary compiles.

## Console

Not changed. It is a separate program with no library dependency; its security contracts (host filter, CSRF, cookie/session
generation, the internal key never reaching the browser, loopback binding) stay where they were and keep their tests
(`BeeMemoryBank.BlindConsole.Tests`, `BlindConsolePasswordTests`, `BlindWipeAndConsoleTests`).

## Startup, pipeline, routes, services

See [STARTUP-CONTRACT.md](STARTUP-CONTRACT.md). Routes: the 47 in [ROUTES.golden.txt](ROUTES.golden.txt), the same list for
the Api in the blind role and for this host. Registrations: `BlindNodeServices` (host) over the shared `AddNodeStorage` / `AddNodeCore` / `AddNodeSync` modules - each full-node-only
registration of `ApiServices.cs` / `AddStorage` / `AddCore` / `AddSync` (the last three live in `BeeMemoryBank.Vault`) is absent, not skipped.

## Things kept on purpose that a stricter node might drop (decide later, separately)

* `POST /api/sync/probe-relay`, `GET /api/sync/snapshot/for-join`, `GET/POST /api/sync/invisible`, `GET /api/sync/quarantine`:
  mapped by `MapSyncEndpoints` on a blind node today; unchanged here so the mesh behaves as before.
* The data volume keeps `chat.db` (an empty file the old Api created); the new host neither creates nor opens it.
