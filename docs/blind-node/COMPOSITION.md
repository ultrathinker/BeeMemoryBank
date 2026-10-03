# Blind node: what the container holds

The full application registers its services with three library methods (`AddStorage`, `AddCore`, `AddSync`, plus `AddRecovery`
inside it) and a long `ApiServices.cs`. A blind node registers the blind half of them in `libs/BeeMemoryBank.Blind/BlindComposition.cs`
(library services) and `server/BeeMemoryBank.BlindNode/Startup/BlindNodeServices.cs` (host services). A registration that is not
there is not "switched off": its implementation is not in the assembly, so nothing can resolve it.
`python tools/blind-link/registrations.py` prints, for every registration of the originals, whether the types it names are
linked into the blind assembly.

## Kept (library half)

| Group | Registrations | Why a blind node needs them |
|-------|---------------|-----------------------------|
| storage | `DbConnectionFactory` (+ `IDbConnectionFactory`), `MigrationRunner`, `DapperConfig.Configure()`, `EmbeddingVectorCache`, `ChunkEmbeddingVectorCache`, the repositories of the replicated data model (article, article body, blob, comment, folder + ACL, media, concept tag, tombstone, conflict version, role + ACL, user), the node's own (`NodeIdentity`, `Whitelist`, `KeySlot`, `RetiredMasterDek`), the sync's (`EventLog`, `SyncPosition`, `SyncPushPosition`, `SyncQuarantine`, `RestoreEventState`, `DekRotationState`, `RestoreReplayShield`, `AuditLog`, remote account/subscription/token), `FolderBootstrapper`, `CallerScopeHolder` | `EventApplier` applies replicated rows into these tables so a joining peer can be seeded from this node; the event log is the node's reason to exist |
| core | `SessionService`, `InvisibleModeService`, `MaintenanceModeService`, `SearchMetrics`, `CommentService`, `MediaService` (+ a transcoder that refuses), `MediaBlobBackfillService`, `FolderAccessService`, `ConceptTagService`, `LegacyPasswordSlotMigrationService`, `RemoteAccountService`, the null actor provider | constructor dependencies of the linked sync code. They exist in the assembly; they are never unlocked (no route can open a session) |
| sync | `LamportClock`, `SyncTrigger`, `EventLogger`, `EventApplier`, `SyncClient`, `HardDeleteService`, the pin registry and the pinned `HttpClient`, `SyncScheduler`, `CleanupService`, `LazySlotRewrapService`, the recovery services (`RecoverySetBuilder`, `StateAnchorService`, device box publisher, reconciler) | the mesh protocol, anchors and recovery sets a blind node takes part in |
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
node registers its own `BlindDekRotationApplier`), the full node's `AddRecovery` registration (`RecoveryServiceCollectionExtensions`;
the blind composition registers the recovery services it needs itself). Android-only: `BlindPhonePullClient` lives in `Blind.PhoneClient`.

Host (`ApiServices.cs`) registrations with no counterpart: MCP, agent bearer auth, caller-scope middleware and its HttpContext store,
the HTTP actor provider, OpenRouter and the chat stack, the embedding model and image transcoder, mDNS, update, DEK rotation, zip export,
compaction, download tokens, the unlock cache, OS auto-unlock, the remote-account scheduler, the node reset hook.

## How it is checked

* `BlindNodeCompositionTests.EveryRegisteredServiceCanBeConstructed_...`: the container is built with `ValidateOnBuild`; the hosted services
  are listed and the forbidden ones must be absent.
* `BlindNodeCompositionTests.TheBlindCodeDoesNotContain` / `TheHostDoesNotReferenceAnythingOfAFullNode` / `TheHostReachesTheLibrariesOnlyThroughTheBlindAssembly`.
* `BlindLinkedSetTests`: nothing on `tools/blind-link/exclude.txt` is linked.
* The blind-only integration tests of `BeeMemoryBank.Integration.Tests` run unchanged against this host (`BeeMemoryBank.BlindNode.Tests`):
  they found the one registration the first draft forgot (`IEmbeddingGenerator`) within a minute.

## Known limits (stated plainly)

The blind assembly still contains `SessionService`, `MasterKeyManager`, the `Crypto` primitives and the replication code that applies
rows: they are dependencies of the sync code the originals share, and the originals are not changed by this work. What the blind node
does NOT contain is every path that could open a session or read content for a person or an agent - no route, no middleware, no service
that offers it - and, since review B, the master-key re-wrap machinery of a peer (`PeerDekRotationApplier`, `DekRewrapper`,
`DekRotationMaterial`), `SealedSecretService` and the full node's `AddRecovery`.

Two more things stay compiled in because taking them out means editing an original file (tried: the host stops compiling):

* **`SnapshotService`** (a partial class of the Api). The parts a blind node uses - building and serving a ciphertext package,
  swapping the database file - sit in the same class as snapshot creation, encryption and restore, which call
  `SessionService.GetMasterDek()`. On a blind volume the master DEK is refused at that call, so the paths cannot run; they are
  present, not absent.
* **`BlindNodeManager` and `BlindPreflight`** (the full node's side of adding a blind node). `BlindEndpoints.cs` is one file that maps both
  the routes of the blind node and the routes of the PC that manages it, so the host links the file and with it the management types.
  The host does not map the management routes (`ROUTES.golden.txt` is exact).

Splitting those two originals is a separate piece of work on the original code (BMB-91 follow-up). Until then the honest statement is:
*not reachable and not activatable on a blind volume*, not *not compiled in*.

