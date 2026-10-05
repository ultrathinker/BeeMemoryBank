# Blind node: what the container holds

The full application registers its services with three library methods (`AddStorage`, `AddCore`, `AddSync`, plus `AddRecovery`
inside it; since the vault split they live in `BeeMemoryBank.Vault`) and a long `ApiServices.cs`. Each of them calls a **shared module** that a
blind node uses too: `AddNodeStorage`, `AddNodeCore`, `AddNodeSync` (+ `AddNodeSyncScheduler`, `AddNodeCleanupService`) in the `NodeDependencyInjection`
class of Storage, Core and Sync. A blind node calls the same modules from `server/BeeMemoryBank.BlindNode/Startup/BlindNodeServices.cs`, which adds
the host's own services. A registration that is not there is not "switched off": its implementation is not in any assembly the blind program references,
so nothing can resolve it. The shared modules deliberately register no event logger, node-auth signer, restore initiator or DEK rotation applier:
the full node takes them from `AddSync` (Vault), the blind host registers its own, and a host that registers none fails when the container resolves it.

`BeeMemoryBank.Blind.AppCore` is the Android and future desktop application's separate composition boundary. It owns the narrow receive-only graph and blind-app rules; the MAUI host supplies only Keystore, committed SharedPreferences, WorkManager and device-condition adapters. AppCore depends only on Core, Crypto, Storage, Sync and `Blind.PhoneClient`, never Vault or host UI APIs.

## Kept (library half)

| Group | Registrations | Why a blind node needs them |
|-------|---------------|-----------------------------|
| storage | `DbConnectionFactory` (+ `IDbConnectionFactory`), `MigrationRunner`, `DapperConfig.Configure()`, `EmbeddingVectorCache`, `ChunkEmbeddingVectorCache`, the repositories of the replicated data model (article, article body, blob, comment, folder + ACL, media, concept tag, tombstone, conflict version, role + ACL, user), the node's own (`NodeIdentity`, `Whitelist`, `KeySlot`, `RetiredMasterDek`), the sync's (`EventLog`, `SyncPosition`, `SyncPushPosition`, `SyncQuarantine`, `RestoreEventState`, `DekRotationState`, `RestoreReplayShield`, `AuditLog`, remote account/subscription/token), `FolderBootstrapper`, `CallerScopeHolder` | `EventApplier` applies replicated rows into these tables so a joining peer can be seeded from this node; the event log is the node's reason to exist |
| core | `InvisibleModeService`, `MaintenanceModeService`, `SearchMetrics`, `MediaBlobBackfillService`, `FolderAccessService`, `ConceptTagService`, the null actor provider | non-decrypting helpers the sync code uses. No `SessionService`, `CommentService`, `MediaService`, `RemoteAccountService` and no content crypto: they are vault code |
| sync | `LamportClock`, `SyncTrigger`, `BlindEventLogger` (refuses to author), `ExternalKeyNodeAuthSigner` (signs with the external key), `EventApplier`, `SyncClient` (no master-key sentinel check), `HardDeleteService`, the pin registry and the pinned `HttpClient`, `SyncScheduler`, `CleanupService`, `RecoverySetBuilder` | the mesh protocol, and the recovery sets a blind node receives and serves. No `EventLogger`, `LazySlotRewrapService`, `StateAnchorService`, device box publisher or reconciler: they create or open recovery boxes with the master key |
| blind role (host) | `FileNodeKey` as `IExternalNodeKey`, `BlindState`, `BlindRestoreInitiator`, `BlindDekRotationApplier`, `BlindEmbeddingGenerator`, `BlindTlsIdentity`, `BlindPairing`, `BlindSeedService`, `BlindLogTrimmer`, `BlindPackageBuilder` + `BlindReplicaPackageCache`, backups, console login, wipe, the blind side of recovery (`BlindRestoreCodeService`, anchors, boxes status) | what makes the node blind: its identity key lives in a file, it cannot rotate or restore a vault, it computes no vector |

## Dropped (not linked, so not registered)

