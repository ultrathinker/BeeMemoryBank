using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// A format-2 seed (docs/design/step-a-spec.md 8, 9): the package is verified (8.6) against the authority this node already
/// knows - the pair code's seeder or a superadmin peer - and adopted into the blind store. Nothing is opened and no database
/// is swapped: the node keeps its log and its push positions, takes the whitelist rows and positions it lacks, the clear
/// control tables, the blobs and the baseline, then stores the deltas through the relay applier. The first adoption purges
/// the plaintext content a format-1 seed left (9.3).
/// </summary>
public sealed partial class BlindSeedService
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;

    /// <summary>The format string of the package's blind manifest, or null when there is none to read (the format-1 path reports why).</summary>
    private static async Task<string?> PackageFormatAsync(string archive, CancellationToken ct)
    {
        try
        {
            var bytes = await Format2Package.ReadEntryAsync(archive, BlindManifest.FileName, MaximumManifestBytes, ct);
            if (bytes is null) return null;
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String
                ? format.GetString()
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }

    private async Task ApplyFormat2Async(Upload upload, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        // Who serves the package and whose baselines count here: what this node knew before it read the package (8.6).
        var (servingNode, servingKey) = await ServingNodeAsync(upload.Authority, self, whitelist);
        var trusted = await TrustedProducersAsync(upload, whitelist, servingNode, servingKey, ct);

        VerifiedPackageV2 package;
        try
        {
            package = await sp.GetRequiredService<IBlindPackageV2Reader>().ExtractVerifiedAsync(
                upload.Path, [], servingKey, id => trusted.TryGetValue(id, out var t) ? t : null,
                Path.Combine(SeedDir(upload.SeedId), "package-v2"), ct);
        }
        catch (InvalidDataException ex)
        {
            // The reader's own sentence (a package file name, a hash, a count) is not passed on: the sender is told the fixed sentence of the code, the exception
            // travels as the inner one and its type is what the log names.
            throw new BlindSeedRejectedException(BlindSeedRefusal.PackageV2Invalid, ex);
        }
        var manifest = package.Manifest;
        if (manifest.SeedId != upload.SeedId)
            throw new BlindSeedRejectedException(BlindSeedRefusal.WrongSeed);
        if (manifest.ProducerNodeId != servingNode)
            throw new BlindSeedRejectedException(BlindSeedRefusal.NotBuiltBySeedingNode);
        if (upload.Authority is BlindSeedAuthority.PairingSecret && manifest.Kind != BlindPackageKinds.Full)
            throw new BlindSeedRejectedException(BlindSeedRefusal.FirstSeedNeedsFullPackage);
        await CheckSchemaV2Async(package.Directory, sp);

        using (await EventWriteGate.Instance.QuiesceAsync(ct))
        {
            // Every write below (imports, adoption, the deltas through the applier) is this flow's; sync applies wait.
            using var ownerFlow = EventWriteGate.EnterOwnerFlow();
            await HeavyOperationLock.Instance.WaitAsync();
            try
            {
                // The baseline files switch and the first adoption rebuilds the database: no backup may read them meanwhile.
                using var exclusive = await jobs.TryBeginExclusiveAsync(_cutoverDrainWait)
                    ?? throw new BlindSeedRejectedException(BlindSeedRefusal.DrainTimeout);
                // Order matters (review round): everything that can be refused is decided before this node's live tables change,
                // and the serving node's position - the record "everything up to here is stored" - moves last. The blobs come
                // first because the baseline's adoption needs them here; they are content-addressed and checked against
                // their names, and a surplus from a refused package is swept after the grace period (9.6). The reader has
                // validated the package's content, rows of control.db included (ControlDbValidator); what only this node can tell
                // - a control database its schema cannot import, the grant time a first seed would have to take - is tried first
                // (the import rolled back, the grant time computed), so no deterministic failure is left for after the adoption.
                if (manifest.Kind == BlindPackageKinds.Full)
                {
                    try
                    {
                        ImportControlTables(sp.GetRequiredService<DbConnectionFactory>(), package.ControlDbPath!, commit: false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
                    {
                        throw new BlindSeedRejectedException(BlindSeedRefusal.ControlDatabaseInvalid, ex);
                    }
                    if (upload.Authority is BlindSeedAuthority.PairingSecret)
                        await PreflightGrantTimeAsync(package, sp.GetRequiredService<IDbConnectionFactory>());
                    await ImportBlobsAsync(package, sp);
                }

                // The position of THIS node's log the baseline covers: how far its producer pulled from it (9.2).
                var localCut = package.Baseline.Manifest.PullPositionOf(self.NodeId);
                AdoptResult adopted;
                try
                {
                    adopted = await sp.GetRequiredService<IBaselineStore>().AdoptAsync(package.Baseline, localCut, producerAuthorized: true, ct);
                }
                catch (Exception ex) when (ex is IOException or SqliteException)
                {
                    // The baseline is current, but the purge of the earlier readable content did not finish; the next adoption
                    // (this baseline again is enough) and the log trimmer finish it, and the status says so until then.
                    logger.LogError(ex, "Blind seed {SeedId}: the baseline was adopted but the purge of the earlier content did not finish", manifest.SeedId);
                    throw new BlindSeedRejectedException(BlindSeedRefusal.BaselineCleanupIncomplete, ex);
                }
                if (adopted.Outcome == AdoptOutcome.Refused)
                {
                    // The check that refused it is this node's own sentence (BaselineStore): for the operator's log, not for the sender.
                    logger.LogInformation("Blind seed {SeedId}: the baseline was refused: {Reason}", manifest.SeedId, adopted.Reason);
                    throw new BlindSeedRejectedException(BlindSeedRefusal.BaselineRefused);
                }

                if (manifest.Kind == BlindPackageKinds.Full)
                {
                    await ImportFullAsync(package, self, upload.Authority, sp);
                    await StoreDeltasAsync(package, servingNode, sp, ct);
                    await AdvancePositionsAsync(package, self, sp);
                }

                // A format-2 adoption answers the reseed request (the activation's, or a restore's).
                using (var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
                    await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = 'reseed_needed'");
                logger.LogInformation(
                    "Blind seed {SeedId} (format 2, {Kind}) from {Producer} applied: baseline {BaselineId} {Outcome}, local cut {Cut}, {Deltas} deltas",
                    manifest.SeedId, manifest.Kind, manifest.ProducerNodeId, manifest.BaselineId, adopted.Outcome, localCut, manifest.DeltaCount);
            }
            finally
            {
                HeavyOperationLock.Instance.Release();
            }
        }

        if (upload.Authority is BlindSeedAuthority.PairingSecret)
            await sp.GetRequiredService<BlindPairing>().ConsumeAsync();
    }

    /// <summary>The serving node and the key its package must be signed with, as CheckProducerAsync decides it for format 1.</summary>
    private static async Task<(Guid Node, byte[] Key)> ServingNodeAsync(
        BlindSeedAuthority authority, NodeIdentity self, IWhitelistRepository whitelist)
    {
        switch (authority)
        {
            case BlindSeedAuthority.SuperadminPeer peer:
                var row = await whitelist.GetByNodeIdAsync(peer.NodeId);
                if (row is not { IsSuperadmin: true })
                    throw new BlindSeedRejectedException(BlindSeedRefusal.NotSuperadminPeer);
                return (peer.NodeId, row.Ed25519PublicKey);
            case BlindSeedAuthority.PairingSecret seeder:
                if (BlindNodeId.IsBlind(seeder.SeederNodeId))
                    throw new BlindSeedRejectedException(BlindSeedRefusal.BlindCannotSeed);
                if (self.NodeId == seeder.SeederNodeId)
                    throw new BlindSeedRejectedException(BlindSeedRefusal.CannotSeedItself);
                return (seeder.SeederNodeId, Convert.FromBase64String(seeder.SeederKeyB64));
            default:
                throw new BlindSeedRejectedException(BlindSeedRefusal.UnknownAuthority);
        }
    }

    /// <summary>
    /// The baseline producers this node trusts (spec 8.6 step 3): its own active whitelist for a reseed; for a first seed the
    /// paired seeder as superadmin and the rows of the package's whitelist (signed by the seeder the pair code vouched for, and
    /// accepted only when the package verifies), never a blind node.
    /// </summary>
    private static async Task<Dictionary<Guid, (byte[] PublicKey, bool IsSuperadmin)>> TrustedProducersAsync(
        Upload upload, IWhitelistRepository whitelist, Guid servingNode, byte[] servingKey, CancellationToken ct)
    {
        var trusted = new Dictionary<Guid, (byte[], bool)>();
        if (upload.Authority is BlindSeedAuthority.SuperadminPeer)
        {
            foreach (var row in await whitelist.GetAllActiveAsync())
                trusted[row.NodeId] = (row.Ed25519PublicKey, row.IsSuperadmin && !BlindNodeId.IsBlind(row.NodeId));
            return trusted;
        }
        if (await Format2Package.ReadEntryAsync(upload.Path, BlindManifestV2.FileName, MaximumManifestBytes, ct) is { } bytes)
        {
            try
            {
                foreach (var peer in BlindManifestV2.Parse(bytes).Whitelist)
                    if (!BlindNodeId.IsBlind(peer.NodeId))
                        trusted[peer.NodeId] = (Convert.FromBase64String(peer.PublicKeyB64), peer.IsSuperadmin);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException)
            {
                // The reader refuses the package with the precise reason.
            }
        }
        trusted[servingNode] = (servingKey, true);
        return trusted;
    }

    private static async Task CheckSchemaV2Async(string packageDir, IServiceProvider services)
    {
        using var outer = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(packageDir, Format2Package.ManifestFileName)));
        var version = outer.RootElement.TryGetProperty("migrationVersion", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetInt32() : 0;
        using var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var local = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM tbl_migration");
        if (version > local)
            throw new BlindSeedRejectedException(BlindSeedRefusal.SchemaMismatch);
    }

    /// <summary>
    /// What a full package brings besides the baseline, once the baseline is adopted: the whitelist rows this node lacks (a
    /// first seed's producer is granted through its signed standing and the pair code, as for format 1) and the clear control
    /// tables (insert-or-ignore, spec 8.4). Blobs and positions have their own steps (<see cref="ImportBlobsAsync"/>,
    /// <see cref="AdvancePositionsAsync"/>).
    /// </summary>
    private async Task ImportFullAsync(VerifiedPackageV2 package, NodeIdentity self, BlindSeedAuthority authority, IServiceProvider sp)
    {
        var manifest = package.Manifest;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var firstSeed = authority is BlindSeedAuthority.PairingSecret;
        foreach (var peer in manifest.Whitelist.Where(p => p.NodeId != self.NodeId && !(firstSeed && p.NodeId == manifest.ProducerNodeId)))
        {
            if (await whitelist.GetByNodeIdAsync(peer.NodeId, includeDeleted: true) is not null) continue;
            await whitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = peer.NodeId,
                DisplayName = peer.DisplayName,
                Ed25519PublicKey = Convert.FromBase64String(peer.PublicKeyB64),
                ApiAddress = BeeMemoryBank.Sync.PeerAddressPolicy.ForStorage(peer.ApiAddress),
                IsSuperadmin = peer.IsSuperadmin && !BlindNodeId.IsBlind(peer.NodeId),
                TlsSpki = BlindTrust.PinOf(peer.TlsTrust, peer.TlsSpki),
                TlsTrust = BlindTrust.Effective(peer.TlsTrust, peer.TlsSpki),
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LamportTs = peer.LamportTs,
                SourceNodeId = peer.SourceNodeId,
            });
        }

        ImportControlTables(sp.GetRequiredService<DbConnectionFactory>(), package.ControlDbPath!);
        using (var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation") > 0)
                sp.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();

        if (authority is BlindSeedAuthority.PairingSecret seeder)
        {
            var claimed = manifest.Whitelist.Single(p => p.NodeId == manifest.ProducerNodeId);
            var grantAt = GrantTime(await MaxLamportHeldAsync(sp.GetRequiredService<IDbConnectionFactory>()), manifest.Standing);
            await ApplyFirstSeedStandingAsync(manifest.ProducerNodeId, claimed, manifest.Standing, seeder.SeederKeyB64, grantAt);
        }
    }

    /// <summary>
    /// A first seed's grant time (<see cref="GrantTime"/>) computed ahead of the adoption, over everything that will be held when
    /// the grant is taken: this node's rows in the tables the purge leaves alone, the package's peers and <c>control.db</c> rows,
    /// and its standing events. The same function, so a seed that leaves no time to grant is refused before the baseline is adopted
    /// and the legacy content purged, not after. It over-counts what the import skips (a peer already here), never under-counts.
    /// </summary>
    private static async Task PreflightGrantTimeAsync(VerifiedPackageV2 package, IDbConnectionFactory live)
    {
        long controlMax;
        try
        {
            controlMax = ControlDbValidator.Validate(package.ControlDbPath!);
        }
        catch (InvalidDataException ex)
        {
            throw new BlindSeedRejectedException(BlindSeedRefusal.PackageV2Invalid, ex);
        }
        var held = await MaxLamportHeldAsync(live, afterPurge: true);
        GrantTime(Math.Max(Math.Max(held, controlMax), package.Manifest.Whitelist.Max(p => p.LamportTs)), package.Manifest.Standing);
    }

    /// <summary>The blobs of a full package, each checked against its name, into the blob store (before the baseline is adopted: it needs them).</summary>
    private static async Task ImportBlobsAsync(VerifiedPackageV2 package, IServiceProvider sp)
    {
        if (package.BlobsDirectory is not { } blobs || !Directory.Exists(blobs)) return;
        var repo = sp.GetRequiredService<IBlobRepository>();
        foreach (var file in Directory.EnumerateFiles(blobs))
        {
            var data = await File.ReadAllBytesAsync(file);
            if (!BlobHash.Matches(Path.GetFileName(file), data))
                throw new BlindSeedRejectedException(BlindSeedRefusal.BlobMismatch);
            await repo.StoreAsync(data);
        }
    }

    /// <summary>
    /// The serving node's own log is in this node up to cp_sequence now (baseline + deltas); its pull positions are this node's
    /// content's. Done last, after every delta is stored: a later ordinary pull starts after these positions, so they must not
    /// name a row that was not stored. Never lowered: a reseed of a running node keeps what it already pulled.
    /// </summary>
    private static async Task AdvancePositionsAsync(VerifiedPackageV2 package, NodeIdentity self, IServiceProvider sp)
    {
        var manifest = package.Manifest;
        var positions = sp.GetRequiredService<ISyncPositionRepository>();
        var current = (await positions.GetAllAsync()).ToDictionary(p => p.RemoteNodeId, p => p.LastSequenceNum);
        var wanted = manifest.Positions.Where(p => p.RemoteNodeId != self.NodeId)
            .Select(p => (p.RemoteNodeId, p.LastSequence))
            .Append((manifest.ProducerNodeId, manifest.CpSequence));
        foreach (var (node, seq) in wanted)
            if (!current.TryGetValue(node, out var have) || have < seq)
                await positions.UpsertAsync(new SyncPosition { RemoteNodeId = node, LastSequenceNum = seq, UpdatedAt = DateTime.UtcNow });
    }

    /// <summary>
    /// <c>control.db</c>'s tables into this node's, row for row, a key already here kept (spec 8.4). With <paramref name="commit"/>
    /// false the same import runs and is rolled back: the preflight that finds a control database this node cannot take
    /// (tables sharing no column with its schema) before the baseline is adopted, not after.
    /// </summary>
    private static void ImportControlTables(DbConnectionFactory live, string controlDbPath, bool commit = true)
    {
        using var conn = (SqliteConnection)live.CreateConnection();
        conn.Execute("PRAGMA foreign_keys = OFF");
        conn.Execute($"ATTACH DATABASE '{controlDbPath.Replace("'", "''")}' AS snap");
        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var table in SnapshotTables.Control)
                if (SnapshotTableImport.SnapshotHasTable(conn, tx, table))
                    SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            if (commit) tx.Commit();
            else tx.Rollback();
        }
        finally
        {
            conn.Execute("DETACH DATABASE snap");
            conn.Execute("PRAGMA foreign_keys = ON");
        }
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={controlDbPath};Pooling=False"));
    }

    /// <summary>
    /// The deltas in the serving node's order, each through the applier in relay mode with its origin (spec 8.5): signatures
    /// checked against this node's whitelist; one that can never apply is quarantined as ordinary sync would; one that could
    /// apply later fails the install, so the sender sends it again.
    /// </summary>
    private async Task StoreDeltasAsync(VerifiedPackageV2 package, Guid servingNode, IServiceProvider sp, CancellationToken ct)
    {
        var applier = sp.GetRequiredService<EventApplier>();
        var quarantine = sp.GetRequiredService<ISyncQuarantineRepository>();
        await sp.GetRequiredService<CallerScopeHolder>().RunAsSystemAsync(async () =>
        {
            await foreach (var evt in Format2Package.ReadDeltasAsync(package.DeltasPath!, ct))
            {
                try
                {
                    await applier.ApplyAsync(evt, new ApplyContext(ReceivedFrom: servingNode, ReceivedSeq: evt.SequenceNum));
                }
                catch (Exception ex) when (SyncFailureClassifier.Classify(ex) == SyncFailureKind.Permanent)
                {
                    // The quarantine list is shown on a screen: the kind of failure, never the exception's own text.
                    await quarantine.RecordFailureAsync(evt.EventId, evt.EventType, evt.NodeId,
                        $"Blind format-2 seed: {ex.GetType().Name}", SyncFailureKind.Permanent);
                    logger.LogWarning(ex, "Blind seed: delta {EventId} ({Type}) can never apply; quarantined", evt.EventId, evt.EventType);
                }
                catch (Exception ex)
                {
                    throw new BlindSeedRejectedException(BlindSeedRefusal.DeltaNotStored, ex);
                }
            }
            return true;
        });
    }
}
