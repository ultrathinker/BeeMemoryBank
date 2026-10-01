# STAGE 1 Report: Android Blind Node App Skeleton

## 1. Done
- **Created `mobile/BeeMemoryBank.BlindMobile` project** ([`2e7c4ae3`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj)):
  - Target: `net10.0-android`, ApplicationId `com.beememorybank.blind`, DisplayName `"Bee Memory Bank Blind"` ([`mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj:1-48`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj#L1-L48)).
  - References ONLY `Core`, `Crypto`, and `Storage` per Orchestrator Decision 1 (Option B). Completely free of `Embeddings`, `Media`, `Sync`, `Markdig`, `Markdown`, `SixLabors.ImageSharp`, and `Microsoft.ML.OnnxRuntime`.
- **Single-screen UI host (no Shell)**:
  - Adapted `App.xaml` and `App.xaml.cs` ([`mobile/BeeMemoryBank.BlindMobile/App.xaml.cs:8-41`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/App.xaml.cs#L8-L41)) setting `MainPage = BlindHomePage`, running database migrations, and initializing `BlindWorkScheduler.Ensure`.
  - Copied `BlindHomePage.xaml` and `BlindHomePage.xaml.cs` ([`mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml:1-82`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml#L1-L82), [`mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs:1-188`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Pages/BlindHomePage.xaml.cs#L1-L188)). The 5 stand-ins remain in place, displaying pending notices gracefully without throwing unhandled exceptions.
- **Background services & Keystore keys**:
  - Copied `BlindWork.cs` ([`mobile/BeeMemoryBank.BlindMobile/Platforms/Android/BlindWork.cs:1-197`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/BlindWork.cs#L1-L197)) with `BlindWorkScheduler`, `BlindSyncWorker`, `BlindHeavyWorker`, `BlindBackupService`, and `BlindNotifications` (stripped `DeviceModeStore.IsBlind` checks).
  - Implemented self-contained `KeystoreBlindPhoneKeys` ([`mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs:11-34`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/KeystoreBlindPhoneKeys.cs#L11-L34)) encapsulating the identity seed under alias `bmb_blind_seed_v1` per Orchestrator Decision 2, removing dependency on `IIngestKeyStore`.
  - Copied `PreferencesBlindStore`, `PendingBlindPieces`, `BlindPhoneReset` (without `DeviceModeStore.Clear`), `MaintenanceDetectingHandler`, `SafExport`, `AndroidDeviceState`, `MainActivity`, `MainApplication`, `BootReceiver`, and `AndroidManifest.xml` ([`mobile/BeeMemoryBank.BlindMobile/Platforms/Android/AndroidManifest.xml:1-49`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.BlindMobile/Platforms/Android/AndroidManifest.xml#L1-L49)).
- **Unit test project `tests/BeeMemoryBank.BlindMobile.Tests`** ([`e699d09c`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj)):
  - Created plain `net10.0` test project with `ForbiddenReferencesTests` inspecting `project.assets.json` and csproj ([`tests/BeeMemoryBank.BlindMobile.Tests/ForbiddenReferencesTests.cs:29-106`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/ForbiddenReferencesTests.cs#L29-L106)).
  - Verified RED first, then GREEN on clean project graph.
  - Added unit tests for `BlindPaths`, all 5 stand-ins (`PendingBlindPieces`), and phone wipe logic (`BlindPhoneReset`) ([`tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:10-136`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs#L10-L136)).
- **CI workflow & Solution updates** ([`62c06a04`](file:///D:/review/bmb-android-blind/repo/.github/workflows/build-mobile.yml)):
  - Added `build-blind` job to `.github/workflows/build-mobile.yml` ([`.github/workflows/build-mobile.yml:97-151`](file:///D:/review/bmb-android-blind/repo/.github/workflows/build-mobile.yml#L97-L151)) without ONNX download and with automated unzip inspection of the APK.
  - Added `BlindMobile` and `BlindMobile.Tests` to `BeeMemoryBank.slnx` ([`BeeMemoryBank.slnx:28,33`](file:///D:/review/bmb-android-blind/repo/BeeMemoryBank.slnx#L28)).

## 2. Not done
- None. All Stage 1 deliverables are complete.

## 3. Deviations
- Followed Orchestrator Decision 1 (Option B): `BeeMemoryBank.BlindMobile` references only `Core`, `Crypto`, and `Storage` in Stages 1 and 2; `BeeMemoryBank.Sync` is excluded entirely.
- Followed Orchestrator Decision 2: `KeystoreBlindPhoneKeys` stores the seed directly in `bmb_blind_seed_v1` via `KeystoreBlob`.
- Followed Orchestrator Decision 3: Single-page host without `AppShell`.

## 4. Tests
- Exact command: `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`
  - Result: **Passed: 9, Failed: 0, Skipped: 0, Total: 9** (Duration: 40 ms).
- Exact command: `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~BlindPhone"`
  - Result: **Passed: 68, Failed: 0, Skipped: 0, Total: 68** (Duration: 3 s).
- **How forbidden references test was seen RED first**:
  1. Temporarily added `<ProjectReference Include="..\..\libs\BeeMemoryBank.Sync\BeeMemoryBank.Sync.csproj" />` to `mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj`.
  2. Executed `dotnet restore mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj`.
  3. Executed `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~ForbiddenReferencesTests"`:
     - Result: **Failed: 2, Passed: 0**.
     - Error: `Target 'net10.0-android' contains forbidden reference 'Microsoft.ML.OnnxRuntime/1.29.0'; Target 'net10.0-android' contains forbidden reference 'Microsoft.ML.Tokenizers/2.0.0'; Target 'net10.0-android' contains forbidden reference 'BeeMemoryBank.Embeddings/1.0.14'; Target 'net10.0-android' contains forbidden reference 'BeeMemoryBank.Sync/1.0.14'`.
  4. Removed forbidden reference by file edit (no file deleted), restored, and verified GREEN (**Passed: 2, Failed: 0**).

## 5. Numbers
- **Release APK built**: `mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android/com.beememorybank.blind-Signed.apk`.
- **Blind APK size**: **43,208,953 bytes (41.21 MB)**.
- **Ordinary app APK size**: **254,441,712 bytes (242.65 MB)** (`D:\Backups\bmb-release-a\apk\bmb-1.0.12-Signed.apk`).
- **APK size reduction**: **83.0% reduction (211.23 MB smaller)** due to exclusion of ONNX runtime, model, Markdown, Media, and UI pages.
- **Forbidden assets in APK**: **0** out of 1636 entries (verified via `System.IO.Compression.ZipFile` inspection for `model.onnx`, `OnnxRuntime`, `Tokenizers`, `Embeddings`, `Media`, `Sync`, `ImageSharp`, `Markdig`, `Indiko.Maui`).
- **Existing app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Existing app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors**.

## 6. Needs the orchestrator
- Orchestrator decision on `BeeMemoryBank.Sync` decoupling before Stage 3 (Option A split vs. dedicated blind sync client).
- No file deletions, renames, cache clears, or device installs were executed.

## 7. Open risks
- AndroidKeyStore non-exportable hardware-backed key operations require physical device TEE execution for end-to-end verification during Stage 2.
- Storage Access Framework UI picker interaction cannot be headlessly driven in CI without dedicated device test infrastructure.
