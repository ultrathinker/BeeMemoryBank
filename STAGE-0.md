# STAGE 0 Report: Android Blind Node App Planning

## 1. Done
- **Analyzed all standing briefs and contracts**:
  - Read and strictly adhered to [`D:\review\bmb-android-blind\BRIEF.md`](file:///D:/review/bmb-android-blind/BRIEF.md) (binding section 0 rules: no deletions, no renames, no cache cleaning, no device installs, verify claims with `file:line`).
  - Read [`D:\review\bmb-blind\plan.md`](file:///D:/review/bmb-blind/plan.md) section 10 (Android blind node) and related architectural sections (§3.5, §4.4, §5.5, §6.8).
  - Read [`D:\review\bmb-agents\CONTRACTS.md`](file:///D:/review/bmb-agents/CONTRACTS.md) for shared contracts, package formats, and subsystem ownership boundaries.
- **Project layout design (Section a)**:
  - Specified new project `mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj` (`com.beememorybank.blind`, display title `"Bee Memory Bank Blind"`).
  - Audited all files in [`mobile/BeeMemoryBank.Mobile`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile) and categorized each as COPIED, ADAPTED, or NOT COPIED.
  - Excluded all article/tree/tag/search/unlock UI pages, note controls (`SecretEntry`), mode selection (`ModeChoicePage`, `DeviceModeStore`), biometrics, full sync foreground service, and Markdown parser/controls.
- **Transitive library dependency audit (Section b)**:
  - Audited `csproj` files for all potential dependencies: `Core`, `Crypto`, `Storage`, `Search`, `Sync`, `Embeddings`, `Media`.
  - Discovered critical transitive dependency: [`libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj:16`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj#L16) references `BeeMemoryBank.Embeddings`, which pulls in `Microsoft.ML.OnnxRuntime` and `Microsoft.ML.Tokenizers` via [`libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj:22-23`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj#L22-L23).
  - Traced exact cause: solely [`libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs:4,92`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs#L4-L92) and DI registration in [`libs/BeeMemoryBank.Sync/DependencyInjection.cs:150`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/DependencyInjection.cs#L150). Neither `SyncClient` nor `EventApplier` uses `BeeMemoryBank.Embeddings` directly.
  - Proposed smallest split (Option A: move `PendingEmbeddingProcessor.cs` to `BeeMemoryBank.Embeddings`, cost: 1 file, ~10 lines) and staged isolation strategy (Option B: `BlindMobile` references only `Core`, `Crypto`, `Storage` in Stages 1 and 2).
- **The five stand-ins real design & mapping (Section c)**:
  - Mapped all 5 stand-ins in [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:9-43`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L9-L43):
    1. `PendingBlindIdentityRecorder`: writes `tbl_node_identity` with `ed25519_private_key_v = 2` via `INodeIdentityRepository` ([`libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs:5`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs#L5), [`libs/BeeMemoryBank.Crypto/NodeIdentityCrypto.cs:18`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Crypto/NodeIdentityCrypto.cs#L18)).
    2. `PendingBlindReplicaSource`: downloads `bmb-blind-package.tar.gz` from `GET /api/blind/replica` ([`server/BeeMemoryBank.Api/Endpoints/BlindEndpoints.cs:84-115`](file:///D:/review/bmb-android-blind/repo/server/BeeMemoryBank.Api/Endpoints/BlindEndpoints.cs#L84-L115)) with SPKI pinning, verifies signature against `BlindCallCode.PublicKey`, and installs snapshot atomically.
    3. `PendingBlindPhoneSync`: protocol 3 pull sync against server with v=2 Keystore key signer (`KeystoreNodeAuthSigner`), applying events via `EventApplier` with `BlindEmbeddingGenerator` ([`libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs:13`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs#L13)).
    4. `PendingBlindPackageSource`: streams cached blind replica package plus incremental events to create backup body (`BlindPhoneBackupBody`).
    5. `PendingRecoverySetSource`: queries local SQLite tables to build `bmb-recovery-set-v1` via `RecoverySetBuilder` ([`libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs:93`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs#L93)).
- **Test strategy (Section d)**:
  - Structured unit tests into plain `net10.0` test project `tests/BeeMemoryBank.BlindMobile.Tests`.
  - Identified existing baseline coverage (68 Core tests + 15 Integration tests).
  - Listed new tests per stage and documented device-only constraints (TEE AndroidKeyStore, WorkManager battery optimizations, SAF document picker).
- **CI pipeline design (Section e)**:
  - Formulated dedicated `build-blind` job in `.github/workflows/build-mobile.yml` without ONNX model download, including an automated APK unzip inspection to verify absence of `model.onnx`, `OnnxRuntime`, `Embeddings`, `Media`, and `Markdown`.
- **Risks & stage roadmap (Section f)**:
  - Formulated 4 numbered risks/questions for the orchestrator and defined adjusted stage roadmap for Stages 1-4.
- **Created `PLAN.md`**:
  - Saved to [`D:\review\bmb-android-blind\PLAN.md`](file:///D:/review/bmb-android-blind/PLAN.md) and [`D:\review\bmb-android-blind\repo\PLAN.md`](file:///D:/review/bmb-android-blind/repo/PLAN.md).

## 2. Not done
- None. Stage 0 is strictly a planning stage with no product code; all deliverables required by Section 4 Stage 0 are complete.

## 3. Deviations
- None.

## 4. Tests
- Stage 0 adds no product code and no new test code.
- Baseline test runs executed to verify current repository health:
  1. `dotnet test tests\BeeMemoryBank.Core.Tests\BeeMemoryBank.Core.Tests.csproj --filter "FullyQualifiedName~BlindPhone"`:
     - Result: **Passed: 68, Failed: 0, Skipped: 0, Total: 68** (Duration: 3 s).
  2. `dotnet test tests\BeeMemoryBank.Integration.Tests\BeeMemoryBank.Integration.Tests.csproj --filter "FullyQualifiedName~BlindPhone"`:
     - Result: **Passed: 15, Failed: 0, Skipped: 0, Total: 15** (Duration: 15 s).

## 5. Numbers
- Number of files analyzed in `mobile/BeeMemoryBank.Mobile`: 40 files (~8.6k lines of code).
- Files targeted for copying/adapting to `mobile/BeeMemoryBank.BlindMobile`: 14 files.
- Files excluded as forbidden/unneeded: 26 files (12 view pages, 3 note/folder controls, full sync services, biometrics, mode choice).
- Baseline tests verified: 83 tests (68 Core + 15 Integration).
- Diffstat of product code in `BeeMemoryBank.slnx`: 0 lines changed (clean baseline).

## 6. Needs the orchestrator
1. **Decision on `BeeMemoryBank.Sync` -> `BeeMemoryBank.Embeddings` transitive reference**:
   - `libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj` currently references `libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj` solely to support `PendingEmbeddingProcessor.cs`.
   - Option A: Orchestrator moves `PendingEmbeddingProcessor.cs` to `BeeMemoryBank.Embeddings` (or `Api`), freeing `BeeMemoryBank.Sync` from `Embeddings`.
   - Option B: `mobile/BeeMemoryBank.BlindMobile` references only `Core`, `Crypto`, and `Storage` in Stages 1 and 2, and uses a dedicated lightweight blind sync coordinator for Stage 3.
2. **Approval of adjusted stage roadmap** (Stages 1 through 4).

## 7. Open risks
1. **Transitive ONNX dependency**: If `BeeMemoryBank.Sync` is referenced directly without decoupling `PendingEmbeddingProcessor`, ONNX runtime binaries will bloat the blind APK and violate the brief.
2. **SAF headless testing**: Storage Access Framework requires user UI interaction with the Android system document picker; automated CI cannot drive SAF on headless runners. Verification is done via headless `AndroidBackupFile` and `AndroidBackupRestore` crypto tests.
3. **Hardware Keystore availability in CI**: AndroidKeyStore with non-exportable hardware-backed keys requires Android TEE or StrongBox, which is verified on real devices by the orchestrator.
