# STAGE 31 Report: Carry-Over Fixes & Sync-Embeddings Split

## 1. Done
- **Item 1: Carry-over fixes from Stage 2 review (commit `bad3c0df`)**:
  - **Cleared key buffers on all paths including Keystore exception (`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs:190-235`)**:
    - Removed raw-return `PhoneCode()` API that exposed live secret byte arrays to callers.
    - Updated `WithPhoneCode<TResult>(Func<BlindPhoneCode, TResult> consume)` so that both `keys.LoadPairingSecret()` and `keys.LoadBackupKey()` occur inside an outer `try/finally` block.
    - If `LoadBackupKey()` throws an exception (e.g. Android Keystore decryption failure), the `finally` block runs immediately and clears the previously loaded pairing secret buffer using `CryptographicOperations.ZeroMemory`.
    - Added overload `WithPhoneCode(Action<BlindPhoneCode> consume)` and retained `PhoneCodeText()` which wipes all buffers immediately upon formatting.
    - Added spy test in `tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:174-219`: `WithPhoneCode_WhenSecondLoadThrows_ClearsFirstBufferImmediately`.
  - **Rejected control characters in `displayName` (`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs:60-63`)**:
    - Added `displayName.Any(char.IsControl)` check in `CreateIdentityAsync`, matching `BlindPhoneCode.TryParse` in `libs/BeeMemoryBank.Core/Models/BlindPhoneCodes.cs:47`.
    - Throws `ArgumentException` when control characters are detected, preventing generation of codes the Windows/hub side would reject.
    - Added theory test in `tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs:266-279`: `CreateIdentityAsync_RejectsControlCharactersInDisplayName` covering `\n`, `\r`, `\t`, `\0`.

- **Item 2: Decoupled `BeeMemoryBank.Sync` from `BeeMemoryBank.Embeddings` (Option A)**:
  - **Moved `PendingEmbeddingProcessor.cs`**:
    - Executed single approved move `git mv libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs libs/BeeMemoryBank.Embeddings/`.
    - Updated namespace to `BeeMemoryBank.Embeddings;` (`libs/BeeMemoryBank.Embeddings/PendingEmbeddingProcessor.cs:8`).
  - **Removed Embeddings reference from Sync**:
    - Removed `<ProjectReference Include="..\BeeMemoryBank.Embeddings\BeeMemoryBank.Embeddings.csproj" />` from `libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj`.
    - Removed `using BeeMemoryBank.Embeddings;`, `services.AddEmbeddingServices();`, and `AddEmbeddingProcessor()` from `libs/BeeMemoryBank.Sync/DependencyInjection.cs`.
  - **Added extension methods in Embeddings**:
    - Added `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />` to `libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj:17`.
    - Added `AddPendingEmbeddingProcessor(TimeSpan? interval = null, int? batchSize = null)` and backwards-compatible alias `AddEmbeddingProcessor` to `libs/BeeMemoryBank.Embeddings/DependencyInjection.cs:74-91`.
  - **Updated host consumers & callers**:
    - `server/BeeMemoryBank.Api/Startup/ApiServices.cs:81`: Updated `AddEmbeddingProcessor(...)` call to `builder.Services.AddPendingEmbeddingProcessor(interval: embeddingInterval, batchSize: embeddingBatchSize)`.
    - `server/BeeMemoryBank.Api/Endpoints/SearchMetricsEndpoints.cs:5`: Added `using BeeMemoryBank.Embeddings;` where `PendingEmbeddingProcessor` is directly injected for manual draining.
    - `tests/BeeMemoryBank.Sync.Tests/BeeMemoryBank.Sync.Tests.csproj:11`: Added `ProjectReference` to `BeeMemoryBank.Embeddings.csproj` so existing processor tests continue to run and compile.
  - **Host analysis**:
    - `server/BeeMemoryBank.Api`: Explicitly registers `PendingEmbeddingProcessor` via `AddPendingEmbeddingProcessor()` in `ApiServices.cs:81`.
    - `desktop/BeeMemoryBank.Node`: Server background host that runs Api/Web. Does not register embedding services independently. Built cleanly (0 errors).
    - `desktop/BeeMemoryBank.Desktop`: Tray app. Does not consume sync embedding processor. Built cleanly (0 errors).
    - `tests/BeeMemoryBank.Sync.Tests`: Test host for sync. References Embeddings directly for processor tests; all 403 tests pass.
    - `mobile/BeeMemoryBank.Mobile`: Ordinary mobile app does not register `PendingEmbeddingProcessor` (it is not a background server). It references `BeeMemoryBank.Embeddings` directly in its csproj. In accordance with orchestrator instructions, it was left 100% byte-identical to `46e88776`.
    - `mobile/BeeMemoryBank.BlindMobile`: Does not register `PendingEmbeddingProcessor` and does not reference `BeeMemoryBank.Embeddings` (forbidden reference tests pass).

## 2. Not done
- None. Both Item 1 and Item 2 are fully completed.

