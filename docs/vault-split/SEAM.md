# Capability inventory (S1): what leaves the shared layer, what must be split

Release 2.0.1. Companion of the release plan (kept outside the repository). Method: a **removal probe** (a throw-away script, not part of the repository; the files it removed are the 35-file table below): take the sets the
blind host and the phone are compiled from today, remove the files the capability test classifies as vault, stand in empty shells for the removed types,
build, and read what breaks. What breaks in a file that stays is exactly the seam work. The probe ran in 5 rounds.

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
  They hold no key and open no content; splitting them is not needed for the claim "the blind node contains no vault code" as defined in the release plan, section 2 (D5).
* **The full Api keeps its `BMB_ROLE=blind` branch** (`ApiServices.cs`, `ApiPipeline.cs`) for 2.0.1; documented in COMPOSITION.md. It is a full binary in a restricted role, not the blind image.
* `Sync/PendingEmbeddingProcessor.cs` is a dead excluded file (`Sync.csproj` removes it; the live one is in Embeddings): not moved, not classified.

## Hidden consumers still to be found by the compiler during S3 (not by the probe)

Internal members crossing the new line (InternalsVisibleTo: `SessionService.PostUnlockCatchUp`, `AesGcmHelper` (internal in Crypto, used by `ArticleEncryptor`/`MediaEncryptor`
that move to Vault), `IArticleRepository` bypass members), the `Storage/DependencyInjection.cs` search-index block (resolves `SessionService`), test projects using vault types
(`Sync.Tests/SyncTestFixture.cs` and others), Infrastructure / Embeddings / Rekey (`SessionService`, `GetMasterDek`) which get a Vault reference.

## S2 status (BMB-102, operation-level seams)

Done in S2, all behind the same wire bytes, DB format and ciphertext:
`MediaStorageOptions` extracted; `NodeIdentityCrypto` split (constants, public-key helper, v=2 `SignWithExternalSeed` stay shared; master-DEK members moved to `NodeIdentityVault`);
`ExternalKeyNodeAuthSigner` (shared) beside `SessionNodeAuthSigner`; `IRemoteSentinelVerifier` + `RemoteSentinelVerifier` (SyncClient no longer takes `SessionService`);
`BlindEventLogger` (shared, refuses authoring) vs `EventLogger`; `ICurrentKeyFingerprintSource` (`RecoveryStatusService`); `IReplicaProducerAuthority` (`/api/blind/replica`);
`ISnapshotKeyOperations` (`SnapshotService` takes it instead of `SessionService`; the blind host passes `ExternalKeySnapshotKeyOperations`); `BlindWipeService` without the session;
`BlindComposition` and the lib link props shrunk to what the seams leave.

Not a code change: `KeyDerivation` stays shared whole (Argon2id + the memory budget are a neutral primitive; the master-key derivation call sites are in Vault classes, not in this file).

Folded into S3 because they only make sense together with the physical move (doing them earlier would mean writing them twice):
the three `DependencyInjection.cs` + `RecoveryServiceCollectionExtensions` (D3: `AddNodeStorage/AddNodeCore/AddNodeSync` stay, the old `AddStorage/AddCore/AddSync/AddRecovery` move to Vault),
the `Storage/DependencyInjection.cs` search-index block, `Api/Models` DTO split, the phone `AndroidBackupRestore` recognizer, the `SessionService` internals.

## S3 status (BMB-103, the graph cut)

Layout now (counts are `.cs` files):

