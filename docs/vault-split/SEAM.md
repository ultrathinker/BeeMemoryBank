# Capability inventory (S1): what leaves the shared layer, what must be split

Release 2.0.1. Companion of `D:\review\bmb-vault-split\PLAN-v2.md`. Method: a **removal probe** (`probe_cut.py`, kept in the work folder): take the sets the
blind host and the phone are compiled from today, remove the files the capability test classifies as vault, stand in empty shells for the removed types,
build, and read what breaks. What breaks in a file that stays is exactly the seam work. The probe ran in 5 rounds; logs in `logs/probe_*.txt`.

## The capability test

A type is **shared** iff a blind node needs it to: store, relay and apply ciphertext events; speak sync protocol 3; hold its external-key identity (v=2) and
sign with it; terminate TLS and check pins; pair; take its own backups; serve its console (password hash); build, serve and verify ciphertext packages;
receive, validate and serve recovery sets. It is **vault** iff it holds, derives, wraps, unwraps or uses a master key, opens a session, encrypts or decrypts
article / comment / media / protected content, creates or opens recovery boxes or sealed secrets, creates or restores snapshots with the master key, or
manages the node as an operator. Not a criterion: "is cryptography" (Argon2 for the console password, backup envelope, `AesGcmHelper`, `SealedSecretCrypto`,
`DekFingerprint`, `HeavyDerivationQueue` stay shared).

## Result of the probe

Removing these **35 files** from the blind sets breaks nothing except the seams listed in the next section (the blind-owned composition files are expected to change):

| Destination | Files |
|---|---|
| Vault (libs) | Core/Services: `SessionService`, `CommentService`, `MediaService` (without `MediaStorageOptions`), `RemoteAccountService`, `LegacyPasswordSlotMigrationService`, `NodeDataKeyEnvelope`; Core/Interfaces `IRetiredMasterDekStore`; Storage/Sqlite `RetiredMasterDekStore`; Crypto: `MasterKeyManager`, `DekManager`, `ArticleEncryptor`, `MediaEncryptor`, `ProtectedContentCodec`, `EnvelopeFraming`, `RecoveryBoxCrypto`; Sync: `EventLogger`, `SessionNodeAuthSigner`, `LazySlotRewrapService`; Sync/Recovery: `RecoveryEventPublisher`, `DeviceBoxPublisher`, `RecoveryReconciler`, `StateAnchorService` (+ the not-linked vault files that move with them: `RecoveryKeyResolver`, `SealedSecretService`, `PeerDekRotationApplier`, `DekRewrapper`, `DekRotationMaterial`, article/key/user/role/tree/import/restore/search services, ...) |
| Api only (stay in the Api project, no longer linked into the blind host) | `SnapshotService.Create/.Crypto/.Restore/.NetworkRestore/.Upload`, `Blind/BlindNodeManager`, `Blind/BlindPreflight`, `Recovery/RecoveryCleanupService`, `RecoveryTriggers`, `RecoveryReconcileWatcher`, `StateAnchorScheduler`, `StrongBoxService`, `AuditLogPruningHostedService` |

After this removal **293 files** stay in the shared sets (from 326). A text scan of those for master-key / content-crypto terms (`smell_scan.txt`) finds 22 files; 14 of them
are legitimate (stored wrapped-key rows `KeySlotRepository`/`MasterKeyStore`, fingerprints in recovery payloads, `AesGcmHelper`, `SealedSecretCrypto`, `AndroidBackupFile`
(the phone backup key, not the master DEK), `HeavyDerivationQueue`); the 8 that need work are the seam list below.

## The seams (S2 work list) - exactly what the probe left standing

