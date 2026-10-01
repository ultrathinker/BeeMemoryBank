# Plan: Android Blind Node App (`mobile/BeeMemoryBank.BlindMobile`)

## Summary & Context

This plan outlines the design and implementation roadmap for turning the Android blind node into an independent, dedicated mobile application (`mobile/BeeMemoryBank.BlindMobile`).
The application is built strictly from existing code and class libraries, completely excluding all components unnecessary for a blind node (no note editing, no markdown rendering, no tree/article/search navigation, no embeddings/ONNX runtime, no media viewing, no master password or DEK handling, and no mode choice).

References:
- Master design: [`D:\review\bmb-blind\plan.md`](file:///D:/review/bmb-blind/plan.md) section 10 (and sections 3.5, 4.4, 5.5, 6.8).
- Team contracts: [`D:\review\bmb-agents\CONTRACTS.md`](file:///D:/review/bmb-agents/CONTRACTS.md).
- Standing brief: [`D:\review\bmb-android-blind\BRIEF.md`](file:///D:/review/bmb-android-blind/BRIEF.md).

---

## a. Project Layout

### 1. New Project Identity
- **Project file**: `mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj`
- **ApplicationId**: `com.beememorybank.blind`
- **ApplicationTitle / DisplayName**: `Bee Memory Bank Blind`
- **Target Framework**: `net10.0-android`
- **OutputType**: `Exe`
- **UseMaui**: `true`
- **SupportedOSPlatformVersion**: `29`
- **RootNamespace**: `BeeMemoryBank.BlindMobile`

### 2. Files Copied vs. Not Copied from `mobile/BeeMemoryBank.Mobile`

| Path in Existing App | Status in Blind App | Rationale & Changes (`file:line` references) |
|---|---|---|
| `App.xaml` / `App.xaml.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/App.xaml.cs:8-193`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/App.xaml.cs#L8-L193). Stripped of vault migration runner, folder bootstrapper, session lock listeners, `MediaBlobBackfillService`, and `//mode`, `//setup`, `//unlock` routing. Sets `MainPage` directly to `BlindHomePage` (or minimal navigation host) and starts WorkManager via `BlindWorkScheduler.Ensure`. |
| `AppShell.xaml` / `AppShell.xaml.cs` | **NOT COPIED** | Existing shell in [`mobile/BeeMemoryBank.Mobile/AppShell.xaml:1-88`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/AppShell.xaml#L1-L88) defines flyout items for Articles, Tags, Tree, Security, Status. The blind node has only one screen, making the flyout menu and tabs completely redundant. |
| `Pages/BlindHomePage.xaml` / `.cs` | **COPIED** | Copied from [`mobile/BeeMemoryBank.Mobile/Pages/BlindHomePage.xaml:1-83`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Pages/BlindHomePage.xaml#L1-L83) and [`Pages/BlindHomePage.xaml.cs:1-182`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Pages/BlindHomePage.xaml.cs#L1-L182). This is the only user-facing screen: shows node state, pairing codes & QR, backup triggers/history, log, and "Disconnect and wipe". |
| `Pages/ModeChoicePage.xaml` / `.cs` | **NOT COPIED** | Existing mode choice in [`mobile/BeeMemoryBank.Mobile/Pages/ModeChoicePage.xaml.cs:1-49`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Pages/ModeChoicePage.xaml.cs#L1-L49) switches between Full and Blind. The separate blind app has no mode choice. |
| `Pages/UnlockPage.xaml(.cs)` | **NOT COPIED** | Vault unlock screen. Blind nodes never hold the master password. |
| `Pages/SetupPage.xaml(.cs)` | **NOT COPIED** | First-run setup for full nodes (password creation / QR join). |
| `Pages/InitialSyncPage.xaml(.cs)` | **NOT COPIED** | Post-join initial sync screen for full nodes. |
| `Pages/StatusPage.xaml(.cs)` | **NOT COPIED** | Vault sync/status screen for full nodes. |
| `Pages/ArticlesPage.xaml(.cs)` | **NOT COPIED** | Article list for full nodes. |
| `Pages/ArticleDetailPage.xaml(.cs)` | **NOT COPIED** | Article viewing. |
| `Pages/ArticleEditPage.xaml(.cs)` | **NOT COPIED** | Article editing. |
| `Pages/TagsPage.xaml(.cs)` | **NOT COPIED** | Tag list. |
| `Pages/TagArticlesPage.xaml(.cs)` | **NOT COPIED** | Filtered article list. |
| `Pages/TreePage.xaml(.cs)` | **NOT COPIED** | Folder tree view. |
| `Pages/TreeFolderPage.xaml(.cs)` | **NOT COPIED** | Folder detail view. |
| `Pages/TreeHelpers.cs`, `TreeNode.cs` | **NOT COPIED** | Folder tree model and helper classes. |
| `Pages/FolderPickerPage.xaml(.cs)` | **NOT COPIED** | Folder picker dialog. |
| `Pages/SecurityPage.xaml(.cs)` | **NOT COPIED** | Security & key settings for full nodes. |
| `Controls/SecretEntry.cs` | **NOT COPIED** | Password entry masking for full nodes. |
| `Controls/InputPopup.xaml(.cs)` | **NOT COPIED** | Dialog popups for creating folders/notes. |
| `Controls/PopupExtensions.cs` | **NOT COPIED** | Extension methods for `InputPopup`. |
| `Controls/SyncIndicatorView.xaml(.cs)`| **NOT COPIED** | Header sync indicator for full app shell. |
| `Platforms/Android/BlindWork.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/BlindWork.cs:1-197`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/BlindWork.cs#L1-L197). Contains `BlindWorkScheduler`, `BlindSyncWorker`, `BlindHeavyWorker`, `BlindBackupService`, and `BlindNotifications`. Stripped checks against `DeviceModeStore.IsBlind` (the entire app is blind). |
| `Platforms/Android/KeystoreBlindPhoneKeys.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreBlindPhoneKeys.cs:1-104`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreBlindPhoneKeys.cs#L1-L104). Wraps pairing secret, backup key, and identity seed in hardware-backed AndroidKeyStore AES-256-GCM keys. Can encapsulate identity seed directly in a `KeystoreBlob` to drop dependency on `IIngestKeyStore`. |
| `Platforms/Android/KeystoreNodeAuthSigner.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreNodeAuthSigner.cs:1-62`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreNodeAuthSigner.cs#L1-L62). Signs challenges using the unwrapped Ed25519 seed from AndroidKeyStore. Stripped fallback to `session.GetMasterDek` and `SessionService`. |
| `Platforms/Android/SafExport.cs` | **COPIED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/SafExport.cs:1-41`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/SafExport.cs#L1-L41). Handles Storage Access Framework (`Intent.ActionCreateDocument`) to allow the user to save backup files to external storage. |
| `Platforms/Android/AndroidDeviceState.cs` | **COPIED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/AndroidDeviceState.cs:1-32`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/AndroidDeviceState.cs#L1-L32). Queries `ConnectivityManager` and sticky battery intent to verify Wi-Fi, charging, and battery >= 20%. |
| `Platforms/Android/MainActivity.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/MainActivity.cs:1-199`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/MainActivity.cs#L1-L199). Retains notification permission request and `OnActivityResult` -> `SafExport.OnResult`. Stripped `HandleShareIntent` (text/plain send intent), auto-unlock, and auto-init. |
| `Platforms/Android/MainApplication.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/MainApplication.cs:1-14`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/MainApplication.cs#L1-L14). Bootstraps `MauiProgram.CreateMauiApp()`. |
| `Platforms/Android/BootReceiver.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/BootReceiver.cs:1-33`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/BootReceiver.cs#L1-L33). Ensures `BlindWorkScheduler.Ensure` on device reboot. Stripped `SyncForegroundService` start. |
| `Platforms/Android/AndroidManifest.xml` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Platforms/Android/AndroidManifest.xml:1-52`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/AndroidManifest.xml#L1-L52). Package name set to `com.beememorybank.blind`, label `Bee Memory Bank Blind`. Declares `SystemForegroundService` and `BlindBackupService`. Stripped biometric permissions and `SyncForegroundService`. |
| `Platforms/Android/Resources/xml/*` | **COPIED** | `network_security_config.xml` (TLS configuration) and `data_extraction_rules.xml` (disabling cloud backup). |
| `Platforms/Android/Resources/drawable/*` | **COPIED** | `ic_notification.xml` (notification icon for foreground service). |
| `Platforms/Android/BiometricService.cs` | **NOT COPIED** | Biometric prompt service for unlocking full vault. |
| `Platforms/Android/Permissions.cs` | **NOT COPIED** | Android runtime permissions helper for biometrics. |
| `Platforms/Android/SyncForegroundService.cs` | **NOT COPIED** | Continuous sync service for full nodes. |
| `Platforms/Android/SyncWorker.cs` | **NOT COPIED** | WorkManager sync worker for full nodes. |
| `Platforms/Android/SyncWorkScheduler.cs` | **NOT COPIED** | WorkManager scheduler for full nodes. |
| `Platforms/Android/IngestKeyStore.cs` | **NOT COPIED** | Legacy ingest key wrapper; subsumed by clean `KeystoreBlob` in `KeystoreBlindPhoneKeys`. |
| `Services/Blind/PreferencesBlindStore.cs` | **COPIED** | Copied from [`mobile/BeeMemoryBank.Mobile/Services/Blind/PreferencesBlindStore.cs:1-16`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PreferencesBlindStore.cs#L1-L16). Implements `IBlindPhoneStore` via MAUI Preferences. |
| `Services/Blind/BlindPhoneReset.cs` | **COPIED & ADAPTED** | Copied from [`mobile/BeeMemoryBank.Mobile/Services/Blind/BlindPhoneReset.cs:1-54`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/BlindPhoneReset.cs#L1-L54). Stops WorkManager and backup service, wipes Keystore keys, clears preferences, deletes database and replica files, and restarts the app. |
| `Services/Blind/DeviceModeStore.cs` | **NOT COPIED** | Mode store for dual-mode app. Blind app is always blind. |
| `Services/Blind/PendingBlindPieces.cs` | **COPIED (temporarily)** | Copied from [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:1-44`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L1-L44) for Stage 1 skeleton, to be replaced progressively in Stages 2-4. |
| `Services/MaintenanceDetectingHandler.cs` | **COPIED** | Copied from [`mobile/BeeMemoryBank.Mobile/Services/MaintenanceDetectingHandler.cs:1-33`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/MaintenanceDetectingHandler.cs#L1-L33). Formats HTTP 503 maintenance mode responses. |
| `Services/IBiometricService.cs` | **NOT COPIED** | Biometric contract. |
| `Services/IIngestKeyStore.cs` | **NOT COPIED** | Ingest key contract. |
| `Services/IngestKeyEnroller.cs` | **NOT COPIED** | Ingest key enrollment on unlock. |
| `Services/MobileUnlockHolder.cs` | **NOT COPIED** | In-memory session key holder. |
| `Services/NodeSetupService.cs` | **NOT COPIED** | Full node initialization and `/api/join`. |
| `Services/PostUnlockRouter.cs` | **NOT COPIED** | Routing logic after vault unlock. |
| `Services/ShareIntentHandler.cs` | **NOT COPIED** | Handler for text sharing into articles. |
| `Services/SyncHeartbeat.cs` | **NOT COPIED** | Sync heartbeat for full nodes. |
| `Services/SyncNotificationService.cs`| **NOT COPIED** | Full node sync status notifications. |
| `Services/SyncStatusService.cs` | **NOT COPIED** | Full node sync state tracking. |
| `Resources/AppIcon/appicon.svg` | **COPIED** | Application launcher icon. |
| `Resources/Images/*.svg` | **NOT COPIED** | Note/tree/search icons: `icon_article.svg`, `icon_articles.svg`, `icon_compact.svg`, `icon_expand.svg`, `icon_folder.svg`, `icon_menu.svg`, `icon_new_article.svg`, `icon_new_folder.svg`, `icon_search.svg`, `icon_security.svg`, `icon_status.svg`, `icon_tag.svg`, `icon_tags.svg`, `icon_tree.svg`. None are used on `BlindHomePage`. |
| `Resources/Raw/model.onnx` | **NOT COPIED** | Multilingual E5 ONNX model (~87 MB). Forbidden. |

---

## b. Referenced Libraries & Transitive Dependency Audit

### 1. Proposed Project & Package References Table

| Dependency | Type | Target / Reason | Transitive Audit Result |
|---|---|---|---|
| `BeeMemoryBank.Core` | ProjectReference | **ALLOWED & REQUIRED**. Provides `BeeMemoryBank.Core.Services.BlindPhone` (`BlindPhonePairing`, `BlindPhoneEnrollment`, `BlindPhoneState`, `BlindPhoneBackupRunner`, `BlindHeavyWork`, `BlindPhoneLog`, `AndroidBackupRestore`, `BlindPhoneContracts`), domain models (`BlindNodeId`, `BlindPhoneCodes`, `NodeIdentity`, `WhitelistEntry`), interfaces (`INodeIdentityRepository`, `IWhitelistRepository`, `IEventLogRepository`). | References `Crypto` and `Search`. Packages: `DiffPlex`, `Microsoft.Extensions.*`. **CLEAN**. Does NOT reference `Embeddings` or `Media`. |
| `BeeMemoryBank.Crypto` | ProjectReference | **ALLOWED & REQUIRED**. Provides `AndroidBackupFile` (chunked AES-256-GCM format with open preamble), `AndroidBackupWriter`, `Ed25519Signer`, `DekFingerprint`, `MediaEncryptor`. | Packages: `Konscious.Security.Cryptography.Argon2`, `BouncyCastle.Cryptography`. **CLEAN**. No project references. |
| `BeeMemoryBank.Storage` | ProjectReference | **ALLOWED & REQUIRED** (from Stage 2). Provides SQLite connection factory (`DbConnectionFactory`), migrations (`MigrationRunner`), and repositories for `tbl_node_identity`, `tbl_whitelist`, `tbl_event`, `tbl_recovery_box`, `tbl_dek_retired_link`, `tbl_state_anchor`, `tbl_sealed_secret`. | References `Core` and `Search`. Packages: `Dapper`, `Microsoft.Data.Sqlite`, `System.Numerics.Tensors`, `SQLitePCLRaw.lib.e_sqlite3`. **CLEAN**. Explicitly contains no reference to `Embeddings` ([`libs/BeeMemoryBank.Storage/BeeMemoryBank.Storage.csproj:9-11`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Storage/BeeMemoryBank.Storage.csproj#L9-L11)). |
| `BeeMemoryBank.Search` | (Transitive via Core/Storage) | **ALLOWED (Transitive only)**. Provides `System.IO.Hashing` (used by inverted index and storage segment headers). | Direct package: `System.IO.Hashing`. **CLEAN**. No project references. |
| `BeeMemoryBank.Sync` | ProjectReference | **CRITICAL FINDING: FORBIDDEN IN CURRENT STATE** (see detailed analysis below). | **CONTAINS FORBIDDEN REFERENCE**: `libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj:16` directly references `libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj`! |
| `BeeMemoryBank.Embeddings`| ProjectReference | **FORBIDDEN**. Must NOT be referenced directly or transitively. | Contains `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, `sentencepiece.bpe.model`. |
| `BeeMemoryBank.Media` | ProjectReference | **FORBIDDEN**. Must NOT be referenced. Image transcoding and camera roll upload logic are not used on blind nodes. | References `ImageSharp`. |
| `Indiko.Maui.Controls.Markdown` | PackageReference | **FORBIDDEN**. Must NOT be referenced. Markdown rendering control. | Third-party MAUI UI control. |
| `Markdig` | PackageReference | **FORBIDDEN**. Must NOT be referenced. Markdown parser. | Parsing library. |
| `QRCoder` | PackageReference | **ALLOWED & REQUIRED**. Renders the phone's pairing code as a QR code on `BlindHomePage` ([`mobile/BeeMemoryBank.Mobile/Pages/BlindHomePage.xaml.cs:96-98`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Pages/BlindHomePage.xaml.cs#L96-L98)). | Pure managed QR generator. |
| `Xamarin.AndroidX.Work.Runtime` | PackageReference | **ALLOWED & REQUIRED**. Android WorkManager runtime for background sync and heavy backup scheduling. | AndroidX library. |
| `Xamarin.AndroidX.Core` & `Xamarin.AndroidX.Core.Core.Ktx` | PackageReference | **ALLOWED & REQUIRED**. Pinned AndroidX core libraries. | AndroidX library. |
| `SQLitePCLRaw.lib.e_sqlite3` | PackageReference | **ALLOWED & REQUIRED** (with `ExcludeAssets="native;runtime"`). Standard fix from [`BeeMemoryBank.Mobile.csproj:64`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj#L64) preventing RID fallback from packaging GLIBC Linux binaries on Android Bionic. | Pure build mitigation. |

---

### 2. Transitive Dependency Audit & The `BeeMemoryBank.Sync` Split Analysis

#### Finding
An inspection of [`libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj:16`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj#L16) reveals:
```xml
<ProjectReference Include="..\BeeMemoryBank.Embeddings\BeeMemoryBank.Embeddings.csproj" />
```
And [`libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj:22-31`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Embeddings/BeeMemoryBank.Embeddings.csproj#L22-L31) pulls in:
- `Microsoft.ML.OnnxRuntime`
- `Microsoft.ML.Tokenizers`
- Embedded resource `Models\sentencepiece.bpe.model`

If `mobile/BeeMemoryBank.BlindMobile` references `BeeMemoryBank.Sync`, NuGet and the .NET SDK will transitively include the ONNX runtime and ML tokenizers in the Blind APK packaging, directly violating Section 1 and Section 4b of `BRIEF.md`.

#### Why does `BeeMemoryBank.Sync` reference `BeeMemoryBank.Embeddings`?
In `BeeMemoryBank.Sync`, only a single class references `BeeMemoryBank.Embeddings`:
1. [`libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs:4,92`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs#L4-L92): Resolves `EmbeddingProjectionService` from DI to generate embeddings in the background on full nodes where `can_generate_embeddings = true`.
2. [`libs/BeeMemoryBank.Sync/DependencyInjection.cs:4,150`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/DependencyInjection.cs#L4-L150): Registers `PendingEmbeddingProcessor` in `services.AddHostedService(...)`.

Crucially, **neither `EventApplier` nor `SyncClient` directly uses `BeeMemoryBank.Embeddings`**:
- `EventApplier` takes `IEmbeddingGenerator` from [`libs/BeeMemoryBank.Core/Interfaces/IEmbeddingGenerator.cs`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Interfaces/IEmbeddingGenerator.cs), and on a blind node this is satisfied by `BlindEmbeddingGenerator` ([`libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs:13-21`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs#L13-L21)), which throws `ModelUnavailableException` and needs no ONNX.
- `RecoverySetBuilder` ([`libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs:93-136`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs#L93-L136)) only uses `IDbConnectionFactory`, Dapper, and System.Text.Json.

#### Proposed Smallest Split & Cost
To allow the blind mobile node (and any other lightweight consumer) to use sync client primitives without dragging in the ONNX runtime:

- **Option A (Architectural Split — Recommended for Orchestrator)**:
  Extract `PendingEmbeddingProcessor.cs` out of `libs/BeeMemoryBank.Sync` into `libs/BeeMemoryBank.Embeddings` (or `server/BeeMemoryBank.Api/Services/`).
  - **Action**:
    1. Move `PendingEmbeddingProcessor.cs` to `libs/BeeMemoryBank.Embeddings/`.
    2. Remove `<ProjectReference Include="..\BeeMemoryBank.Embeddings\BeeMemoryBank.Embeddings.csproj" />` from `libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj`.
    3. In `libs/BeeMemoryBank.Sync/DependencyInjection.cs`, remove `AddHostedService<PendingEmbeddingProcessor>()` and expose it as an extension method in `BeeMemoryBank.Embeddings` (e.g. `services.AddPendingEmbeddingProcessor()`).
  - **Cost**:
    - Exactly 1 file moved, ~10 lines changed in DI wiring.
    - Zero runtime performance overhead.
    - Completely eliminates the transitive ONNX/tokenizers dependency from `BeeMemoryBank.Sync`.

- **Option B (Zero-Risk Phased Isolation for Stages 1 & 2)**:
  In **Stage 1 (Skeleton)** and **Stage 2 (Identity & Pairing)**, `mobile/BeeMemoryBank.BlindMobile` references **ONLY**:
  - `libs/BeeMemoryBank.Core`
  - `libs/BeeMemoryBank.Crypto`
  - `libs/BeeMemoryBank.Storage`
  It does **NOT** reference `BeeMemoryBank.Sync` in Stages 1 and 2.
  All stand-ins in Stage 1, and the identity recording (`IBlindIdentityRecorder` -> `INodeIdentityRepository`) and Keystore pairing in Stage 2, require only Core, Crypto, and Storage!
  This guarantees that Stage 1 and Stage 2 produce a clean APK with zero risk of pulling in `Embeddings`. For Stage 3 (Sync), the orchestrator can either apply Option A or provide a decoupled `BlindSyncClient`.

---

## c. The Five Stand-ins

The five stand-ins are defined in [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:1-44`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L1-L44).

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                             BUILD ORDER FOR STAND-INS                       │
│                                                                             │
│  Stage 2:                                                                   │
│  [Stand-in 1] PendingBlindIdentityRecorder ───► Writes tbl_node_identity    │
│                                                 with v=2 (external key)     │
│                                                                             │
│  Stage 3:                                                                   │
│  [Stand-in 2] PendingBlindReplicaSource   ───► Downloads /api/blind/replica │
│                                                 and installs snapshot DB    │
│  [Stand-in 3] PendingBlindPhoneSync       ───► Protocol 3 pull sync         │
│                                                 with v=2 Keystore signer    │
│                                                                             │
│  Stage 4:                                                                   │
│  [Stand-in 5] PendingRecoverySetSource    ───► Queries DB for boxes,        │
│                                                 links, anchors, secrets     │
│  [Stand-in 4] PendingBlindPackageSource   ───► Assembles backup body:       │
│                                                 replica tar.gz + events     │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

### Stand-in 1: `PendingBlindIdentityRecorder : IBlindIdentityRecorder`
- **File & Lines**: [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:9-15`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L9-L15)
- **Contract Interface**: `IBlindIdentityRecorder` in [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs:40-43`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs#L40-L43)
- **Current Stand-in**:
  ```csharp
  public Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct) => Task.CompletedTask;
  ```
- **Real Design**:
  When `BlindPhonePairing.CreateIdentityAsync` runs:
  1. A UUIDv8 blind `nodeId` is generated (`BlindNodeId.NewId()`).
  2. An Ed25519 keypair is generated; the private seed is stored in `IBlindPhoneKeys` (AndroidKeyStore).
  3. `IBlindIdentityRecorder.RecordAsync` is called to persist the node's identity row into SQLite table `tbl_node_identity`.
  4. The row is written with:
     - `node_id` = nodeId
     - `display_name` = displayName
     - `ed25519_public_key` = publicKey
     - `ed25519_private_key_v` = 2 (`NodeIdentityCrypto.ExternalKeyVersion`)
     - `ed25519_private_key` = empty byte array
     - `ed25519_private_key_iv` = null
     - `dek_epoch` = 0
- **Exact Server / Library Types Used**:
  - `NodeIdentity` model: [`libs/BeeMemoryBank.Core/Models/NodeIdentity.cs:3-23`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Models/NodeIdentity.cs#L3-L23)
  - `INodeIdentityRepository.SaveAsync`: [`libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs:5-12`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Interfaces/INodeIdentityRepository.cs#L5-L12)
  - `NodeIdentityCrypto.ExternalKeyVersion = 2`: [`libs/BeeMemoryBank.Crypto/NodeIdentityCrypto.cs:18`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Crypto/NodeIdentityCrypto.cs#L18)
  - Database schema: `tbl_node_identity` in migration `001_initial_schema.sql` and `005_wrapped_node_identity.sql`.
  - Server auth verifier: `PeerAuthenticator.AuthenticatePeerAsync`: [`libs/BeeMemoryBank.Sync/PeerAuthenticator.cs:33-40`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/PeerAuthenticator.cs#L33-L40)
- **What is Missing**:
  Nothing is missing on server or libraries. SQLite repository already handles `NodeIdentity` persistence, and the database schema supports `ed25519_private_key_v = 2`.
- **Order to Build**: **1st** (Stage 2 - Identity and Pairing).

---

### Stand-in 2: `PendingBlindReplicaSource : IBlindReplicaSource`
- **File & Lines**: [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:18-22`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L18-L22)
- **Contract Interface**: `IBlindReplicaSource` in [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs:52-55`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs#L52-L55)
- **Current Stand-in**:
  ```csharp
  public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) =>
      throw new BlindFeaturePendingException("the first download of the blind package (GET /api/blind/replica)");
  ```
- **Real Design**:
  Executed by `BlindHeavyWork` when paired and `!state.InitialLoadDone` (on Wi-Fi and charger):
  1. Configures `HttpClient` with certificate pinning against `target.SpkiPin` (`PinnedSyncHttpClient`).
  2. Performs sync transport handshake:
     - Calls `POST /api/sync/challenge`.
     - Signs challenge using `KeystoreNodeAuthSigner` with the v=2 private seed from AndroidKeyStore.
     - Calls `POST /api/sync/authenticate` with protocol version 3 to obtain bearer `authToken`.
  3. Downloads `bmb-blind-package.tar.gz` from `GET /api/blind/replica`:
     - Resumable download writing into `workDirectory/replica.tar.gz.part`.
     - Uses HTTP Range headers (`Range: bytes={offset}-`) if resuming after pause.
     - Reports download fraction via `progress`.
  4. Verifies package integrity:
     - Checks SHA-256 against `X-BMB-Package-Sha256` header.
     - Checks detached signature against `X-BMB-Snapshot-Signature` using `target.PublicKey` (`BlindCallCode.PublicKey`).
  5. Extracts and installs:
     - Extracts tar.gz using `System.Formats.Tar`.
     - Parses and verifies `blind-manifest.json` (`BlindManifest.Parse`).
     - Drains SQLite connection pools (`SqliteConnection.ClearAllPools()`).
     - Atomically moves extracted database (`beememorybank.db`) and `media/` into place.
     - Sets `state.InitialLoadDone = true`.
- **Exact Server / Library Types Used**:
  - Endpoint `GET /api/blind/replica`: [`server/BeeMemoryBank.Api/Endpoints/BlindEndpoints.cs:84-115`](file:///D:/review/bmb-android-blind/repo/server/BeeMemoryBank.Api/Endpoints/BlindEndpoints.cs#L84-L115)
  - Producer builder: `BlindPackageBuilder`: [`server/BeeMemoryBank.Api/Services/Blind/BlindPackageBuilder.cs:21-72`](file:///D:/review/bmb-android-blind/repo/server/BeeMemoryBank.Api/Services/Blind/BlindPackageBuilder.cs#L21-L72)
  - `BlindManifest`: [`libs/BeeMemoryBank.Sync/Blind/BlindManifest.cs:11-37`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Blind/BlindManifest.cs#L11-L37)
  - `BlindCallCode`: [`libs/BeeMemoryBank.Core/Models/BlindPhoneCodes.cs:51-118`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Models/BlindPhoneCodes.cs#L51-L118)
  - `PinnedSyncHttpClient`: [`libs/BeeMemoryBank.Sync/Blind/PinnedSyncHttpClient.cs:14-25`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Blind/PinnedSyncHttpClient.cs#L14-L25)
- **What is Missing**:
  Server side is complete. The mobile client needs a clean `BlindReplicaInstaller` class using `System.Formats.Tar` to extract the package and swap the database atomically without server-side `SnapshotService` dependencies.
- **Order to Build**: **2nd** (Stage 3 - Download and Sync).

---

### Stand-in 3: `PendingBlindPhoneSync : IBlindPhoneSync`
- **File & Lines**: [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:25-29`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L25-L29)
- **Contract Interface**: `IBlindPhoneSync` in [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs:61-64`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs#L61-L64)
- **Current Stand-in**:
  ```csharp
  public Task SyncOnceAsync(BlindCallCode target, CancellationToken ct) =>
      throw new BlindFeaturePendingException("blind sync with the v=2 identity signer");
  ```
- **Real Design**:
  Executed periodically (>= 15 min via `BlindSyncWorker`):
  1. Configures pinned `HttpClient` for `target.Address` with `target.SpkiPin`.
  2. Authenticates with listening node using v=2 Keystore key (`KeystoreNodeAuthSigner`).
  3. Pulls new events: `GET /api/sync/events?since={lastSeq}&limit=100`.
  4. Applies incoming events via `EventApplier`:
     - Blind node never holds DEK and authors no events.
     - Ciphertext bodies for notes/comments are written directly to `tbl_article`.
     - Concept tags use `BlindEmbeddingGenerator` (no ONNX model needed; throws `ModelUnavailableException` to skip vectors).
     - Recovery events (`recovery_box_set`, `recovery_box_retire`, `retired_link_set`, `state_anchor`, `sealed_secret_set`) are applied to replicated tables.
  5. Pulls any referenced media blobs via `GET /api/sync/blobs/{hash}` into local blob store.
  6. Updates pull cursor in `ISyncPositionRepository` and records `state.LastSyncAt = DateTimeOffset.UtcNow`.
- **Exact Server / Library Types Used**:
  - Sync endpoints: [`server/BeeMemoryBank.Api/Endpoints/SyncEndpoints.cs:33-226`](file:///D:/review/bmb-android-blind/repo/server/BeeMemoryBank.Api/Endpoints/SyncEndpoints.cs#L33-L226)
  - `SyncClient`: [`libs/BeeMemoryBank.Sync/SyncClient.cs:17-88`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/SyncClient.cs#L17-L88)
  - `EventApplier`: [`libs/BeeMemoryBank.Sync/EventApplier.cs:20-60`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/EventApplier.cs#L20-L60)
  - `BlindEmbeddingGenerator`: [`libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs:13-21`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Blind/BlindEmbeddingGenerator.cs#L13-L21)
  - `SyncProtocolVersion.Current = 3`: [`libs/BeeMemoryBank.Sync/SyncProtocolVersion.cs`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/SyncProtocolVersion.cs)
  - `KeystoreNodeAuthSigner`: [`mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreNodeAuthSigner.cs:19-61`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Platforms/Android/KeystoreNodeAuthSigner.cs#L19-L61)
- **What is Missing**:
  Server side is complete. Clean library access to `SyncClient` and `EventApplier` without `BeeMemoryBank.Embeddings` (as detailed in section b).
- **Order to Build**: **3rd** (Stage 3 - Download and Sync).

---

### Stand-in 4: `PendingBlindPackageSource : IBlindPackageSource`
- **File & Lines**: [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:32-36`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L32-L36)
- **Contract Interface**: `IBlindPackageSource` in [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs:71-74`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs#L71-L74)
- **Current Stand-in**:
  ```csharp
  public Task CreateAsync(string destinationPath, CancellationToken ct) =>
      throw new BlindFeaturePendingException("building the blind package on the phone");
  ```
- **Real Design**:
  Constructs the unencrypted body that will be fed into `AndroidBackupWriter.WriteAsync`:
  Per `BlindPhoneBackupBody.cs`:
  1. Streams the listening node's signed blind replica package (cached in `blind-replica/` on phone) along with its signature header.
  2. Streams the signed events that the phone has received since the replica checkpoint.
  3. Writes this compound body stream to `destinationPath`.
  4. `AndroidBackupWriter` then encrypts this body chunk-by-chunk with the phone's backup key (`IBlindPhoneKeys.LoadBackupKey()`) and prefixes it with the open recovery-set header.
- **Exact Server / Library Types Used**:
  - `BlindPhoneBackupBody`: [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneBackupBody.cs:1-60`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneBackupBody.cs#L1-L60)
  - `AndroidBackupWriter`: [`libs/BeeMemoryBank.Crypto/AndroidBackupFile.cs:214-325`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Crypto/AndroidBackupFile.cs#L214-L325)
  - `BlindPhoneBackupRunner`: [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneBackupRunner.cs:21-129`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneBackupRunner.cs#L21-L129)
- **What is Missing**:
  The phone needs to preserve the downloaded replica `.tar.gz` and `.sig` in its data directory, and serialize post-replica events from `IEventLogRepository`.
- **Order to Build**: **4th** (Stage 4 - Backup Files and Schedule).

---

### Stand-in 5: `PendingRecoverySetSource : IRecoverySetJsonSource`
- **File & Lines**: [`mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs:39-43`](file:///D:/review/bmb-android-blind/repo/mobile/BeeMemoryBank.Mobile/Services/Blind/PendingBlindPieces.cs#L39-L43)
- **Contract Interface**: `IRecoverySetJsonSource` in [`libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs:80-83`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Core/Services/BlindPhone/BlindPhoneContracts.cs#L80-L83)
- **Current Stand-in**:
  ```csharp
  public Task<string> BuildJsonAsync(CancellationToken ct) =>
      throw new BlindFeaturePendingException("the recovery set of the phone's data");
  ```
- **Real Design**:
  Constructs the open JSON header of the backup file per CONTRACTS §2 and plan 6.8:
  Queries the phone's local SQLite database:
  - `tbl_recovery_box` where `status = 'A'`
  - `tbl_dek_retired_link`
  - `tbl_state_anchor`
  - `tbl_sealed_secret` where `status = 'A'` (must contain `android-backup:<phone_id>`)
  Serializes to JSON with format `bmb-recovery-set-v1`.
  `RecoverySetBuilder` in [`libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs:93-136`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs#L93-L136) already contains the complete implementation!
- **Exact Server / Library Types Used**:
  - `RecoverySetBuilder`: [`libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs:93-136`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs#L93-L136)
  - `RecoverySet`: [`libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs:57-87`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Sync/Recovery/RecoverySet.cs#L57-L87)
  - `AndroidBackupHeader`: [`libs/BeeMemoryBank.Crypto/AndroidBackupFile.cs:14-21`](file:///D:/review/bmb-android-blind/repo/libs/BeeMemoryBank.Crypto/AndroidBackupFile.cs#L14-L21)
- **What is Missing**:
  The implementation exists in `RecoverySetBuilder`. If `BeeMemoryBank.Sync` split is completed, `BlindMobile` directly uses `RecoverySetBuilder`.
- **Order to Build**: **5th** (Stage 4 - Backup Files and Schedule).

---

## d. Test Strategy

### 1. Project Distribution
- **Business Logic in Plain Class Libraries**:
  All core logic lives in `BeeMemoryBank.Core` and `BeeMemoryBank.Crypto`, which target `net10.0` and can be tested without an Android device or emulator.
- **New Test Project**: `tests/BeeMemoryBank.BlindMobile.Tests` (target `net10.0`).
  Houses unit tests for:
  - Reference guard tests (verifying no forbidden references in `BeeMemoryBank.BlindMobile.csproj`).
  - Unit tests for phone services, reset, preferences, and key abstractions using test doubles.
- **Integration Tests**: `tests/BeeMemoryBank.Integration.Tests`.
  Houses server-side interaction tests using `WebApplicationFactory`:
  - Blind phone pairing flow (`POST /api/blind-nodes/android`).
  - Blind package generation and download (`GET /api/blind/replica`).
  - Protocol 3 sync with v=2 identity signer.

### 2. Existing Test Coverage
- [`tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Core.Tests/BlindPhoneServicesTests.cs) (68 tests passing):
  Tests identity generation, one-time secret spending, re-pair secret renewal, backup charger/Wi-Fi guards, sealed secret presence check, chunked backup encryption/decryption, resumption of interrupted backups, retention of latest 3 backups, heavy work execution ordering, backup schedule calculations, log capping.
- [`tests/BeeMemoryBank.Core.Tests/BlindPhoneCodesTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Core.Tests/BlindPhoneCodesTests.cs): Parsing and validation of `BlindPhoneCode` and `BlindCallCode`.
- [`tests/BeeMemoryBank.Core.Tests/BlindPhoneEnrollmentTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Core.Tests/BlindPhoneEnrollmentTests.cs): Windows-side enrollment preparation and seal creation.
- [`tests/BeeMemoryBank.Core.Tests/BlindPhoneWorkTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Core.Tests/BlindPhoneWorkTests.cs): Device state conditions (Wi-Fi, charging, battery >= 20%).
- [`tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Integration.Tests/BlindPhonePairingTests.cs) (15 tests passing): Server-side pairing endpoint and listener validation.
- [`tests/BeeMemoryBank.Integration.Tests/BlindReplicaProducerTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Integration.Tests/BlindReplicaProducerTests.cs): `GET /api/blind/replica` package generation and authentication.
- [`tests/BeeMemoryBank.Integration.Tests/BlindReplicaSignatureTests.cs`](file:///D:/review/bmb-android-blind/repo/tests/BeeMemoryBank.Integration.Tests/BlindReplicaSignatureTests.cs): Detached Ed25519 signature checks.

### 3. New Tests to Add
- **Stage 1**:
  - `ForbiddenReferencesTests`: Reads `BeeMemoryBank.BlindMobile.csproj` and verifies absence of `Embeddings`, `Media`, `Markdig`, `Markdown`.
  - `CopiedCodeUnitTests`: Verifies `PreferencesBlindStore` and reset logic against fakes.
- **Stage 2**:
  - `BlindIdentityRecorderTests`: Verifies that `IBlindIdentityRecorder` writes `tbl_node_identity` with `ed25519_private_key_v = 2`, `ed25519_public_key`, empty private key, and correct `node_id`.
  - `TwoCodePairingIntegrationTests`: End-to-end pairing test linking fake phone to Integration test host.
- **Stage 3**:
  - `BlindReplicaInstallerTests`: Unpacking and installing `bmb-blind-package.tar.gz`, verifying SHA-256 and detached signature.
  - `BlindPhoneSyncTests`: Protocol 3 pull sync against server with v=2 challenge signer.
- **Stage 4**:
  - `RecoverySetBuilderPhoneTests`: Querying phone database and generating valid `bmb-recovery-set-v1` containing sealed backup key.
  - `BlindPackageSourceTests`: Constructing composite backup body from replica package and incremental events.
  - `WipeAndResetTests`: Verifying complete cleanup of state, keys, DB, and temporary files.

### 4. What Can ONLY Be Checked on a Real Device
- AndroidKeyStore non-exportable hardware-backed key generation (`KeyGenParameterSpec`, TEE / StrongBox).
- WorkManager background triggers across OEM battery optimizations, doze mode, and device restarts.
- System foreground notification display (`ForegroundServiceType.TypeDataSync`).
- Storage Access Framework UI picker interaction (`SafExport.SaveAsync`).
- Process self-kill and restart via `Android.OS.Process.KillProcess(Process.MyPid())`.

---

## e. CI Workflow Changes (`.github/workflows/build-mobile.yml`)

The existing workflow in [`.github/workflows/build-mobile.yml`](file:///D:/review/bmb-android-blind/repo/.github/workflows/build-mobile.yml) builds only `BeeMemoryBank.Mobile`.
We add a second job `build-blind` (or matrix entry) for `BeeMemoryBank.BlindMobile`.

### Proposed CI Addition
```yaml
  build-blind:
    runs-on: ubuntu-latest
    env:
      DOTNET_INSTALL_DIR: ${{ github.workspace }}/.dotnet
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1

      - name: Setup .NET
        id: dotnet
        uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          dotnet-version: 10.0.x

      - name: Setup Java JDK 17
        uses: actions/setup-java@dd06d9cba3e5552c54d9f8ea23572deb30010f7c # v6
        with:
          distribution: 'microsoft'
          java-version: '17'

      - name: Cache .NET workloads
        id: workload-cache
        uses: actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6
        with:
          path: ${{ github.workspace }}/.dotnet
          key: dotnet-workloads-maui-android-net10-${{ runner.os }}-${{ steps.dotnet.outputs.dotnet-version }}

      - name: Install MAUI Android workload
        if: steps.workload-cache.outputs.cache-hit != 'true'
        run: dotnet workload install maui-android

      # Notice: NO ONNX download step! Blind app does not use model.onnx.

      - name: Restore blind mobile dependencies
        run: dotnet restore mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj

      - name: Build Blind APK (Release, unsigned)
        run: |
          dotnet publish mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj \
            -f net10.0-android -c Release --no-restore

      - name: Verify forbidden assets absent from Blind APK
        run: |
          APK_PATH=$(find mobile/BeeMemoryBank.BlindMobile/bin/Release/net10.0-android -name "*.apk" | head -n 1)
          if [ -z "$APK_PATH" ]; then echo "APK not found!"; exit 1; fi
          echo "Found APK: $APK_PATH"
          # Ensure model.onnx and forbidden assemblies are NOT present in APK
          if unzip -l "$APK_PATH" | grep -Ei "model\.onnx|Microsoft\.ML\.OnnxRuntime|BeeMemoryBank\.Embeddings|BeeMemoryBank\.Media|Indiko\.Maui|Markdig"; then
            echo "ERROR: Forbidden asset found in Blind APK!"
            exit 1
          fi
          echo "Verification passed: No forbidden assets in Blind APK."
```

---

## f. Risks, Open Questions & Proposed Stage Roadmap

### Numbered Risks and Open Questions for Orchestrator

1. **`BeeMemoryBank.Sync` -> `BeeMemoryBank.Embeddings` Reference**:
   - `libs/BeeMemoryBank.Sync/BeeMemoryBank.Sync.csproj:16` directly references `BeeMemoryBank.Embeddings` solely for `PendingEmbeddingProcessor.cs`.
   - **Question for Orchestrator**: Shall we move `PendingEmbeddingProcessor.cs` to `BeeMemoryBank.Embeddings` or `BeeMemoryBank.Api`, thereby freeing `BeeMemoryBank.Sync` from `Embeddings`? (Recommended: in Stage 1 and 2, `BlindMobile` references only Core, Crypto, and Storage; the split can be performed before Stage 3).
2. **Encapsulating Identity Seed in `KeystoreBlindPhoneKeys`**:
   - `KeystoreBlindPhoneKeys.cs:16` delegates the identity seed to `IIngestKeyStore` (`bmb_ingest.bin` with alias `bmb_ingest_key_v1`).
   - In `BeeMemoryBank.BlindMobile`, using a direct `KeystoreBlob` with alias `bmb_blind_seed_v1` cleanly decouples the blind app from the ingest key infrastructure. We recommend this self-contained design.
3. **Storage Access Framework (SAF) Automation**:
   - SAF invokes the system document picker activity, which cannot be automated headlessly in Windows or Linux CI without UI test runners. Core backup encryption and file generation will be verified headlessly via `AndroidBackupFile` and `BeeMemoryBank.Core.Tests`.
4. **AppShell vs. Single-Page Navigation**:
   - Because `BeeMemoryBank.BlindMobile` has only one screen (`BlindHomePage`), MAUI Shell flyouts are unnecessary. We recommend setting `MainPage = new NavigationPage(new BlindHomePage(...))` in `App.xaml.cs`.

---

### Proposed Stages (Adjusted)

- **STAGE 0 — PLAN (Current)**:
  Read documentation and code, analyze dependencies, write `PLAN.md` and `STAGE-0.md`. Zero product code. Commit and stop.
- **STAGE 1 — SKELETON**:
  Create `mobile/BeeMemoryBank.BlindMobile` with stand-ins. Reference only `Core`, `Crypto`, `Storage` (no `Embeddings`, no `Media`, no `Markdown`, no `Sync`). Add `tests/BeeMemoryBank.BlindMobile.Tests` with forbidden reference guards. Add CI build step in `.github/workflows/build-mobile.yml`. Build Release APK, verify size and absence of forbidden assemblies. Verify ordinary app remains byte-identical.
- **STAGE 2 — IDENTITY AND PAIRING**:
  Replace `PendingBlindIdentityRecorder` with real SQLite recorder writing `tbl_node_identity` with `ed25519_private_key_v = 2`. Implement AndroidKeyStore persistence via `KeystoreBlindPhoneKeys`. Implement two-code pairing and call code acceptance (`BlindPhonePairing`, `BlindCallCode`, `SpkiPinRegistry`). Unit and integration tests with fake listening node.
- **STAGE 3 — DOWNLOAD AND SYNC**:
  Implement `PendingBlindReplicaSource` (`GET /api/blind/replica` download with SPKI pinning, signature check, and atomic DB installation). Implement `PendingBlindPhoneSync` (protocol 3 sync with listening node using v=2 identity signer and `EventApplier` with `BlindEmbeddingGenerator`). Integration tests against test host.
- **STAGE 4 — BACKUP FILES AND SCHEDULE**:
  Implement `PendingBlindPackageSource` (assembling replica + incremental events) and `PendingRecoverySetSource` (`RecoverySetBuilder`). Wire encrypted backup writing (`AndroidBackupWriter`), SAF save (`SafExport`), foreground service (`BlindBackupService`), schedule, log, and wipe (`BlindPhoneReset`). Comprehensive tests.
