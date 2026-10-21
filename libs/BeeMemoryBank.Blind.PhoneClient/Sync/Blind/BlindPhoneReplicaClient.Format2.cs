using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The format-2 half of the replica client (docs/design/step-a-spec.md 13.1, 13.3): a phone or desktop blind copy that has activated Step A takes
/// a sealed baseline from the node it calls instead of a database. It never opens anything: the install is signatures, hashes, rows and files.
/// </summary>
public sealed partial class BlindPhoneReplicaClient
{
    private const long MaximumBlindManifestBytes = 4L * 1024 * 1024;

    private IServiceScopeFactory Scopes() => scopes
        ?? throw new InvalidOperationException("This replica client was built without the services a format-2 install needs.");

    /// <summary>The protocol this copy declares (spec 5.6): 3, or 4 once it has applied the activation; null when the client has no host services.</summary>
    private int? DeclaredProtocol()
    {
        if (scopes is null) return null;
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ISyncProtocolPolicy>()?.Declared;
    }

    /// <summary>The id of the baseline this copy holds, or null when it holds none (or cannot tell).</summary>
    private async Task<string?> HeldBaselineIdAsync(CancellationToken ct)
    {
        if (scopes is null) return null;
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetService<IBaselineStore>();
        return store is null ? null : (await store.GetCurrentAsync(ct))?.Files.Manifest.BaselineId.ToString();
    }

