# The SQLite engine

Since 2.5.3 every node, desktop app and phone app runs on **SQLite3 Multiple Ciphers** (SQLite3MC, MIT licence,
<https://github.com/utelle/SQLite3MultipleCiphers>) instead of the stock SQLite build that `Microsoft.Data.Sqlite` brought along.
The release changes the engine and nothing else: **no key is set anywhere, and every database stays a plain SQLite file.** A vault
written by 2.5.3 opens on a 2.5.1 node and a vault written by 2.5.1 opens on a 2.5.3 node (rolling upgrades, a restore from an older
backup, a downgrade), and any other SQLite 3 tool still reads the files.

Since 2.6.0 the same engine encrypts a node's databases when the owner chooses "Encrypt this node" (see "Encrypted databases (2.6.0)"
below). A node the owner has not encrypted behaves exactly as described in the rest of this page; nothing here changes for it.

## Why a separate release

SQLite3MC reads and writes plain files exactly like stock SQLite, and can encrypt a database page by page when it is given a key.
The product will use that in a later release. Swapping the library first, with databases that stay plain, proves the packaging on every
platform (Windows, Linux x64 and arm64, macOS, Android, iOS) on its own, and a problem with a platform shows up in a release that cannot
have damaged a database.

## What is referenced

| Package | Where | What it is |
|---|---|---|
| `Microsoft.Data.Sqlite.Core` 10.0.11 | `BeeMemoryBank.Storage`, `Vault`, the Migrator | the managed API (`SqliteConnection`, namespace `Microsoft.Data.Sqlite`): the code did not change |
| `SQLite3MC.PCLRaw.bundle` 2.4.0 (SQLite 3.53.4) | `BeeMemoryBank.Storage` | the native libraries and the SQLitePCLRaw provider; flows to every program that references Storage |
| `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 | `tests/BeeMemoryBank.Storage.Tests` only | the stock native library, kept as a test tool: `Engine/StockSqlite.cs` loads it to prove that files written by one engine open on the other |

Never reference the full `Microsoft.Data.Sqlite` package: it brings a second SQLitePCLRaw bundle (e_sqlite3), and two bundles in one
program fight over the provider. `SQLitePCLRaw.core` arrives as 3.0.2 (what the SQLite3MC provider needs), above the 2.1.12 that
`Microsoft.Data.Sqlite.Core` asks for; NuGet resolves it. `.github/dependabot.yml` ignores major bumps, and SQLitePCLRaw 3 is not a
direct reference, so nothing proposes it on its own.

The native file by platform (`Directory.Packages.props` pins the version):

| Platform | File | How it gets into the program |
|---|---|---|
| Windows x64 | `sqlite3mc.dll` | `runtimes/win-x64/native`, flattened by a RID-specific publish |
| Linux x64 and arm64 (Docker images) | `libsqlite3mc.so` | `runtimes/linux-*/native`; needs glibc 2.34 or newer and nothing else (`aspnet:10.0` has 2.39) |
| macOS arm64 (full and blind package) | `libsqlite3mc.dylib` (`Contents/MacOS/api/` in the full app) | signed with the other Mach-O files by the packer's generic loop; `scripts/pack-macos-full.sh` checks that it is there |
| Android (full and blind app) | `libsqlite3mc.so` per ABI | the package has `runtimes/android-*/native` of its own, so the apps no longer exclude native assets (the old package had desktop RIDs only, and the RID fallback handed the app a glibc library) |
| iOS (full and blind app) | `sqlite3mc.a`, static | the SDK links it into the app by itself; no `NativeReference` is needed (checked in a simulator app, below) |

## What differs from the stock build

Both builds are compiled with the same defaults that matter here (`SQLITE_DQS=0`, foreign keys and recursive triggers on by default,
FTS5, WAL). The differences, from `PRAGMA compile_options` and the pragma defaults of a new connection:

* `SQLITE_TEMP_STORE=2` (temp data in memory unless asked otherwise) against 1 (files) in the stock build. An in-place `VACUUM` of a
  vault that shrinks from 351 to 175 MiB peaked at 257 MiB working set in memory, 57 MiB with files. The two connection factories,
  the restore compaction and the rekey scrub therefore run `PRAGMA temp_store=FILE` on plain databases: the memory profile of 2.5.1 is
  kept. (An encrypted database chooses memory on purpose, because the engine does not encrypt temporary files: see below.)
* `SQLITE_SECURE_DELETE`: deleted content is overwritten with zeros by default (`PRAGMA secure_delete` is 1, stock 0). The code that
  needs it (`DEK rotation`, `StoredEventRepair`, the rekey scrub) already asked for it explicitly. Deletes write a little more.
* `SQLITE_ENABLE_CARRAY`, `SQLITE_ENABLE_PERCENTILE`: two more SQL features, unused.
* `SQLITE_USE_URI`: a `Data Source` that starts with `file:` is read as a URI (no path of the product does).
* The SQL function `sqlite3mc_version()` exists, and the cipher pragmas (`PRAGMA cipher`, `key`, `rekey`) are known; nothing is encrypted until a key is set, and the product sets none.

## Encrypted databases (2.6.0)

An encrypted ("sealed") node keeps `beememorybank.db` and `chat.db` as SQLCipher 4 files written by SQLite3MC: 4096-byte pages, each page
AES-256-CBC with an 80-byte tail (16 bytes of IV and a 64-byte HMAC-SHA512), no `SQLite format 3` header, so the file looks like noise. The
key of each file is a raw 256-bit page key derived from the node's database key (one key per file: a page of `beememorybank.db` does not
decrypt in `chat.db`); the database key is opened by the master-key chain, not by this page (design: `docs/design/step-b-spec.md`
sections 3 and 7; operator guide: `docs/sealed-database.md`). Which mode a data directory is in is decided from the files
(`VaultStoreFiles.Diagnose`), never from a setting.

**How a sealed file is opened.** Everything goes through `DbConnectionFactory` (and `ChatDbConnectionFactory`), or, for one file outside
a factory (a staging copy, the re-key), through `SealedSqlite.OpenKeyed` or `DbConnectionFactory.OpenFile`.

* The connection string never contains the key: `file://<path>?cipher=sqlcipher&legacy=4`. Right after the connection opens, the key goes
  to the native connection with `sqlite3_key_v2` as `x'<64 hex>'` in a byte array that is zeroed at once. A key written into a
  `Password=` would stay reachable: `Microsoft.Data.Sqlite` (10.0.11, measured) keeps every connection string, pooled or not, in its
  provider-wide pool registry, and neither `ClearPool` nor `ClearAllPools` removes the entry; only its private prune timer does, after
  several runs. The spec text of 7.1/7.2 describes the `Password=` recipe; this is the deliberate deviation, and it keeps the promise of
  spec 3.3: after the vault closes, the key is nowhere (the tests walk the factory, the key source and the provider's registry).
* Pooling stays: the pooled string carries a non-secret `bmbpool=<factory>-<generation>` parameter, so one pool belongs to one open
  generation, and a connection taken from the pool is recognised as already keyed by its native handle object. A pooled keyed connection
  costs the same as a plain one (about 0.02 ms); a new physical connection pays for the key once.
* `temp_store=MEMORY` on every keyed connection (a plain one keeps `FILE`), because SQLite3MC does not encrypt temporary files. A large
  sort or `VACUUM` of an encrypted vault therefore uses memory instead of disk.
* A wrong key, and a plain file opened with a key, both end in SQLite error 26 and become `VaultKeyMismatchException` (no inner
  exception, no key in the message). Nothing is retried without a key. A factory whose vault is closed throws `VaultSealedException`
  on `CreateConnection` and `OpenFile`: there is no plain fallback.
* `VACUUM INTO` from a keyed connection writes a copy under the same key; a copy under another key uses
  `SqlCipherScheme.VacuumIntoTarget` (the re-key and the migration); a plain copy of a sealed database is never written on a sealed node.
* The sealed WAL and rollback journal have plain headers (page numbers, salts, checksums) with encrypted page images, so the "is this
  file plain" test looks at the page images and not at the header magic of a sidecar file (`VaultStoreFiles.LooksLikePlainPage`).
* A read of a sealed file that must leave its directory byte-identical (the re-key reads the old vault) opens it with
  `immutable=1`: a read-only WAL open would create `-wal` and `-shm` beside it.

**Plain images without a plain file.** A sealed file is turned into a plain SQLite image page by page by `PlainImageReader` (a seekable
read-only stream that checks the HMAC of every page; one flipped byte anywhere is refused) and back by `PlainImageImporter` and
`SealedFileWriter` (a new sealed file, created with `CreateNew`, removed again on a failure). The engine has no `sqlite_dbpage`
(`SQLITE_ENABLE_DBPAGE_VTAB` is not compiled in), so this codec is the only way to get a plain image from a sealed file without a plain
file ever touching the disk. The snapshot of a sealed node is built this way (`Bmbdb2Container`, the same BMBDB2 container
`SnapshotService` has always written).

* An image of 4096-byte pages with at least 80 reserved bytes (every image a 2.6 node makes) is streamed. Any other image (from a 2.5
  node or a plain 2.6 node) is loaded with `sqlite3_deserialize` into native memory, read-only, and written with `VACUUM INTO` under the
  page key. That is held in memory up to a cap: 1 GiB, 256 MiB on a phone, `BMB_SEALED_IMPORT_MEMORY_MB` overrides it. Above the cap the
  import throws `SealedImportTooLargeException`; it never falls back to a plain file.
* An image with a page size other than 4096 is refused (`InvalidDataException`): `VACUUM INTO` keeps the page size.
* The page format is checked in `docs/design/step-b-spec.md` Appendix C against files SQLite3MC wrote (zero HMAC failures, `integrity_check`
  and FTS5 answers on the rebuilt image).

**No hand access to a sealed file.** `sqlite3`, DB Browser and every other tool see an unreadable file. A copy of the file is useless
without the master password (the database key is in the boot store only wrapped under the master key), and a snapshot, a backup or the
re-key is how data leaves or is changed. Do not set a key on a vault by hand (`PRAGMA key`, `PRAGMA rekey`): the node would no longer open it.

**Where a page key may appear in the source.** `docs/design/key-string-sites.golden.txt` lists the files that may call `sqlite3_key_v2`
or build a string that holds a key (`SqlCipherScheme.Password`, `SqlCipherScheme.VacuumIntoTarget`). `KeyStringSitesTests` scans libs,
server, desktop, mobile and tools and fails for a file outside the list; a new entry is a review question (does the site clear the
key and keep it out of logs, exceptions and cache keys?).

**Not checked here.** Linux x64/arm64 and macOS runs of the sealed files (the `fstat`-based file identity of `FileIdentity` among them)
are the gate of spec 15.5, run by the owner or the orchestrator; the tests carry no Windows-only assumption and the one
platform-specific assertion per OS is skipped on the other family.

## Checked when the engine was swapped (2.5.3)

* iOS: the .NET iOS SDK (workload 26.5, .NET 10.0.300) links `sqlite3mc.a` into the app without a `NativeReference` and without a call to
  `SQLitePCL.Batteries_V2.Init()` (`Microsoft.Data.Sqlite.Core` finds the provider by reflection, and partial trimming keeps it). A
  throwaway simulator app that ran the product's own `DbConnectionFactory` and `MigrationRunner` (35 migrations), FTS5, `VACUUM`,
  `temp_store=FILE` and a keyed round trip answered `select sqlite3mc_version()` with `SQLite3 Multiple Ciphers 2.4.0` in a Debug and in a
  Release (partially trimmed) build; the same app built for `ios-arm64` (Release, ahead-of-time) links the device slice. Not run on a
  device. Both real iOS apps build for the simulator and carry the provider assemblies.
* Android: the Release APKs of both apps hold `lib/arm64-v8a/libsqlite3mc.so` and `lib/x86_64/libsqlite3mc.so` (bionic builds, 16 KB
  aligned) and no `libe_sqlite3.so`; not run on a device.
* macOS: the full and the blind package build (unsigned); `api/libsqlite3mc.dylib` (arm64, minimum macOS 11.0, depends only on libSystem)
  signs ad hoc with the other files, and the headless smoke of the full app (`scripts/smoke-macos-full.sh`, cases api and stdin) passes.
* Docker: both images build and pass `scripts/smoke-docker.sh`; the `linux-arm64` publish of the Api and of the blind node carries the
  aarch64 `libsqlite3mc.so`.

## What guards it

`tests/BeeMemoryBank.Storage.Tests/Engine/`:

* `NativeEngineTests`: the loaded library answers `sqlite3mc_version()`; `sqlite_version()` is not older than 3.50.2 (the floor of
  CVE-2025-6965, the rule `Storage.csproj` has always carried); FTS5 and WAL are there; a database without a key is a plain file (magic
  header, no per-page reserve, content readable in the file); the two user functions (`unicode_contains`, `sha256`) work; temp files
  stay on disk.
* `PlainDatabaseCompatibilityTests` with `Fixtures/plain-2.5.1.sqlite` (a vault written by the 2.5.1 tree on the stock engine): it opens,
  migrates nothing, answers FTS5, takes writes, `VACUUM INTO` and the backup API; a vault written by the current engine opens on the stock
  engine, takes a write there and reads back; the header bytes agree with the 2.5.1 file.
* `tests/BeeMemoryBank.BlindMobile.Tests/NativeSqliteAssetsTests`: restores each Android app project by itself (`dotnet restore --no-dependencies`,
  obj folder redirected to a temp folder, so it needs no build of the apps and touches none of their files) and checks the resolved graph:
  the SQLite3 Multiple Ciphers packages, `libsqlite3mc.so` of exactly each Android RID, none of the stock engine's packages. It needs the
  maui-android workload, as any build of these projects does.
* `tests/BeeMemoryBank.MacFullPackage.Tests` and `scripts/pack-macos-full.sh` name `api/libsqlite3mc.dylib`.

For the encrypted databases of 2.6.0, `tests/BeeMemoryBank.Storage.Tests/Sealed/`:

* `KeyedFactoryTests`: a keyed factory writes and reopens (pooled and not), its files hold no canary and no SQLite magic, a wrong key and
  a plain file are refused, a closed factory throws, the key is found nowhere once the vault is closed (the reflective walk of the factory,
  the key source and the provider's pool registry), `temp_store` is 2 on keyed connections and 1 on plain ones, a large keyed sort leaves no
  temporary file.
* `PageCodecTests`, `PlainImageImporterTests`, `Bmbdb2ContainerTests`: a sealed file becomes a plain image stock SQLite opens, a flipped
  byte anywhere is refused, the writer's output opens in SQLite3MC, the streamed and the in-memory import, the cap, the container layout and
  its compatibility with `SnapshotService` in both directions.
* `SealedSqliteTests`: an immutable read creates no file and changes no byte.
* `DetectModeTests`: `VaultStoreFiles.Diagnose` and `DetectMode` with real SQLite3MC files.
* `KeyStringSitesTests`: the allow-list above.

## Changing the version

1. Read the release notes of SQLite3MC and of the NuGet package (`https://github.com/utelle/SQLite3MultipleCiphers-NuGet/releases`).
   The NuGet package follows the core release by days to weeks; take only a package whose SQLite is at least the current floor.
2. Change `SQLite3MC.PCLRaw.bundle` in `Directory.Packages.props` (and nothing else), build, run `Storage.Tests` and the other test
   projects.
3. Check the platforms the tests cannot reach: the glibc the Linux library needs (`GLIBC_2.34` today; the `aspnet` base image must have
   it), the Mac package (`scripts/pack-macos-full.sh`, `scripts/pack-macos-blind.sh`), the Android ABIs (16 KB page alignment of the `.so`
   files, as before), the iOS apps (`scripts/build-ios-full.sh simulator`, `scripts/build-ios-blind.sh simulator`; `select
   sqlite3mc_version()` in a simulator app).
4. Update the version in `THIRD-PARTY-NOTICES.txt`.

## For operators

Nothing changes for a node that is not encrypted: the files are what they were, `sqlite3` and DB Browser still open them (take a copy
first, or use the snapshot and backup features, as before). An encrypted node's files cannot be opened by hand (see above). The notices are in `THIRD-PARTY-NOTICES.txt`, inside both Docker images.
