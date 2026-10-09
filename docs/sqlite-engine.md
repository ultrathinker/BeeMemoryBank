# The SQLite engine

Since 2.5.3 every node, desktop app and phone app runs on **SQLite3 Multiple Ciphers** (SQLite3MC, MIT licence,
<https://github.com/utelle/SQLite3MultipleCiphers>) instead of the stock SQLite build that `Microsoft.Data.Sqlite` brought along.
The release changes the engine and nothing else: **no key is set anywhere, and every database stays a plain SQLite file.** A vault
written by 2.5.3 opens on a 2.5.1 node and a vault written by 2.5.1 opens on a 2.5.3 node (rolling upgrades, a restore from an older
backup, a downgrade), and any other SQLite 3 tool still reads the files.

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
  the restore compaction and the rekey scrub therefore run `PRAGMA temp_store=FILE`: the memory profile of 2.5.1 is kept. (The later
  encrypted release chooses memory on purpose, because temp files are not encrypted; that is a decision of that release.)
* `SQLITE_SECURE_DELETE`: deleted content is overwritten with zeros by default (`PRAGMA secure_delete` is 1, stock 0). The code that
  needs it (`DEK rotation`, `StoredEventRepair`, the rekey scrub) already asked for it explicitly. Deletes write a little more.
* `SQLITE_ENABLE_CARRAY`, `SQLITE_ENABLE_PERCENTILE`: two more SQL features, unused.
* `SQLITE_USE_URI`: a `Data Source` that starts with `file:` is read as a URI (no path of the product does).
* The SQL function `sqlite3mc_version()` exists, and the cipher pragmas (`PRAGMA cipher`, `key`, `rekey`) are known; nothing is encrypted until a key is set, and the product sets none.

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

Nothing changes for a node: the files are what they were, `sqlite3` and DB Browser still open them (take a copy first, or use the
snapshot and backup features, as before). The notices are in `THIRD-PARTY-NOTICES.txt`, inside both Docker images.