Search index: `SearchQueryCache`, `SearchService`, `IndexBuilder`, `SearchIndexRuntimeState`, `SearchIndexLifecycleService`,
`PendingIndexProcessor`, `SegmentManifestRepository`, `SegmentTombstoneRepository`, `EncryptedSegmentStore`,
`ArticleChunkEmbeddingRepository`, `ProjectionMatrixRepository`.
Management of content and people: `ArticleService`, `ArticleDiffService`, `TreeService`, `FolderService`, `CopyService`,
`KeyManagementService`, `InitializationService`, `UserService`, `RoleService`, `ObsidianImportService`, `BeeImportService`,
`RestoreService`, `RestoreBootstrapMarker`, `RemoteEventApplier`, `AgentRepository`, `FavoriteRepository`,
`ArticleVersionRepository`, `SealedSecretService`.
Sync defaults a full host falls back to: `NoOpRestoreInitiator`, `PeerDekRotationApplier` with `DekRewrapper` and `DekRotationMaterial` (a blind
node registers its own `BlindDekRotationApplier`), the full node's `AddRecovery` registration (`RecoveryServiceCollectionExtensions`).
Android-only: `BlindPhonePullClient` lives in `Blind.PhoneClient`. All of these are in `BeeMemoryBank.Vault` (or `Blind.PhoneClient`), assemblies the blind host does not reference.

Host (`ApiServices.cs`) registrations with no counterpart: MCP, agent bearer auth, caller-scope middleware and its HttpContext store,
the HTTP actor provider, OpenRouter and the chat stack, the embedding model and image transcoder, mDNS, update, DEK rotation, zip export,
compaction, download tokens, the unlock cache, OS auto-unlock, the remote-account scheduler, the node reset hook.

## How it is checked

* `BlindNodeCompositionTests.EveryRegisteredServiceCanBeConstructed_...`: the container is built with `ValidateOnBuild`; the hosted services
  are listed and the forbidden ones must be absent.
* `BlindNodeCompositionTests.TheBlindCodeDoesNotContain` / `TheHostDoesNotReferenceAnythingOfAFullNode` / `TheHostDoesNotReachTheFullNodeLayer`.
* `BlindBoundaryTests` / `AppBoundaryTests`: assembly-level scan (TypeDefs, TypeRefs, MemberRefs, AssemblyRefs) for the vault, with controls; also run over publish folders
  and the extracted image (`BMB_SCAN_DIRS`) by the release process.
* `BlindLinkedSetTests`: nothing on `tools/blind-link/exclude.txt` is linked, and no Vault file is linked anywhere.
* The blind-only integration tests of `BeeMemoryBank.Integration.Tests` run unchanged against this host (`BeeMemoryBank.BlindNode.Tests`):
  they found the one registration the first draft forgot (`IEmbeddingGenerator`) within a minute.

## Known limits (stated plainly)

Since the vault split the blind program contains **no** session, master-key manager, content encryptor, event logger that signs with the DEK,
recovery-box creation or opening, full snapshot encrypt/restore, or PC-side node management; the assembly-level guards above hold that.
What it still contains, on purpose, and why:

* **Shared, non-decrypting helpers with local-authoring corners**: `ConceptTagService`, `FolderAccessService` (cache invalidation and ACL rows the event
  applier uses), `HardDeleteService` (the remote executor), `InvisibleModeService`, the `ArticleRepository` search methods and the `Search` stemmers/tokenizer.
  They hold no key and open no content; splitting them is a candidate for a later release.
* **`SnapshotService`** (a partial class of the Api, linked): the package engine - building and serving a ciphertext package, swapping the database
  file, signing with the node identity through `ISnapshotKeyOperations` (the blind host passes the external-key implementation; there is no session-backed one in it).
  The encrypt/restore/management partials are Api-only and are not linked.
* **The Api's own `BMB_ROLE=blind` branch** is kept for 2.0.1: it is the full Api (with the Vault) in a restricted role, not the blind image.
* **Phone-only code** (`Blind.PhoneClient`) is not in the Linux container.