## 3. Deviations
- None. Followed Option A and exact orchestrator instructions.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: **Passed: 49, Failed: 0, Skipped: 0** (Duration: 2 s)
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: **Passed: 4, Failed: 0, Skipped: 0** (Duration: 67 ms)
- `dotnet test tests/BeeMemoryBank.Sync.Tests`: **Passed: 403, Failed: 0, Skipped: 0** (Duration: 1 m 57 s)
- `dotnet test tests/BeeMemoryBank.Core.Tests --filter "FullyQualifiedName~Embedding|FullyQualifiedName~SemanticSearch|FullyQualifiedName~Onnx"`: **Passed: 16, Failed: 0, Skipped: 0** (Duration: 1 s)
- `dotnet test tests/BeeMemoryBank.Integration.Tests --filter "FullyQualifiedName~Embedding|FullyQualifiedName~PendingEmbedding|FullyQualifiedName~Search"`: **Passed: 21, Failed: 0, Skipped: 0** (Duration: 21 s)
- **How new tests were seen RED first**:
  - `WithPhoneCode_WhenSecondLoadThrows_ClearsFirstBufferImmediately`: Saw RED with `Expected keys.SecretBuffersClearedCount to be 1 because first buffer must be cleared when second load throws, but found 0`. Turned GREEN after wrapping secret and backup key loads in `try/finally`.
  - `CreateIdentityAsync_RejectsControlCharactersInDisplayName`: Saw RED across `\n`, `\r`, `\t`, `\0` with `Expected a <System.ArgumentException> to be thrown, but no exception was thrown`. Turned GREEN after adding `displayName.Any(char.IsControl)` check.
  - `SyncAssembly_DoesNotReference_EmbeddingsAssembly`: Inspects `BeeMemoryBank.Sync.dll` using `System.Reflection.Metadata` and `PEReader`. Saw RED first with `Expected referencedNames to not contain "BeeMemoryBank.Embeddings", but found {"BeeMemoryBank.Embeddings"}`. Turned GREEN after decoupling the csproj reference.
  - `HostBuiltLikeServer_RegistersPendingEmbeddingProcessor_AsHostedService`: Verified `AddPendingEmbeddingProcessor()` registers `PendingEmbeddingProcessor` as an `IHostedService` on a `ServiceCollection`. Passed GREEN.

## 5. Numbers
- **BlindMobile build check**: `dotnet build mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android`: **0 errors**.
- **BlindMobile Release APK publish**: `dotnet publish mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android -c Release`: **0 errors**.
  - Generated APK: `mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android/publish/com.beememorybank.blind-Signed.apk`
  - APK size: **42,771,874 bytes (~40.79 MB)**.
- **Ordinary app byte-identity check**: `git diff 46e88776 --stat mobile/BeeMemoryBank.Mobile`: **0 files changed, 0 insertions, 0 deletions** (byte-identical).
- **Ordinary app build check**: `dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`: **0 errors** (Duration: 55 s).
- **Api build check**: `dotnet build server/BeeMemoryBank.Api`: **0 errors**.
- **Desktop build check**: `dotnet build desktop/BeeMemoryBank.Desktop/BeeMemoryBank.Desktop.csproj`: **0 errors**.
- **Node build check**: `dotnet build desktop/BeeMemoryBank.Node/BeeMemoryBank.Node.csproj`: **0 errors**.
- **Output of `git diff --name-status 46e88776 HEAD`**:
```
M	.github/workflows/build-mobile.yml
M	BeeMemoryBank.slnx
A	PLAN.md
A	STAGE-0.md
A	STAGE-1.md
A	STAGE-2.md
A	STAGE-31.md
M	libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj
M	libs/BeeMemoryBank.Embeddings/DependencyInjection.cs
R099	libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs	libs/BeeMemoryBank.Embeddings/PendingEmbeddingProcessor.cs
M	libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj
M	libs/BeeMemoryBank.Sync/DependencyInjection.cs
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
M	server/BeeMemoryBank.Api/Endpoints/SearchMetricsEndpoints.cs
M	server/BeeMemoryBank.Api/Startup/ApiServices.cs
A	tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj
A	tests/BeeMemoryBank.BlindMobile.Tests/BlindMobileLogicTests.cs
A	tests/BeeMemoryBank.BlindMobile.Tests/ForbiddenReferencesTests.cs
M	tests/BeeMemoryBank.Sync.Tests/BeeMemoryBank.Sync.Tests.csproj
A	tests/BeeMemoryBank.Sync.Tests/SyncEmbeddingSplitTests.cs
```

## 6. Needs the orchestrator
- **Ordinary mobile app (`mobile/BeeMemoryBank.Mobile`)**: Per orchestrator instruction ("If the ORDINARY mobile app is affected, do NOT change it: write it under 'Needs the orchestrator'"), `mobile/BeeMemoryBank.Mobile` was NOT changed in this stage and remains 100% byte-identical to `46e88776`. `mobile/BeeMemoryBank.Mobile.csproj` directly references `BeeMemoryBank.Embeddings.csproj` already (as well as `BeeMemoryBank.Sync.csproj`), and does not register `PendingEmbeddingProcessor`. Its compilation succeeds cleanly (`dotnet build mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android`, 0 errors).
- **Linux CI verification**: Linux verification is performed by the orchestrator.

## 7. Open risks
- None. Decoupling of `BeeMemoryBank.Sync` from `BeeMemoryBank.Embeddings` is complete and verified test-first, leaving `BeeMemoryBank.Sync` ready to be referenced by `BeeMemoryBank.BlindMobile` in Stage 3 without transitive ONNX/Embeddings dependencies.
