# STAGE 2 Report: Identity and Pairing

## 1. Done
- **Restored shared library and test files to base `46e88776`** (`3982efef`):
  - Addressed Review Findings 1, 2, 4 and Orchestrator guidance.
  - Reverted `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs`, `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs`, `tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`, and `tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs` byte-identical to base `46e88776`.
  - Existing shared members preserved with original behavior; no shared API removed or altered.
- **Removed Vault DI Graph & Implemented Option X Container Composition** (Fix Round 5):
  - Extracted clean DI registration into [`BlindMobileServices.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobileServices.cs): `ConfigureServices(services, connectionString, blindDataDir)`.
  - Removed all calls to `AddCore()` and `AddStorage()`, as well as `using BeeMemoryBank.Core;` and `using BeeMemoryBank.Storage;` from [`MauiProgram.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/MauiProgram.cs).
  - Container registers strictly:
    - Blind phone services (`IBlindPhoneStore`, `IBlindNodeKeys`, `BlindHttpClientProvider`, `MaintenanceDetectingHandler`, `BlindMobilePairing`, `SqliteBlindIdentityRecorder`, `PendingBlindPieces`, `BlindPhoneReset`, `IBlindWorkScheduler`, `IDeviceStateProvider`, `ISafExportService`).
    - Local SQLite connection (`SqliteConnection`).
    - Identity, whitelist, and event repositories required by `SqliteBlindIdentityRecorder`.
  - Enforced via two new tests in [`ForbiddenReferencesTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/ForbiddenReferencesTests.cs):
    - `BlindMobileServices_DoesNotRegisterOrResolveVaultServices`: Verifies that no forbidden vault services (`SessionService`, `ArticleService`, `FolderService`, `TagService`, `KeyManagementService`, `SearchIndexService`, `EmbeddingsService`, `MasterKeyManager`, etc.) or repositories are registered in the DI container or resolvable from `IServiceProvider`.
    - `BlindMobileAssembly_ContainsNoForbiddenCoreStorageCryptoTypeReferences`: Inspects `BeeMemoryBank.BlindMobile.dll` via `System.Reflection.Metadata` and `PEReader` to assert that all `TypeReference` entries into `BeeMemoryBank.Core`, `BeeMemoryBank.Storage`, and `BeeMemoryBank.Crypto` belong strictly to an explicit allow-list of blind-phone contracts, error codes, and local storage abstractions.
- **Cleared Key Buffers on Every Path in Pairing & Provided Format/Consume API** (Fix Round 5):
  - In [`BlindMobilePairing.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs), wrapped `EnsureIdentityAsync` recovery path in `try/finally` blocks ensuring that loaded `backupKey` and `pairingSecret` byte arrays are zeroed with `CryptographicOperations.ZeroMemory` on every exit path.
  - Implemented `WithPhoneCode<TResult>(Func<BlindPhoneCode, TResult> consume)` and `PhoneCodeText()` on `BlindMobilePairing` to load identity secrets, format the phone code, and reliably zero loaded secret and backup key byte arrays in `finally` blocks immediately upon consumption.
  - Updated [`BlindHomePage.xaml.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs) to consume `PhoneCodeText()` so raw key buffers are wiped immediately and never retained in page state.
  - Verified test-first via `EnsureIdentityAsync_OnRecovery_ClearsAllLoadedKeyBuffersOnEveryPath` and `WithPhoneCode_And_PhoneCodeText_ClearsLoadedSecretAndBackupKeyBuffersOnConsume`.
- **Isolated blind pairing and keys in BlindMobile** (`122a8e52`):
  - [`IBlindNodeKeys.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/IBlindNodeKeys.cs): Extends `IBlindPhoneKeys` with `LoadIdentitySeed()` for isolated seed retrieval.
  - [`BlindIdentityRecord.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindIdentityRecord.cs): Local immutable record for SQLite row snapshot with `CanGenerateEmbeddings` tracking.
  - [`BlindMobilePairing.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs): Dedicated blind node pairing coordinator with two-code pairing, durable acceptance protocol, atomic one-time secret consumption, crash cleanup, and Keystore seed validation.
  - [`KeystoreBlindPhoneKeys.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs): Hardware-backed Keystore storage implementing `IBlindNodeKeys` under alias `bmb_blind_seed_v1`. Completely isolated from ordinary app ingest store.
- **Enforced `can_generate_embeddings = 0` invariant & fail closed** (`122a8e52`):
  - Addressed Review Finding 3 & Orchestrator note 4.
  - [`SqliteBlindIdentityRecorder.cs`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs): Reads `can_generate_embeddings` column and throws `InvalidOperationException` unless it is 0.
- **Isolated ingest key verification** (`122a8e52`):
  - Addressed Review Finding 2 & Orchestrator note 3.
  - Regression test `CreateIdentityAsync_UsesIsolatedBlindKeyStore_NeverCallsOrdinaryIngestStore_PreservesOrdinaryKeyByteIdentical` verifies 0 calls to ordinary app ingest store and byte-identical key preservation.
- **Core Stage 2 Deliverables**:
  - Real v=2 SQLite identity persistence (`SqliteBlindIdentityRecorder.cs`).
  - SPKI pin HTTP connection pool isolation (`BlindHttpClientProvider.cs`).
  - Two-code pairing with mandatory SPKI pin (`ec705ce4`).

## 2. Not done
- None. All Stage 2 deliverables, review findings, and orchestrator guidance are completely resolved.

## 3. Deviations
- **Review Finding 1 (Vault Graph Registration) - Option X Implemented per Orchestrator**:
  - Finding: Review noted `MauiProgram.cs` invoked `AddCore()` and `AddStorage()`, bringing in the entire vault dependency injection graph.
  - Resolution: Per Orchestrator binding guidance (Fix Round 5, Option X):
    - Removed `AddCore()` and `AddStorage()` from container composition in `MauiProgram.cs` and `BlindMobileServices.cs`.
    - Retained compile-time project references to `BeeMemoryBank.Core`, `BeeMemoryBank.Storage`, and `BeeMemoryBank.Crypto` for now. Full extraction of `BeeMemoryBank.BlindPhone` into an independent class library is deferred per orchestrator instructions.
    - Verified that no vault services or repositories are registered in DI or resolvable.
    - Enforced boundary via reflection metadata test `BlindMobileAssembly_ContainsNoForbiddenCoreStorageCryptoTypeReferences`, ensuring `BeeMemoryBank.BlindMobile.dll` references no unallowed Core/Storage/Crypto types.
- **Review Finding (Uncleared Key Buffers) - Accepted & Resolved Test-First**:
  - Finding: Review noted loaded key buffers (`backupKey`, `pairingSecret`, `seed`) were not zeroed on every path in `BlindMobilePairing.cs` and format API exposed raw strings without zeroing buffers.
  - Resolution: Accepted and resolved test-first with `CryptographicOperations.ZeroMemory` in `finally` blocks on all paths in `EnsureIdentityAsync`, plus `WithPhoneCode` / `PhoneCodeText` consume API.
- **Review Findings 1, 2, and 4 from Round 4 (Shared files restored & isolated)**:
  - Shared files (`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs`, `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs`, `tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`, `tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs`) remain byte-identical to `46e88776`.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: **Passed: 44, Failed: 0, Skipped: 0** (Duration: 1 s)
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: **Passed: 4, Failed: 0, Skipped: 0** (Duration: 55 ms)
- `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`: **Passed: 68, Failed: 0, Skipped: 0** (Duration: 2 s)
- `dotnet test tests/BeeMemoryBank.Integration.Tests --filter "FullyQualifiedName~BlindPhonePairing"`: **Passed: 15, Failed: 0, Skipped: 0** (Duration: 15 s)
- **How new tests were seen RED first**:
  - `EnsureIdentityAsync_OnRecovery_ClearsAllLoadedKeyBuffersOnEveryPath`: Saw RED with `Expected keys.SecretBuffersClearedCount to be 3 ... but found 1` (recovery path loaded `backupKey` and `pairingSecret` without clearing on normal return or exception). Turned GREEN after wrapping in `try/finally` with `CryptographicOperations.ZeroMemory`.
  - `WithPhoneCode_And_PhoneCodeText_ClearsLoadedSecretAndBackupKeyBuffersOnConsume`: Saw RED with `Expected keys.SecretBuffersClearedCount to be 2 ... but found 1`. Turned GREEN after zeroing loaded buffers in `finally`.
  - `BlindMobileServices_DoesNotRegisterOrResolveVaultServices`: Saw RED when temporary `services.AddSingleton<SessionService>()` was registered (`Forbidden vault service registered: SessionService`). Turned GREEN after confirming zero forbidden vault registrations.
  - `BlindMobileAssembly_ContainsNoForbiddenCoreStorageCryptoTypeReferences`: Saw RED when temporary `SessionService? ForbiddenTestField` was added to `BlindMobileServices.cs` (`Unallowed type reference from 'BeeMemoryBank.Core': BeeMemoryBank.Core.Services.SessionService`). Turned GREEN when all references strictly matched the allow-list.

## 5. Numbers
- **BlindMobile build check**: `dotnet build mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android`: **0 errors**.
- **BlindMobile Release APK publish**: `dotnet publish mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android -c Release`: **0 errors**.
  - Generated APK: `mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android/publish/com.beememorybank.blind-Signed.apk`
  - APK size: **43,188,264 bytes (41.19 MB)**.
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.
- **Shared libraries diffstat against 46e88776**:
  - `libs/`: **0 files changed**.
  - `server/`: **0 files changed**.
  - `desktop/`: **0 files changed**.
  - `tests/BeeMemoryBank.Core.Tests`: **0 files changed**.
  - `tests/BeeMemoryBank.Integration.Tests`: **0 files changed**.
- **Output of `git diff --name-status 46e88776 HEAD`**:
```
M	.github/workflows/build-mobile.yml
M	BeeMemoryBank.slnx
A	PLAN.md
A	STAGE-0.md
A	STAGE-1.md
A	STAGE-2.md
A	mobile/BeeMemoryBank.BlindMobile/App.xaml
A	mobile/BeeMemoryBank.BlindMobile/App.xaml.cs
A	mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj
A	mobile/BeeMemoryBank.BlindMobile/MauiProgram.cs
A	mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml
A	mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/AndroidDeviceState.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/AndroidManifest.xml
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/BlindWork.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/BootReceiver.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/MainActivity.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/MainApplication.cs
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/Resources/drawable/ic_notification.xml
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/Resources/xml/data_extraction_rules.xml
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/Resources/xml/network_security_config.xml
A	mobile/BeeMemoryBank.BlindMobile/Platforms/Android/SafExport.cs
A	mobile/BeeMemoryBank.BlindMobile/Resources/AppIcon/appicon.svg
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpClientProvider.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpHandler.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindIdentityRecord.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobileServices.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindPhoneReset.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/IBlindNodeKeys.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/PendingBlindPieces.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/PreferencesBlindStore.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs
A	mobile/BeeMemoryBank.BlindMobile/Services/MaintenanceDetectingHandler.cs
A	tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj
A	tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs
A	tests/BeeMemoryBank.BlindMobile.Tests/ForbiddenReferencesTests.cs
```

## 6. Needs the orchestrator
- None. No deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- Android 14+/15 background network timing for WorkManager sync in Stage 3.