| # | File (stays shared) | Problem | Resolution |
|---|---|---|---|
| 1 | `Core/Services/MediaService.cs` (moves to Vault) | declares `MediaStorageOptions`, used by `EventApplier`, `MediaBlobBackfillService`, `HardDeleteService`, `CleanupService` | extract the record to `Core/Services/MediaStorageOptions.cs` (shared, same namespace) |
| 2 | `Crypto/NodeIdentityCrypto.cs` | v=0/v=1 identity seed opening/wrapping uses `MediaEncryptor` + the master DEK (`DecryptPrivateKey`, `EncryptPrivateKey`, `SignWithIdentityOrGetDek`); v=2 external-key branch is blind-relevant | shared keeps constants, public-key helpers and the v=2 signing; the master-DEK parts go to a Vault class (same namespace); `INodeAuthSigner` implementations: `SessionNodeAuthSigner` (Vault), external-key signer (shared) |
| 3 | `Storage/Sqlite/RetiredMasterDekStore.cs`, `Core/Interfaces/IRetiredMasterDekStore.cs` | needs `NodeDataKeyEnvelope` (wraps retired master DEKs) | both move to Vault with their consumers (no shared consumer: the probe compiles without them) |
| 4 | `Sync/SyncClient.cs` | best-effort master-key sentinel check (`SessionService.IsUnlocked`, `GetMasterDek`, `MasterKeyManager.VerifySentinel`) | `IRemoteSentinelVerifier` (shared contract; Vault implementation; blind no-op); the shared block keeps its catch/skip rules |
| 5 | `Api/Services/BlindConsole/BlindWipeService.cs` | `SessionService.Lock()` | blind implementation drops the session coupling (nothing to lock); full-node reset keeps `Lock` (in Api, unchanged) |
| 6 | `Api/Services/Recovery/RecoveryStatusService.cs` | reads the session DEK to fingerprint it | `IRecoveryFingerprintProvider` (full: session DEK, then anchor fallback; blind: newest anchor) |
| 7 | `Api/Services/SnapshotService.cs` + `.Blind.cs` + `.Maintenance.cs` (+ callers `SyncEndpoints`, `BlindPackageBuilder`, `BlindSeedService`, `BlindReplicaPackageCache`) | one partial class: package build/serve/verify and file swap (needed) mixed with `SessionService?` ctor parameter, `GetMasterDek()` at `Maintenance.cs:201` and the encryption helpers | extract neutral `CiphertextPackageService` + `DatabaseFileSwap` (+ `IPackageSigner` for the signature framing now in `.Crypto.cs`); `SnapshotService` stays the Api facade (encrypt/restore/management) delegating to it; blind callers are retargeted |
| 8 | `Api/Endpoints/BlindEndpoints.cs` | blind routes + PC-side management routes in one file; the shared replica handler calls `BlindPreflight` (line ~103) | split the management map into its own file; `IReplicaProducerAuthority` for the superadmin check (full: `BlindPreflight`; blind: refuses) |

Further splits found by the critics that the probe cannot see (the file is still needed whole by shared code, only part of it is vault); scheduled in S2:
`Crypto/KeyDerivation.cs` (neutral Argon2 + budget stay shared for `BlindConsoleAuthService.cs:169`; master-key derivation goes to Vault),
`Sync/Recovery/RecoveryServiceCollectionExtensions.cs` and the three `DependencyInjection.cs` (D3), `Api/Models/Responses.cs` / `Requests.cs` (shared contracts vs
management DTOs), phone: `Core/Services/BlindPhone/AndroidBackupRestore.cs` (backup recognizer is shared, `OpenAsync` restore orchestration is not),
blind-owned `BlindComposition.cs` / `BlindNodeServices.cs` / `BlindNodeStartupTasks.cs` (vault registrations, session shutdown at `BlindNodeStartupTasks.cs:156-162`).

## Explicit scope decisions

* **In 2.0.1:** everything that touches master keys, sessions, content decryption/encryption, recovery creation/opening, snapshot encryption/restore, full management.
* **Allowed shared capabilities** (documented, guarded by a positive list, candidates for a later phase): non-decrypting replay helpers with local-authoring corners that cannot run
  on a blind node - `ConceptTagService`, `FolderAccessService` (cache invalidation / ACL rows used by `EventApplier`), `HardDeleteService` (remote executor + local preview),
  `ArticleRepository` search methods and the `Search` stemmers/tokenizer (plaintext text utilities pulled in by the repository), `InvisibleModeService`.
  They hold no key and open no content; splitting them is not needed for the claim "the blind node contains no vault code" as defined in PLAN-v2 section 2 (D5).
* **The full Api keeps its `BMB_ROLE=blind` branch** (`ApiServices.cs`, `ApiPipeline.cs`) for 2.0.1; documented in COMPOSITION.md. It is a full binary in a restricted role, not the blind image.
* `Sync/PendingEmbeddingProcessor.cs` is a dead excluded file (`Sync.csproj` removes it; the live one is in Embeddings): not moved, not classified.

## Hidden consumers still to be found by the compiler during S3 (not by the probe)

Internal members crossing the new line (InternalsVisibleTo: `SessionService.PostUnlockCatchUp`, `AesGcmHelper` (internal in Crypto, used by `ArticleEncryptor`/`MediaEncryptor`
that move to Vault), `IArticleRepository` bypass members), the `Storage/DependencyInjection.cs` search-index block (resolves `SessionService`), test projects using vault types
(`Sync.Tests/SyncTestFixture.cs` and others), Infrastructure / Embeddings / Rekey (`SessionService`, `GetMasterDek`) which get a Vault reference.
