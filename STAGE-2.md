# STAGE 2 Report: Identity and Pairing

## 1. Done
- **Restored shared library and test files to base `46e88776`** (`3982efef`):
  - Addressed Review Findings 1, 2, 4 and Orchestrator guidance.
  - Reverted `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs`, `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs`, `tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`, and `tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs` byte-identical to base `46e88776`.
  - Existing shared members preserved with original behavior; no shared API removed or altered.
- **Isolated blind pairing and keys in BlindMobile** (`122a8e52`):
  - [`IBlindNodeKeys.cs:1-12`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/IBlindNodeKeys.cs#L1-L12): Extends `IBlindPhoneKeys` with `LoadIdentitySeed()` for isolated seed retrieval.
  - [`BlindIdentityRecord.cs:1-12`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindIdentityRecord.cs#L1-L12): Local immutable record for SQLite row snapshot with `CanGenerateEmbeddings` tracking.
  - [`BlindMobilePairing.cs:1-255`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs#L1-L255): Dedicated blind node pairing coordinator with two-code pairing, durable acceptance protocol, atomic one-time secret consumption, crash cleanup, and Keystore seed validation.
  - [`KeystoreBlindPhoneKeys.cs:1-34`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs#L1-L34): Hardware-backed Keystore storage implementing `IBlindNodeKeys` under alias `bmb_blind_seed_v1`. Completely isolated from ordinary app ingest store.
- **Enforced `can_generate_embeddings = 0` invariant & fail closed** (`122a8e52`):
  - Addressed Review Finding 3 & Orchestrator note 4.
  - [`SqliteBlindIdentityRecorder.cs:25-56`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs#L25-L56): Reads `can_generate_embeddings` column and throws `InvalidOperationException` unless it is 0.
- **Isolated ingest key verification** (`122a8e52`):
  - Addressed Review Finding 2 & Orchestrator note 3.
  - Regression test `CreateIdentityAsync_UsesIsolatedBlindKeyStore_NeverCallsOrdinaryIngestStore_PreservesOrdinaryKeyByteIdentical` verifies 0 calls to ordinary app ingest store and byte-identical key preservation.
- **Core Stage 2 Deliverables**:
  - Real v=2 SQLite identity persistence (`SqliteBlindIdentityRecorder.cs`).
  - SPKI pin HTTP connection pool isolation (`BlindHttpClientProvider.cs:33-85`).
  - Two-code pairing with mandatory SPKI pin (`ec705ce4`).

## 2. Not done
- None. All Stage 2 deliverables and review findings resolved.

## 3. Deviations
- **Review Findings 1, 2, and 4 (Shared files restored & isolated)**:
  - Findings 1, 2, and 4 are resolved by restoring all four shared files (`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs`, `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs`, `tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`, `tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs`) to their original state in `46e88776`.
  - No shared files in `libs/` or `server/` or `tests/` are modified. All blind-specific logic resides strictly in `mobile/BeeMemoryBank.BlindMobile/` and `tests/BeeMemoryBank.BlindMobile.Tests/`.
  - Output of `git diff --name-status 46e88776 HEAD`:
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

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: **Passed: 37, Failed: 0, Skipped: 0** (Duration: 1 s)
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: **Passed: 2, Failed: 0, Skipped: 0** (Duration: 32 ms)
- `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`: **Passed: 68, Failed: 0, Skipped: 0** (Duration: 2 s)
- `dotnet test tests/BeeMemoryBank.Integration.Tests --filter "FullyQualifiedName~BlindPhonePairing"`: **Passed: 15, Failed: 0, Skipped: 0** (Duration: 17 s)
- **How new tests were seen RED first**:
  - `SqliteBlindIdentityRecorder_GetRecordedAsync_WhenCanGenerateEmbeddingsTrue_FailsClosed`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown` (row with `can_generate_embeddings = 1` was previously accepted without check). Turned GREEN after reading column and failing closed in `SqliteBlindIdentityRecorder.cs`.
  - `CreateIdentityAsync_UsesIsolatedBlindKeyStore_NeverCallsOrdinaryIngestStore_PreservesOrdinaryKeyByteIdentical`: Verified that blind app pairing uses isolated `IBlindNodeKeys` and never invokes ordinary app ingest store methods (`mockOrdinaryIngest.EnrollCallCount == 0`, `mockOrdinaryIngest.ClearCallCount == 0`, and key remains byte-identical).

## 5. Numbers
- **BlindMobile build check**: `dotnet build mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android`: **0 errors**.
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.
- **Shared libraries diffstat against 46e88776**:
  - `libs/`: **0 files changed**.
  - `server/`: **0 files changed**.
  - `desktop/`: **0 files changed**.
  - `tests/BeeMemoryBank.Core.Tests`: **0 files changed**.
  - `tests/BeeMemoryBank.Integration.Tests`: **0 files changed**.

## 6. Needs the orchestrator
- None. No deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- Android 14+/15 background network timing for WorkManager sync in Stage 3.