    /// <summary>Whether the package at <paramref name="archivePath"/> is format 2, by the format its blind manifest names (nothing in it is trusted yet).</summary>
    private static async Task<bool> IsFormat2Async(string archivePath, CancellationToken ct)
    {
        try
        {
            var bytes = await Format2Package.ReadEntryAsync(archivePath, BlindManifestV2.FileName, MaximumBlindManifestBytes, ct);
            if (bytes is null) return false;
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String && format.GetString() == BlindManifestV2.CurrentFormat;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Spec 8.6, steps 1-4, by the format-2 reader: the serving node's signatures under the call code's key, every entry's hash, the baseline's
    /// producer and signature, its container and blob list. The producer must be a superadmin this copy trusts: the one in its own whitelist,
    /// or for a first install (no baseline held yet) the one in the package's own whitelist, which the call code's node signed.
    /// </summary>
    private async Task<VerifiedPackageV2> VerifyFormat2Async(
        string archivePath, string extraction, PackageMetadata metadata, BlindCallCode target, CancellationToken ct)
    {
        using var scope = Scopes().CreateScope();
        return await VerifyFormat2Async(scope.ServiceProvider, archivePath, extraction, metadata, target, ct);
    }

    private async Task<VerifiedPackageV2> VerifyFormat2Async(
        IServiceProvider sp, string archivePath, string extraction, PackageMetadata metadata, BlindCallCode target, CancellationToken ct)
    {
        var claimed = BlindManifestV2.Parse(await Format2Package.ReadEntryAsync(archivePath, BlindManifestV2.FileName, MaximumBlindManifestBytes, ct)
            ?? throw new InvalidDataException("Replica package has no blind-manifest.json."));
        var heldBaseline = sp.GetService<IBaselineStore>() is { } store && await store.GetCurrentAsync(ct) is not null;
        Dictionary<Guid, (byte[] PublicKey, bool IsSuperadmin)> trusted;
        if (heldBaseline)
            trusted = (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
                .GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => (g.First().Ed25519PublicKey, g.First().IsSuperadmin));
        else
            trusted = claimed.Whitelist.GroupBy(p => p.NodeId)
                .ToDictionary(g => g.Key, g => (Convert.FromBase64String(g.First().PublicKeyB64), g.First().IsSuperadmin));

        var reader = sp.GetService<IBlindPackageV2Reader>() ?? new BlindPackageV2Reader();
        var verified = await reader.ExtractVerifiedAsync(archivePath, Convert.FromBase64String(metadata.SignatureB64), target.PublicKey,
            id => trusted.TryGetValue(id, out var row) ? row : null, extraction, ct);
        if (verified.Manifest.ProducerNodeId != target.NodeId)
            throw new InvalidDataException("Replica package producer does not match the call-code node.");
        return verified;
    }

    /// <summary>
    /// Installs a verified format-2 package in place (spec 13.1): the whitelist rows, the control rows and the blobs of a <c>full</c> package, or
    /// the blobs fetched from the node for a <c>baseline</c> one; the baseline adopted (the first adoption purges what a format-1 replica
    /// held in clear); the deltas stored through the relay applier with the node's sequence numbers; the pull position at the package's
    /// <c>cp_sequence</c>; and what the baseline covers of this copy's log trimmed (13.3), then the blobs nothing needs any more collected.
    /// Every step can be run again: a copy that stopped half way finishes on the next try, and the first load stays "not done" until then.
    /// </summary>
    private async Task InstallFormat2Async(
        HttpClient http, string token, string archivePath, string extraction, PackageMetadata metadata, BlindCallCode target, CancellationToken ct)
    {
        using var scope = Scopes().CreateScope();
        var sp = scope.ServiceProvider;
        var verified = await VerifyFormat2Async(sp, archivePath, extraction, metadata, target, ct);
        var manifest = verified.Manifest;
        var store = sp.GetRequiredService<IBaselineStore>();
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var blobs = sp.GetRequiredService<IBlobRepository>();
        var self = await identityRepo.GetAsync() ?? throw new BlindSentenceException("Blind phone identity disappeared during install.");

        // No write lands in the middle of this: the pull sync and the applier wait, and this flow writes through.
        using var writeGate = await EventWriteGate.Instance.QuiesceAsync(ct);
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();

        if (manifest.Kind == BlindPackageKinds.Full)
        {
            foreach (var peer in manifest.Whitelist.Where(p => p.NodeId != self.NodeId))
            {
                if (await whitelist.GetByNodeIdAsync(peer.NodeId, includeDeleted: true) is not null) continue;
                await whitelist.CreateAsync(new WhitelistEntry
                {
                    NodeId = peer.NodeId,
                    DisplayName = peer.DisplayName,
                    Ed25519PublicKey = Convert.FromBase64String(peer.PublicKeyB64),
                    ApiAddress = BeeMemoryBank.Sync.PeerAddressPolicy.ForStorage(peer.ApiAddress),
                    IsSuperadmin = peer.IsSuperadmin,
                    TlsSpki = BlindTrust.PinOf(peer.TlsTrust, peer.TlsSpki),
                    TlsTrust = BlindTrust.Effective(peer.TlsTrust, peer.TlsSpki),
                    Status = WhitelistStatuses.Active,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LamportTs = peer.LamportTs,
                    SourceNodeId = peer.SourceNodeId,
                });
            }
            if (verified.ControlDbPath is not null)
            {
                ImportControlTables(verified.ControlDbPath);
                // The activation arrived as a row, not as the event the applier flips the policy on: this copy declares 4 from now on.
                using var check = liveFactory.CreateConnection();
                if (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation") > 0)
                    sp.GetService<ISyncProtocolPolicy>()?.MarkActivated();
            }
            if (verified.BlobsDirectory is not null && Directory.Exists(verified.BlobsDirectory))
                foreach (var file in Directory.EnumerateFiles(verified.BlobsDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    await blobs.StoreAsync(await File.ReadAllBytesAsync(file, ct));
                    AfterBatch?.Invoke();
                }
        }

        // Every blob the baseline lists must be here before it is adopted (spec 9.2 step 3): a baseline package carries none, a full package
        // from a node that did not ship its blobs carries none either; the copy fetches what it lacks from the node that serves it.
        var listed = Format2Package.ParseBlobList(await File.ReadAllBytesAsync(
            Path.Combine(verified.Baseline.Directory, BaselineManifest.BlobListFileName), ct));
        var unavailable = await BlobTransport.FetchHashesAsync(http, target.Address.TrimEnd('/'), token, listed, blobs, logger, ct, AfterBatch);
        if (unavailable > 0)
            throw new InvalidDataException($"{unavailable} of the {listed.Count} blobs the baseline lists are not available from the node.");

        // The producer is a superadmin of this copy's own whitelist by now (spec 9.2 step 1): the store checks its row and signature again.
        var producer = await whitelist.GetByNodeIdAsync(verified.Baseline.Manifest.ProducerNodeId);
        var adopted = await store.AdoptAsync(verified.Baseline, manifest.CutInServerLog, producer is { IsSuperadmin: true }, ct);
        if (adopted.Outcome == AdoptOutcome.Refused)
            throw new InvalidDataException($"The copy refused the baseline: {adopted.Reason}");

        if (manifest.Kind == BlindPackageKinds.Full)
        {
            if (verified.DeltasPath is not null)
            {
                var applier = sp.GetRequiredService<EventApplier>();
                await foreach (var evt in Format2Package.ReadDeltasAsync(verified.DeltasPath, ct))
                {
                    try
                    {
                        await applier.ApplyAsync(evt, new ApplyContext(ReceivedFrom: target.NodeId, ReceivedSeq: evt.SequenceNum));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Spec 8.5: a delta that cannot be stored (an originator this copy does not know, a signature that fails) fails the install.
                        throw new InvalidDataException($"Delta {evt.EventId} cannot be stored ({ex.GetType().Name})", ex);
                    }
                }
            }
            await sp.GetRequiredService<ISyncPositionRepository>().UpsertAsync(new SyncPosition
            {
                RemoteNodeId = target.NodeId, LastSequenceNum = manifest.CpSequence, UpdatedAt = DateTime.UtcNow,
            });
        }

        // This copy has no readers of its log: what the baseline covers of the node's log is the only bound (spec 13.3).
        using (var conn = liveFactory.CreateConnection())
            await conn.ExecuteAsync(
                "DELETE FROM tbl_event WHERE origin_peer = @peer COLLATE NOCASE AND origin_seq <= @cut",
                new { peer = target.NodeId.ToString(), cut = manifest.CutInServerLog });
        await store.CollectGarbageAsync(ct);
    }

    /// <summary>The control tables of <paramref name="controlDb"/> (spec 8.4) into the live database, insert-or-ignore by key.</summary>
    private void ImportControlTables(string controlDb)
    {
        using var conn = (SqliteConnection)liveFactory.CreateConnection();
        conn.Execute("PRAGMA foreign_keys = OFF");
        conn.Execute($"ATTACH DATABASE '{controlDb.Replace("'", "''")}' AS snap");
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in SnapshotTables.Control)
                if (SnapshotTableImport.SnapshotHasTable(conn, tx, table))
                    SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            conn.Execute("DETACH DATABASE snap");
            conn.Execute("PRAGMA foreign_keys = ON");
        }
    }
}
