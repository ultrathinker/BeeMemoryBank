# STAGE 2 Report: Identity and Pairing

## 1. Done
- **Restored shared library and test files to base `46e88776`** (`3982efef`):
  - Reverted `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs`, `libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs`, `tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`, and `tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs` byte-identical to base `46e88776`.
- **Isolated blind pairing and keys in BlindMobile** (`122a8e52`):
  - [`BlindMobilePairing.cs:1-271`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs#L1-L271): Dedicated blind node pairing coordinator with two-code pairing, durable acceptance protocol, atomic one-time secret consumption, crash cleanup, and Keystore seed validation.
  - [`KeystoreBlindPhoneKeys.cs:1-34`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs#L1-L34): Hardware-backed Keystore storage under alias `bmb_blind_seed_v1`, isolated from ordinary app.
- **Enforced `can_generate_embeddings = 0` invariant & fail closed** (`122a8e52`):
  - [`SqliteBlindIdentityRecorder.cs:25-56`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs#L25-L56): Reads column, throws unless 0.
- **Core Stage 2 Deliverables**: Real v=2 SQLite identity, SPKI pin HTTP pool isolation, two-code pairing with mandatory SPKI pin.
- **Fix round 4** (review `D:\review\bmb-agents\review\android-blind-s2-r1-54018\REVIEW.md`):
  - **(CRITICAL) Backup key recovery fail-closed** — [`BlindMobilePairing.cs:88-97`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs#L88-L97): When seed matches but backup key is missing, throws `InvalidOperationException` ("disconnect and re-pair") instead of silently generating a new key. Prevents creating backups unreadable by the Windows recovery path.
  - **(IMPORTANT) Ordinary-ingest isolation test made real** — [`BlindMobileLogicTests.cs:1472-1527`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L1472-L1527): New DI-composition test registers both `IBlindNodeKeys` (blind spy) and `IBlindPhoneKeys` (ordinary spy), builds `BlindMobilePairing` via DI, asserts blind spy receives all calls and ordinary spy receives zero. Proven RED by swapping registrations.
  - **(MINOR) PhoneCode() buffer clearing** — [`BlindMobilePairing.cs:175-180`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs#L175-L180): On incomplete-code path (one of secret/backupKey is null), both loaded buffers are now `ZeroMemory`-cleared before returning null.

## 2. Not done
- None.

## 3. Deviations
- **Review Findings 2 and 4 rejected (diff artifacts, not real):**
  - Finding 2 says the candidate "reverts durable pairing hardening" in shared `BlindPhonePairing.cs`. Finding 4 says the candidate "removes the existing end-to-end test" in `BlindPhonePairingTests.cs`. Both are artifacts of Codex diffing only the last round against a mid-stage snapshot where those files were temporarily modified. The files are byte-identical to base `46e88776`:
  ```
  git diff --name-status 46e88776 HEAD -- libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs
  ```
  produces **empty output** — zero changes. The shared files were restored in `3982efef` and have not been touched since.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --blame-hang-timeout 30s`: **Passed: 40, Failed: 0, Skipped: 0** (Duration: 1 s)
- **New tests (fix round 4):**
  - `CreateIdentityAsync_WhenSeedPresentButBackupKeyMissing_FailsClosed_DoesNotGenerateNewBackupKey`: RED — `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.` GREEN after adding fail-closed check at `BlindMobilePairing.cs:88-97`.
  - `CreateIdentityAsync_DIComposition_BlindPairingCannotReachOrdinaryIngestStore`: RED when DI registrations swapped (`Expected blindKeys.SaveIdentitySeedCallCount to be greater than 0 ... but found 0`). GREEN when correctly registered. Replaces the disconnected `mockOrdinaryIngest` test.
  - `PhoneCode_WhenBackupKeyMissing_ClearsLoadedPairingSecretBeforeReturning`: RED — `Expected keys.SecretBuffersClearedCount to be greater than 0 ... but found 0.` GREEN after adding `CryptographicOperations.ZeroMemory` on both buffers at `BlindMobilePairing.cs:175-180`.

## 5. Numbers
- **Existing app byte-identity**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed**.
- **Shared files**: `git diff --name-status 46e88776 HEAD -- libs/ server/ desktop/ tests/BeeMemoryBank.Core.Tests tests/BeeMemoryBank.Integration.Tests`: **empty** (zero changes).

## 6. Needs the orchestrator
- None.

## 7. Open risks
- Android 14+/15 background network timing for WorkManager sync in Stage 3.
