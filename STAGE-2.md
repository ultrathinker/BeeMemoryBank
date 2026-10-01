# STAGE 2 Report: Identity and Pairing

## 1. Done
- **Codex Stage 2 Round 1 review fixes**:
  - **Disposable HttpClient wrapper per factory call** ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpClientProvider.cs:33-85`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpClientProvider.cs#L33-L85)):
    - Addressed Finding 1: replaced returning cached `HttpClient` with returning fresh `HttpClient` wrappers containing `NonDisposingDelegatingHandler`.
    - Disposing client instances (such as by `SyncScheduler.cs:192`) safely disposes caller wrapper without tearing down the provider's underlying `HttpClientHandler` connection pool.
  - **Crash-consistent identity initialization & recovery** ([`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs:40-108`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs#L40-L108)):
    - Addressed Finding 2: `CreateIdentityAsync` checks `identity.GetRecordedAsync()`. If a row already exists in SQLite, it loads `keys.LoadIdentitySeed()` and checks derivation with `Ed25519Signer.GetPublicKeyFromSeed`.
    - If Keystore seed matches the database row, it reloads `NodeId`, `PublicKey`, and `DisplayName` without overwriting keys or failing.
    - If database row is orphaned without matching Keystore key, safely rolls back via `identity.ClearAsync()` and `keys.Clear()` before retrying.
  - **Atomic single-identity database enforcement & serialized initialization** ([`libs/BeeMemoryBank.Storage/Sqlite/NodeIdentityRepository.cs:28-59`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Storage/Sqlite/NodeIdentityRepository.cs#L28-L59), [`mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs:18-63`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs#L18-L63)):
    - Addressed Finding 3: `NodeIdentityRepository.CreateAsync` uses `INSERT INTO tbl_node_identity ... SELECT ... WHERE NOT EXISTS (SELECT 1 FROM tbl_node_identity)` making single-identity invariant atomic at SQLite statement level. Idempotent for same `node_id`, throws `InvalidOperationException` for conflicting rows.
    - Serialized in-process identity creation via `SemaphoreSlim` in `SqliteBlindIdentityRecorder` and `BlindPhonePairing`.
  - **Durable, replay-safe call code acceptance with lock** ([`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs:144-245`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs#L144-L245)):
    - Addressed Finding 4: `AcceptCallCode` synchronized via `_pairGate` lock.
    - Persists `state.CallCode = code` before clearing `keys.ClearPairingSecret()`. If Preferences write fails, pairing secret is preserved for retry.
    - If crash occurs after state write before secret clear, `CleanupOrphanedSecretIfCommitted` verifies active call code authenticity and spends the leftover secret safely.
    - Replay resistance preserved: re-submitting spent code returns "already paired".
  - **Comprehensive regression tests added** ([`tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:521-850`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L521-L850)):
    - Addressed Finding 5: Added tests for disposable client factory reuse, atomic single identity enforcement, crash recovery between DB and state, and concurrent call code acceptance with crash recovery.
- **Carry-over Codex review fix: HTTP connection pool isolation per SPKI pin** (`326e627d`).
- **Real v=2 SQLite identity persistence** (`99ceb349`).
- **Android Keystore keys under alias `bmb_blind_seed_v1`** (`bmb-blind-identity-seed-v1`).
- **Two-code pairing with mandatory SPKI pin & fake listener tests** (`ec705ce4`).
- **Windows/Hub responsibilities for the phone code (plan §10 & §3.5)**:
  - Phone presents `BlindPhoneCode` (`bmb-blind-phone:?n=<id>&k=<pubkey>&s=<secret>&b=<backupKey>&d=<name>`).
  - Windows/hub performs standing check (`GET /api/sync/my-standing`) to ensure sender is recognized as superadmin.
  - Emits `whitelist_add` event recording the phone as a regular peer (`is_superadmin = false`), never superadmin.
  - Seals `backupKey` under master DEK via `SealedSecretService.PublishAsync` under name `android-backup:<phoneId>`, bundled with `BlindPhoneBackupSeal` signed by the pairing node (`PairedBy`).
  - Issues `BlindCallCode` (`bmb-blind-call:?a=<address>&n=<listenerId>&s=<spkiPin>&k=<pubkey>&m=<mac>`) carrying listening node coordinates and HMAC-SHA256 authenticated with phone's one-time `secret`.

## 2. Not done
- None. All Stage 2 deliverables and review fixes complete.

## 3. Deviations
- None rejected. All 4 important findings and 1 minor finding from Codex Stage 2 Round 1 review were verified against code, accepted as genuine correctness/security concerns, and resolved test-first.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: **Passed: 30, Failed: 0, Skipped: 0** (Duration: 1 s)
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: **Passed: 2, Failed: 0, Skipped: 0** (Duration: 29 ms)
- `dotnet test tests/BeeMemoryBank.Integration.Tests --filter "FullyQualifiedName~BlindPhonePairing"`: **Passed: 16, Failed: 0, Skipped: 0** (Duration: 15 s)
- `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`: **Passed: 68, Failed: 0, Skipped: 0** (Duration: 2 s)
- **How new tests were seen RED first**:
  - `BlindHttpClientProvider_DisposingOneClient_AllowsSubsequentClientToSucceed`: Saw RED with `System.ObjectDisposedException: Cannot access a disposed object. Object name: 'System.Net.Http.HttpClient'`. Turned GREEN with `NonDisposingDelegatingHandler`.
  - `NodeIdentityRepository_CreateAsync_EnforcesSingleIdentityInvariantAtomically`: Saw RED with `Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'UNIQUE constraint failed: tbl_node_identity.node_id'`. Turned GREEN after atomic `WHERE NOT EXISTS` insert and single-identity check.
  - `CreateIdentityAsync_WhenCrashBetweenDbAndState_RecoversMatchingIdentity`: Saw RED with `System.InvalidOperationException: Node already has identity ..., cannot overwrite with ...`. Turned GREEN with matching seed identity recovery.
  - `AcceptCallCode_WhenInterruptedOrRetried_RecoversDurableConnectionAndSpendsSecretAtomically`: Saw RED with `Expected results.Count(r => r == null) to be 1, but found 0`. Turned GREEN with `_pairGate` synchronization and durable commit protocol.

## 5. Numbers
- **Release APK built**: `mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android/com.beememorybank.blind-Signed.apk`.
- **Blind APK size**: **75,384,303 bytes (71.89 MB)**.
- **Ordinary app APK size**: **254,441,712 bytes (242.65 MB)** (`D:\Backups\bmb-release-a\apk\bmb-1.0.12-Signed.apk`).
- **APK size reduction**: **70.4% reduction (179.06 MB smaller)**.
- **Forbidden assets in APK**: **0** out of 1362 zip entries.
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.

## 6. Needs the orchestrator
- Orchestrator decision on `BeeMemoryBank.Sync` decoupling before Stage 3 (Option A split vs. dedicated blind sync client).
- Physical device with Android Keystore TEE hardware backing for end-to-end device verification.
- No deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- WorkManager sync execution in Stage 3 requires background network capabilities on Android 14+ / 15 battery optimization regimes.
