# STAGE 2 Report: Identity and Pairing

## 1. Done
- **Codex Stage 2 Round 2 review fixes**:
  - **Fail closed on missing/mismatched Keystore seed** ([`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs:54-106`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs#L54-L106)):
    - Addressed Finding 1: `CreateIdentityAsync` throws `InvalidOperationException` when `tbl_node_identity` exists in SQLite but Keystore seed is null or mismatched.
    - Preserves existing database row and Keystore keys; never destroys old node identity or replica pairing material on startup. Disconnect & wipe required to re-join.
  - **Enforce blind invariants on recovery & fail closed** ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs:21-61`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs#L21-L61), [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs:58-69`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs#L58-L69)):
    - Addressed Finding 2: `GetRecordedAsync` reads full row (`node_id`, `ed25519_private_key`, `ed25519_private_key_iv`, `ed25519_private_key_v`).
    - Validates `BlindNodeId.IsBlind(node_id)`, `ed25519_private_key_v == 2`, and empty private key/IV. Fails closed with `InvalidOperationException` on non-blind or v0/v1 rows without clearing them.
  - **Preserve ordinary app ingest keys on initialization** ([`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs:107-133`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhonePairing.cs#L107-L133)):
    - Addressed Finding 3: Removed `keys.Clear()` from initialization path in `CreateIdentityAsync`. In the ordinary app (`mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreBlindPhoneKeys.cs:30`), `keys.Clear()` calls `ingest.Clear()`; removing this side effect prevents erasing `bmb_ingest.bin` signing keys.
  - **Restored shared libraries & isolated storage to mobile** ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs:63-125`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs#L63-L125)):
    - Addressed Finding 4: Reverted all changes to `libs/BeeMemoryBank.Storage/Sqlite/NodeIdentityRepository.cs`, `libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs`, and `libs/BeeMemoryBank.Crypto/Ed25519Signer.cs`.
    - SQLite operations (reading full row, invariant checks, atomic `WHERE NOT EXISTS` insert) are contained in `SqliteBlindIdentityRecorder.cs` under `mobile/BeeMemoryBank.BlindMobile/`.
- **Stage 2 Core Deliverables & Round 1 fixes**:
  - Disposable HttpClient wrapper per factory call (`BlindHttpClientProvider.cs:33-85`).
  - Durable, replay-safe call code acceptance under lock in `BlindPhonePairing.cs:144-245`.
  - HTTP connection pool isolation per SPKI pin (`326e627d`).
  - Real v=2 SQLite identity persistence (`SqliteBlindIdentityRecorder.cs`).
  - Android Keystore keys under alias `bmb_blind_seed_v1` (`KeystoreBlindPhoneKeys.cs`).
  - Two-code pairing with mandatory SPKI pin & fake listener tests (`ec705ce4`).
  - Windows/hub responsibilities documented for phone code (plan §10 & §3.5).

## 2. Not done
- None. All Stage 2 deliverables and review findings resolved.

## 3. Deviations
- **Review Finding 4 (shared file ownership)**:
  - Fully reverted edits to shared library files `libs/BeeMemoryBank.Storage/Sqlite/NodeIdentityRepository.cs`, `libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs`, and `libs/BeeMemoryBank.Crypto/Ed25519Signer.cs`, restoring them byte-identical to base `326e627d`.
  - All blind node database operations are moved into `mobile/BeeMemoryBank.BlindMobile/Services/Blind/SqliteBlindIdentityRecorder.cs` (Opus-R2/mobile owned).
  - The remaining phone-side contracts and pairing logic live in `libs/BeeMemoryBank.Core/Services/BlindPhone/` (`BlindPhoneContracts.cs`, `BlindPhonePairing.cs`) per BRIEF.md §2 line 69 and §3 line 87 ("Put logic in plain class libraries so it is testable without a phone"). Requesting orchestrator ownership exception for these phone-specific classes.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: **Passed: 36, Failed: 0, Skipped: 0** (Duration: 1 s)
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: **Passed: 2, Failed: 0, Skipped: 0** (Duration: 29 ms)
- `dotnet test tests/BeeMemoryBank.Integration.Tests --filter "FullyQualifiedName~BlindPhonePairing"`: **Passed: 16, Failed: 0, Skipped: 0** (Duration: 17 s)
- `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`: **Passed: 68, Failed: 0, Skipped: 0** (Duration: 2 s)
- **How new tests were seen RED first**:
  - `CreateIdentityAsync_WhenMissingSeed_FailsClosed_AndPreservesRowAndKeys`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown` (previous code deleted DB row and created new identity). Turned GREEN after failing closed and preserving row/keys.
  - `CreateIdentityAsync_WhenMismatchedSeed_FailsClosed_AndPreservesRowAndKeys`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown`. Turned GREEN after failing closed and preserving row/keys.
  - `SqliteBlindIdentityRecorder_GetRecordedAsync_WhenNonBlindNodeId_FailsClosed`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown`. Turned GREEN after enforcing `BlindNodeId.IsBlind`.
  - `SqliteBlindIdentityRecorder_GetRecordedAsync_WhenV1PrivateKey_FailsClosed`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown`. Turned GREEN after enforcing `ed25519_private_key_v == 2`.
  - `SqliteBlindIdentityRecorder_GetRecordedAsync_WhenNonEmptyPrivateKey_FailsClosed`: Saw RED with `Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown`. Turned GREEN after enforcing empty private key.
  - `CreateIdentityAsync_DoesNotClearKeys_OnNewRegistration_PreservingOrdinaryIngestKey`: Saw RED with `Expected mockKeys.ClearCalled to be False because Clear() must not be called as a side effect of creating identity, but found True`. Turned GREEN after removing `keys.Clear()` from registration path.

## 5. Numbers
- **BlindMobile build check**: `dotnet build mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android`: **0 errors**.
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.
- **Shared libraries diffstat against 326e627d**:
  - `libs/BeeMemoryBank.Storage`: **0 files changed**.
  - `libs/BeeMemoryBank.Crypto`: **0 files changed**.
  - `libs/BeeMemoryBank.Core/Interfaces`: **0 files changed**.

## 6. Needs the orchestrator
- Formal ownership exception for `libs/BeeMemoryBank.Core/Services/BlindPhone/` files (`BlindPhoneContracts.cs`, `BlindPhonePairing.cs`) placed in Core per BRIEF.md §2/§3 for non-phone testability.
- Decisions on `BeeMemoryBank.Sync` decoupling for Stage 3 (Option A split vs. dedicated blind sync client).
- No deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- WorkManager sync execution in Stage 3 requires background network capabilities on Android 14+ / 15 battery optimization regimes.
