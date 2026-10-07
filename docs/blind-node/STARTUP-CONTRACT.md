# Blind node: startup contract

What `BeeMemoryBank.BlindNode` does between `main` and the first request, step by step, and where each step
came from in `BeeMemoryBank.Api` (`Program.cs`, `Startup/ApiServices.cs`, `Startup/ApiStartupTasks.cs`,
`Startup/ApiPipeline.cs`). The order is load-bearing; `BlindNodeStartupTests` pins it.

## Process (Program.cs)

| # | Step | Same as Api | Note |
|---|------|-------------|------|
| 1 | `TaskScheduler.UnobservedTaskException` handler (log + `SetObserved`) | yes | fire-and-forget sites must not crash the process |
| 2 | `Utf8Console.EnableForRedirectedOutput()` | yes | |
| 3 | `AddLoopbackForwardedHeaders` | yes | `UseLoopbackForwardedHeaders()` is called ONCE, right after `Build()` - never in the startup tasks (two passes consume two hops of a spoofed `X-Forwarded-For`) |
| 4 | `BMB_INTERNAL_KEY` fail-fast in Production | yes | refuses to start when the entrypoint was bypassed |
| 5 | data path: `BeeMemoryBank:DataPath`, else `BMB_DATA_PATH`, else `./data` | yes | |
| 6 | `VaultStartup.Enter(dataPath)` -> re-key swap finished/rolled back, `vault.lease` held shared | yes | the lease lives and ends with the host |
| 7 | `.internal-key` file fallback (development only) | yes | production always exports the key |
| 8 | `AddBlindNodeServices(dataPath)` | **new**, derived | see below |
| 9 | `Build()`, resolve `VaultLease` | yes | |
| 10 | `UseLoopbackForwardedHeaders()` | yes | |
| 11 | `RunBlindNodeStartupTasksAsync` | **new**, derived | see below |
| 12 | `RekeySwapResolver.CompleteFirstStart(dataPath)` | yes | the first start on a swapped-in vault succeeded |
| 13 | `UseBlindNodePipeline()` | **new**, derived | ExceptionHandler -> PublicSurface -> RateLimit -> Maintenance |
| 14 | `MapBlindNodeSurface()` | **new**, derived | `/health`, `/api/version`, sync, blind seed/node/replica/restore |

## Startup tasks (in this order)

1. `BlindSeedCutover.Recover` - a seed cutover a crash interrupted is finished or rolled back **before anything opens the database**.
2. `MigrationRunner.RunMigrationsAsync` - needs no DEK; the full, byte-identical SQL set is embedded (the runner deletes the ledger rows of migrations it cannot find - see ADR 0001).
3. `StoredEventRepair.RunAsync`.
4. `BlindRoleStartup.RunAsync` - blind temp dir cleaned, `RefuseDekMaterial` (key slots / wrapped agent DEK / `os-auto-unlock.dat` / `update-unlock.dat` -> refuse to start), identity from the key file (`EnsureIdentityAsync`), the three secrets in clear (key file, TLS pfx, `blind/settings.json`) made owner-only where an older build left them with the folder's inherited Windows ACL (`RepairSecretPermissions`: repaired in place and logged, a failure is a warning, never a refusal), pending restores and rotations retried.
5. `FolderBootstrapper.RunIfNeededAsync`.
6. Lamport clock restored from the database.
7. Crash-recovery sweeps: stuck restore rows -> Failed, leftover `beememorybank.db.standalone-staging` removed, stuck `Committing` DEK rotations originated here -> Failed, stale `Proposed` rows (> 24 h) -> Cancelled, orphan media files reconciled.
8. Media blob backfill + redundant `.enc` sweep, in the background.
9. `ApplicationStopping` -> `SessionService.ClearPendingDek()` + `Lock()`.

## Deliberately NOT in the blind node

| Api step | Why it is gone |
|----------|----------------|
| `BMB_READY_FILE`, `BMB_STDIN_LIFELINE` | desktop orchestrator (bmbd) hooks; a container is stopped by the runtime |
| `ChatDbInitializer` | no AI chat |
| OS auto-unlock + update unlock handoff | would put the master DEK into the process |
| concept-tag embedding backfill | no model |
| agent auth, caller scope, MCP guards | no agents/users/MCP; they would unlock a session |
| `UseWhen("/mcp")`, `MapOpenApi`, all non-blind endpoint groups | not served, so not reachable - including endpoints added to the Api later |

## Listeners

`BMB_BLIND_HTTPS_PORT` (5610 in the image) opens TLS on all interfaces with the node's self-signed certificate
(`BlindTlsCertificate.LoadOrCreate(dataPath)`); `BMB_BLIND_LOCAL_PORT` (default 5612) keeps a loopback HTTP port
for the console and the CLI (internal key). Without `BMB_BLIND_HTTPS_PORT` the process listens where
`ASPNETCORE_URLS` says.

## Identity artifacts that must survive an upgrade (data volume)

`beememorybank.db` (+ `-wal`/`-shm`), the node key file (`FileNodeKey.FileName`), the TLS pfx, `.internal-key`
(development only), `blind-tmp/` (cleaned at start), backup settings and restic repository paths under
`BlindPaths`. A new image must open all of them unchanged; the compatibility fixture (a copy of a real node's
volume) is the test. The key file, the pfx and the backup settings are created owner-only (`OwnerOnlyFile`: 0600 on
Linux/macOS; on Windows a protected DACL without Everyone/Users/Authenticated Users and the like, with the node's
account and SYSTEM in full control, named accounts and Administrators kept).
