using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.Vault.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The reader of open copies for the test host of the recovery engine (BMB-306). No restore of the product reads a copy that holds the vault in the open
/// (a format-1 blind package, a database copy with no baseline): <see cref="RecoveryRestoreService"/> refuses both unconditionally, and nothing in a product
/// host can change that. The engine's own tests - the key chain, the anchors, the clocks - run on fixtures that were made before metadata encryption was
/// switched on, so the recovery test factory (<see cref="RecoveryTestFactory"/>, with <c>allowOpenCopies</c>) registers this subclass in place of the
/// product's service. It lives in the test assembly only: the code below is what the product used to carry, with no gate to consult.
/// </summary>
public sealed class OpenCopyRecoveryRestoreService(IServiceScopeFactory scopes, LamportClock clock, ILogger<RecoveryRestoreService> logger)
    : RecoveryRestoreService(scopes, clock, logger)
{
    /// <summary>
    /// A blind package (BlindPackageBuilder). Read with the blind seed's own reader
    /// (<see cref="SnapshotService.ExtractVerifiedAsync"/>: every file against the signed manifest), the
    /// embedded signature checked against <paramref name="producerPublicKey"/>, and its signed
    /// blind-manifest.json taken as the evidence: whitelist (superadmin flags, TLS pins) and positions.
    /// </summary>
    /// <param name="events">The signed events that came with the package (the blind node's restore route).</param>
    public override async Task<RestoreResult> RestoreFromPackageAsync(
        string packagePath, byte[] signature, byte[] producerPublicKey, RestoreIdentity who,
        RestoreBlindPeer? blind, IReadOnlyList<SyncEvent> events, CancellationToken ct = default,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        // In the data folder and owner-only, not the OS temp folder: the extracted package holds the vault database in clear.
        string dataPath;
        using (var pathScope = scopes.CreateScope())
            dataPath = pathScope.ServiceProvider.GetRequiredService<SnapshotService>().DataPath;
        var dir = SnapshotStaging.NewDirectory(dataPath);
        try
        {
            VerifiedSnapshot package;
            BlindManifest manifest;
            using (var scope = scopes.CreateScope())
            {
                try
                {
                    package = await scope.ServiceProvider.GetRequiredService<SnapshotService>().ExtractVerifiedAsync(packagePath, dir);
                }
                catch (InvalidOperationException ex)
                {
                    // A file that does not match its manifest hash, an unknown layout: not a package to restore from.
                    throw new InvalidDataException($"Not a valid blind package: {ex.Message}", ex);
                }
                // Both signatures up front: the embedded one covers every file through the manifest, the sidecar
                // the whole archive (what the join import checks again), so nothing is resolved from a package
                // the import would refuse.
                if (!package.IsSignedBy(producerPublicKey)
                    || !Ed25519Signer.Verify(producerPublicKey,
                        await SnapshotService.ComputeSignaturePayloadAsync(package.ManifestBytes, packagePath, ct), signature))
                    throw new InvalidDataException("The package signature does not verify.");
                var manifestPath = Path.Combine(package.Directory, BlindManifest.FileName);
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("Not a blind package: blind-manifest.json is missing.");
                manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
            }
            var producer = blind?.NodeId ?? manifest.ProducerNodeId;
            if (manifest.ProducerNodeId != producer)
                throw new InvalidDataException("The package was not built by the node it came from.");
            // One row per node, and the producer's own row naming the key that signed the package.
            if (manifest.Whitelist.GroupBy(p => p.NodeId).Any(g => g.Count() > 1))
                throw new InvalidDataException("The package's manifest lists a node twice.");
            var producerRow = manifest.Whitelist.SingleOrDefault(p => p.NodeId == producer);
            if (producerRow == null || !ProducerKeyMatches(producerRow.PublicKeyB64, producerPublicKey))
                throw new InvalidDataException("The package's manifest does not name its producer with the key that signed it.");

            var evidence = new RestoreEvidence(RestoreEvidenceFromManifest.Of(manifest), events);
            return await RestoreCoreAsync(package.DatabasePath, recoverySet: null, who, blind is null ? null : blind with { CpSeq = manifest.CpSequence },
                evidence, async sp =>
                {
                    await sp.GetRequiredService<SnapshotService>().RestoreForJoinAsync(packagePath, signature, producerPublicKey);
                    // Beyond the join snapshot: what lets the restored node refuse late events about
                    // something deleted for good (plan 3.6).
                    ImportTables(sp.GetRequiredService<DbConnectionFactory>(), package.DatabasePath, BlindPackageBuilder.ExtraTables);
                }, ct, boxes);
        }
        finally
        {
            if (!SnapshotStaging.TryCleanupDirectory(dir))
                throw new IOException("The clear recovery staging files could not be removed. The recovery restore requires repair and was not reported as successful.");
        }
    }

    /// <summary>
    /// A database copy from a backup (plan 6.8), with the recovery set that lay next to it. No producer
    /// signature here — the anchor is what vouches for the state. The whitelist, positions and signed
    /// events come from the copy itself.
    /// </summary>
    public override async Task<RestoreResult> RestoreFromDatabaseAsync(
        string databasePath, RecoverySet? recoverySet, RestoreIdentity who, CancellationToken ct = default,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        DbConnectionFactory vault;
        using (var scope = scopes.CreateScope()) vault = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        var evidence = await DatabaseRestoreEvidence.ReadAsync(databasePath, vault);
        return await RestoreCoreAsync(databasePath, recoverySet, who, blind: null, evidence, sp =>
        {
            ImportTables(sp.GetRequiredService<DbConnectionFactory>(), databasePath,
                [.. SnapshotTables.Replicated, .. BlindPackageBuilder.ExtraTables]);
            return Task.CompletedTask;
        }, ct, boxes);
    }
}

/// <summary>
/// What the end of a restore does in the test host of the recovery engine (see <see cref="OpenCopyRecoveryRestoreService"/>): a node restored from a copy
/// that was made before metadata encryption was switched on stays open so that the test can read what was restored (such a node cannot be activated
/// either), and the restore's seal intent ends with it. Every other call - the setup of the source nodes, a join - is the product's own sealing.
/// The product's endpoint always calls <see cref="FreshNodeSealing"/>; only a host that replaces it, as this one does, restores into an open node.
/// </summary>
public sealed class OpenCopyFreshNodeSealing(
    SessionService session, ISyncProtocolPolicy policy, MetadataActivationService activation, SealMigrationService seal,
    IBaselineScheduler? baselines = null, IDekRotationStateRepository? rotations = null, ILogger<FreshNodeSealing>? logger = null,
    ISealIntents? intents = null)
    : FreshNodeSealing(session, policy, activation, seal, baselines, rotations, logger, intents)
{
    public override Task SealAsync(string masterPassword, bool activateMetadata, CancellationToken ct = default, bool afterRestore = false)
    {
        if (!afterRestore) return base.SealAsync(masterPassword, activateMetadata, ct, afterRestore);
        intents?.Complete();
        return Task.CompletedTask;
    }
}