| Assembly | Files | Who references it |
|---|---|---|
| Core 101, Crypto 12, Search 11, Storage 37, Sync 61 (**shared**) | 222 | every node, the blind host and the Android blind app included |
| `Blind.PhoneClient` (the phone's client of a blind node) | 14 | Android blind app, Api, Web and the tests that exercise it; **not** the Linux blind host, the Vault or the ordinary Android app |
| `Vault` (full-node layer) | 120 | Api, Cli, Web, Node, Mobile, Embeddings, Infrastructure, Media, Rekey, Migrator, SeedGen and their tests; never the shared libraries |

Rule used for the move: a library file goes to Vault iff no blind host or phone compiled it (the S1 link sets; 115 files) - the 35 candidates of S1 are among them, together with
the full-node-only services, repositories, the search index and segment store; the three `DependencyInjection.cs` (AddCore/AddStorage/AddSync compat names) follow. Folders in Vault mirror the
project each file came from; namespaces are unchanged, so no `using` changed anywhere. The shared libraries build on their own (no reference to Vault or PhoneClient), which is what
proves they need nothing of it.

Deviations from the release plan, with the reason:
1. `Blind.PhoneClient` is **kept** as a real library (15 `git mv`ed files) instead of being retired: the old test "phone-only code must not ride along in the Linux container" is a property worth keeping,
   and a graph in which PhoneClient and Vault both sit on the shared libraries and never reference each other keeps it true (a first attempt had Vault reference PhoneClient for one class, `BlindPhonePairing`, which only a test used; that dragged the phone client into the ordinary Android app's APK, which the ordinary-app test refuses. The class moved to PhoneClient; `JoinHttp` and `SpkiPin`, used by the CLI and the desktop node too, moved back to Crypto). `NullEventLogger` stays in shared Core (the full node registers it as the default, the phone as its logger; it is two lines).
2. No warning NoOp default for `IDekRotationApplier` in `AddNodeSync`: the event applier cannot be built without one, so a host that forgets it fails when the container resolves it, which is louder than a log line.
   The same holds for `IRestoreInitiator`, `IEventLogger` and `INodeAuthSigner`: the full node takes them from `AddSync` (Vault), the blind host registers its own.
3. The search index (`IndexBuilder`, segments, `EncryptedSegmentStore`, index lifecycle/processor) moved whole to Vault; `Search` keeps the stemmers/tokenizer the shared `ArticleRepository` uses.
4. `libs/BeeMemoryBank.Blind` (link assembly), its props, `BlindPhoneProxy`, `seeds-phone.txt`, `registrations.py`, `stats.py` are removed from the clone (own BMB-91 scaffolding, replaced and proven by `BlindNode.Tests`
   and the Android builds). `tools/blind-link/blind_link.py` recomputes the Api set only; `exclude.txt` lists the Api files that left the blind set.

`InternalsVisibleTo` for Vault (each reason is in the csproj): Core -> `ClearFolderIdUnscopedAsync`, `CodeText`; Crypto -> `AesGcmHelper`, `HeavyDerivationQueue.IsOnWorker`; Storage -> `BlobRepository.StoreOnAsync`.
Sync -> PhoneClient (`BlobTransport`). Vault -> test projects (`SearchService.MaxContentResults`, `SessionService.PostUnlockCatchUp`, `RecoveryKeyResolver.KeyOpened`, `IndexBuilder.SearchRankedReference`).
Api and Web remain unlisted in Core's grants, as before.

Goldens after the cut: full-node routes and container identical to the S0 baseline; blind-host container identical to the S2 golden.

## Review round (independent reviews of the finished cut)

Findings that led to changes after S4, and the two decisions that were not changes:

* `StateAnchorCrypto`, `SealedSecretCrypto` and `AesGcmHelper` (DEK-keyed seal / MAC / AES-GCM helpers) moved to the Vault: nothing shared called them any more. The `Crypto -> Vault` friend grant stays for `HeavyDerivationQueue.IsOnWorker`.
* `docs/vault-split/shared-types.txt` (every type of the shared assemblies and the phone client) is the second half of the contract next to `vault-types.txt`; a type that moves from the Vault to a shared assembly now
  shows up in both files. `VaultBoundary.ExplicitTypes` was widened (and a test checks that every name in it, and in the blind composition guards, is a real type: two names had the wrong namespace and guarded nothing).
* `RepositoryWriteGuardrailTests` discovers the repository interfaces in the Vault assembly as well, and fails on allow-list entries that match nothing. `BlindEventLogger` returns faulted tasks with the original wording and has a test per member.
  The published-folder and trimmed-package scans fail (instead of passing quietly) when `BMB_REQUIRE_SCAN` is set without `BMB_SCAN_DIRS`; CI sets both.
* `/api/blind/replica` resolves `IReplicaProducerAuthority` when a package is built, as the code it replaced resolved `BlindPreflight`. `SnapshotService` without key operations signs a v=0 identity again.
* Not changed on purpose: `Models/Requests.cs` and `Models/Responses.cs` are still linked whole into the blind host (data-only records, no behaviour; splitting the management DTOs from the shared ones is a follow-up);
  the interfaces `ILazySlotRewrapService` and `IRecoveryBoxPublisher` (parameters are DEKs, no logic) stay in Core.
