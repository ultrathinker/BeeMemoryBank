# STAGE 1 Report: Android Blind Node App Skeleton

## 1. Done
- **Created `mobile/BeeMemoryBank.BlindMobile` project** ([`mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj:1-48`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj#L1-L48)):
  - Target: `net10.0-android`, ApplicationId `com.beememorybank.blind`, DisplayName `"Bee Memory Bank Blind"`.
  - References ONLY `Core`, `Crypto`, and `Storage` per Decision 1 (Option B). Completely free of forbidden libraries (`Embeddings`, `Media`, `Sync`, `Markdig`, `Markdown`, `SixLabors.ImageSharp`, `Microsoft.ML.OnnxRuntime`).
- **Single-screen UI host (no Shell)**:
  - Adapted `App.xaml.cs` ([`mobile/BeeMemoryBank.BlindMobile/App.xaml.cs:8-41`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/App.xaml.cs#L8-L41)) setting `MainPage = BlindHomePage`, running database migrations, initializing `BlindWorkScheduler.Ensure`.
  - `BlindHomePage.xaml` and `BlindHomePage.xaml.cs` ([`mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs:1-188`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs#L1-L188)) host the 5 stand-ins, displaying pending notices without throwing unhandled exceptions.
- **Background services, Keystore keys & Network security**:
  - `KeystoreBlindPhoneKeys.cs` ([`mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs:11-34`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs#L11-L34)) encapsulates identity seed under alias `bmb_blind_seed_v1`.
  - App-wide network policy explicitly denies cleartext: `cleartextTrafficPermitted="false"` ([`mobile/BeeMemoryBank.BlindMobile/Platforms/Android/Resources/xml/network_security_config.xml:3-4`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/Resources/xml/network_security_config.xml#L3-L4)).
  - Added `BlindHttpHandler.cs` ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpHandler.cs:1-90`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpHandler.cs#L1-L90)) enforcing HTTPS in client path, refusing redirects (`AllowAutoRedirect = false` and 3xx status rejection), and strictly validating TLS certificate against paired listening node SPKI pin (mandatory pinning, no CA fallback). Wired to default `HttpClient` in `MauiProgram.cs` ([`mobile/BeeMemoryBank.BlindMobile/MauiProgram.cs:40-50`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/MauiProgram.cs#L40-L50)).
  - Fixed `BlindPhoneReset.cs` ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindPhoneReset.cs:14-44`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindPhoneReset.cs#L14-L44)) to take an injected `dataDir` parameter (defaulting to `LocalApplicationData`), preventing accidental wipe of host/dev environment.
  - Updated `PreferencesBlindStore.cs` ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/PreferencesBlindStore.cs:5-35`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/PreferencesBlindStore.cs#L5-L35)) to support testable delegates while retaining MAUI `Preferences.Default` on device.
- **Unit test project `tests/BeeMemoryBank.BlindMobile.Tests`** ([`tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj:1-35`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj#L1-L35)):
  - Links and tests `PendingBlindPieces`, `BlindPhoneReset`, `PreferencesBlindStore`, `BlindHttpHandler`, and `MaintenanceDetectingHandler`.
  - Full suite of 22 tests passing ([`tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:1-358`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L1-L358)).
- **CI workflow & Solution updates**:
  - `build-blind` job in `.github/workflows/build-mobile.yml` ([`.github/workflows/build-mobile.yml:97-151`](file:///D:/review/bmb-android-blind/repo/.github/workflows/build-mobile.yml#L97-L151)) without ONNX download and with automated unzip inspection of APK.
  - `BeeMemoryBank.slnx` updated with both projects.

## 2. Not done
- None. All Stage 1 deliverables, Review Round 1 fixes, and Review Round 2 fixes are complete.

## 3. Deviations
- **Review Round 1 Findings Verification & Actions**:
  - **Finding 1 (Critical - BlindPhoneReset wipe path)**: Verified. Accepted. `BlindPhoneReset.WipeAndRestart` and `Wipe` now accept an optional `dataDir` parameter (`BlindPhoneReset.cs:14`). Unit tests pass a dedicated, test-owned directory (`BlindMobileLogicTests.cs:113`) ensuring developer/CI `%LOCALAPPDATA%` data is never touched.
  - **Finding 2 (Important - Network policy, HTTPS requirement, SPKI pinning, redirect refusal)**: Verified. Accepted. Denied cleartext in `network_security_config.xml:4`. Implemented `BlindHttpHandler` with `AllowAutoRedirect = false`, client-path HTTPS enforcement, 3xx redirect refusal, and SPKI public-key pinning against `BlindPhoneState.CallCode.SpkiPin` / request `ExplicitPin` (`BlindHttpHandler.cs:16-83`). Registered in `MauiProgram.cs:40-49`. Verified test-first with unit tests for HTTP rejection, redirect rejection, and SPKI matching.
  - **Finding 3 (Important - Pending identity skeleton documentation and test)**: Verified. Accepted. Replaced bare task completion test with `PendingBlindIdentityRecorder_DocumentsSkeletonBehavior_CompletesWithoutDatabaseWrites` (`BlindMobileLogicTests.cs:21-42`), documenting deliberate skeleton-stage placeholder behavior that enables in-memory pairing while noting that Stage 2 must introduce a failing-without-the-change test asserting the SQLite `tbl_node_identity` row (`ed25519_private_key_v = 2`).
  - **Finding 4 (Minor - Test project links and copied behavior tests)**: Verified. Accepted. Linked `PreferencesBlindStore.cs` and `MaintenanceDetectingHandler.cs` (plus `BlindHttpHandler.cs`) in `BeeMemoryBank.BlindMobile.Tests.csproj:15-17`. Added isolated unit tests for preference round-trips/removal and 503 maintenance message rewrites.
- **Review Round 2 Findings Verification & Actions**:
  - **Finding (Important - TLS pinning fallback to standard CA validation)** ([`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpHandler.cs:82`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindHttpHandler.cs#L82)):
    - **Verified**: Verified against code. In `BlindHttpHandler.ValidateServerCertificate`, when neither request options nor `BlindPhoneState.CallCode` supplied a pin, the callback fell back to `return errors == SslPolicyErrors.None;`, accepting any certificate that passed system CA validation. Because `MauiProgram.cs:45-46` installs this callback app-wide, unpinned requests (such as prior to pairing or when pairing state is cleared) could reach a CA-valid host without checking the listening node's SPKI pin.
    - **Action**: Accepted. Removed the CA fallback. `ValidateServerCertificate` now strictly requires a non-empty pin (from request options or `state.CallCode`) and returns `false` if no pin is available or if the pin does not match the server certificate's SPKI hash.
    - **Test-First**: Added `BlindHttpHandler_ValidateServerCertificate_RejectsWhenNoPinAvailable` ([`tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:302-317`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L302-L317)) asserting that lack of a pin returns `false` even if `SslPolicyErrors.None`. Verified test was RED (`Expected result to be False, but found True`), then updated `BlindHttpHandler.cs` to return `false` making the test GREEN. Retained and extended matching/mismatching pin tests covering explicit pin and `BlindCallCode` state pin ([`tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:319-357`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L319-L357)).
- Followed Orchestrator Decisions 1-3: Option B dependency boundary (`Sync` excluded), `bmb_blind_seed_v1` Keystore blob, and single-screen host without `AppShell`.

## 4. Tests
- Exact command: `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`
  - Result: **Passed: 22, Failed: 0, Skipped: 0, Total: 22** (Duration: 205 ms).
- Exact command: `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`
  - Result: **Passed: 68, Failed: 0, Skipped: 0, Total: 68** (Duration: 2 s).
- Exact command: `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~ForbiddenReferencesTests"`
  - Result: **Passed: 2, Failed: 0, Skipped: 0, Total: 2** (Duration: 30 ms).
- **How tests were seen RED first**:
  - `BlindPhoneReset_Wipe`: Test calling `BlindPhoneReset.Wipe(provider, tempDir)` failed compilation CS0117 before `Wipe` with `dataDir` was added.
  - `BlindHttpHandler`: Tests for HTTP rejection, redirect rejection, and SPKI pinning failed compilation CS2001 before `BlindHttpHandler.cs` was linked and created.
  - `BlindHttpHandler_ValidateServerCertificate_RejectsWhenNoPinAvailable`: Failed with `Expected result to be False, but found True` before removing the CA fallback from `BlindHttpHandler.ValidateServerCertificate`.
  - `PreferencesBlindStore`: Test calling delegate constructor failed compilation CS1729 before `PreferencesBlindStore.cs` was updated with the testable constructor.
  - `ForbiddenReferencesTests`: Verified RED (Failed: 2) when temporary `BeeMemoryBank.Sync` reference was added, and GREEN (Passed: 2) when removed.

## 5. Numbers
- **Release APK built**: `mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android/com.beememorybank.blind-Signed.apk`.
- **Blind APK size**: **42,829,427 bytes (40.85 MB)**.
- **Ordinary app APK size**: **254,441,712 bytes (242.65 MB)** (`D:\Backups\bmb-release-a\apk\bmb-1.0.12-Signed.apk`).
- **APK size reduction**: **83.2% reduction (211.61 MB smaller)**.
- **Forbidden assets in APK**: **0** out of 1140 entries (verified via `System.IO.Compression.ZipFile` for `model.onnx`, `OnnxRuntime`, `Tokenizers`, `Embeddings`, `Media`, `Sync`, `ImageSharp`, `Markdig`, `Indiko.Maui`).
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.

## 6. Needs the orchestrator
- Orchestrator decision on `BeeMemoryBank.Sync` decoupling before Stage 3 (Option A split vs. dedicated blind sync client).
- No file deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- Physical device TEE execution is required for end-to-end verification of AndroidKeyStore non-exportable hardware keys in Stage 2.
- Storage Access Framework UI picker interaction cannot be headlessly driven in CI without dedicated device test infrastructure.
