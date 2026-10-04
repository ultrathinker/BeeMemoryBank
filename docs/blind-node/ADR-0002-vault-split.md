# ADR-0002: the vault is its own assembly (2.0.1)

Status: accepted, implemented in 2.0.1 (BMB-99). Supersedes the library half of [ADR-0001](ADR-0001-link-first.md).

## Context

1.0.17 gave the blind node its own program, but the library code it shared with the full node was LINKED from Core / Storage / Sync / Crypto / Search into one
assembly, `BeeMemoryBank.Blind`. The set was computed by compiling, so it contained whatever the sync code reached - among it `SessionService`,
`MasterKeyManager`, the content encryptors and the code that creates and opens recovery boxes. The blind container could not use them (no route reaches them,
the master key is refused on a blind volume), but they were compiled in. The goal stated for the blind node is "no code in it that can read".

## Decision

Split the libraries by what a node needs, not by who calls what:

* **Shared** (Core, Crypto, Search, Storage, Sync): store, relay and apply ciphertext; speak sync protocol 3; hold the external-key (v=2) identity and sign with it;
  TLS pinning, pairing, own backups, the console password hash (Argon2 stays shared), build / serve / verify ciphertext packages, receive / validate / serve recovery sets.
* **Vault** (`libs/BeeMemoryBank.Vault`, full node only): sessions, master-key handling, article / comment / media / protected-content crypto, recovery-box creation and opening,
  sealed secrets, snapshot encrypt and restore with the master key, the search index of the content, article / user / role / tree / import services, the full-node repositories.
* **Phone client** (`libs/BeeMemoryBank.Blind.PhoneClient`): what the Android blind app needs and a Linux node does not.

Dependencies point one way: shared <- PhoneClient <- Vault. The shared libraries cannot reference the Vault, so a program that references only them cannot contain it.
Every vault file was moved with `git mv` (history kept) and keeps its namespace, so no `using` changed anywhere. The mixed files were split by an operation-level seam
(not a key-passing interface): `ISnapshotKeyOperations`, `IRemoteSentinelVerifier`, `IReplicaProducerAuthority`, `ICurrentKeyFingerprintSource`, the external-key
`INodeAuthSigner`, a refusing `BlindEventLogger`. DI is split the same way: `AddNodeStorage` / `AddNodeCore` / `AddNodeSync` are shared, the old `AddStorage` / `AddCore` / `AddSync`
are in the Vault and call them, so no full-node host changed.

Wire protocol (3), database format and migrations, event and ciphertext bytes are unchanged; the migrations stay in Storage under the same resource names.

## Consequences

* The blind host, console, CLI and the Android blind app contain none of the vault types; this is proved on the compiled assemblies (TypeDef / TypeRef / MemberRef / AssemblyRef scan with
  controls: `BlindBoundaryTests`, `AppBoundaryTests`, `VaultBoundaryListTests`), on publish folders and on the image file system (`BMB_SCAN_DIRS`), not only on project files.
  The same scan over the 1.0.17 blind host reports 41 findings; over 2.0.1 it reports none.
* The host still links Api source files (see ADR-0001); the Api's `BMB_ROLE=blind` branch is kept for 2.0.1.
* Not split (documented as allowed shared capabilities, candidates for a later release): `ConceptTagService`, `FolderAccessService`, `HardDeleteService`, `InvisibleModeService`,
  the `ArticleRepository` search methods and the stemmers.
* Interop promise: protocol-3 1.0.x nodes (1.0.17) <-> 2.0.1 in both directions; nothing on the wire gates on the application version.
* Rollback: nothing in the database format, key files or migrations changed, so a node can be returned to the 1.0.17 image or binary on the same volume. Release testing
  (old -> new -> old on a copy of a live node's data) confirms this; its result is recorded with the release, not assumed here.
