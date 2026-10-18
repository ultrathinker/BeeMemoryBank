using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.Recovery;

public partial class RecoveryRestoreService
{
    private static readonly IBlindPackageV2Reader PackageV2Reader = new BlindPackageV2Reader();

    /// <summary>The blind manifest is small; anything larger is not read.</summary>
    private const long MaximumBlindManifestBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Whether the package at <paramref name="archivePath"/> is a format-2 one (docs/design/step-a-spec.md 8.2), decided by the format its
    /// blind manifest names; nothing in it is trusted yet. A package that cannot be read at all is not format 2 either: the format-1 path
    /// then names what is wrong with it.
    /// </summary>
    public static async Task<bool> IsFormat2PackageAsync(string archivePath, CancellationToken ct = default)
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
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The package a blind node handed over, in whichever format it is: a format-2 one restores from its baseline and deltas
    /// (<see cref="RestoreFromBaselineAsync"/>); a format-1 one holds the vault in the open and is refused by <see cref="RestoreFromPackageAsync"/> (BMB-306).
    /// A node that has activated Step A serves format 2 only.
    /// </summary>
    public async Task<RestoreResult> RestoreFromBlindPackageAsync(
        string archivePath, byte[] detachedSignature, byte[] servingNodePublicKey, RestoreIdentity who, RestoreBlindPeer blind,
        IReadOnlyList<SyncEvent> events, CancellationToken ct = default, RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        if (!await IsFormat2PackageAsync(archivePath, ct))
            return await RestoreFromPackageAsync(archivePath, detachedSignature, servingNodePublicKey, who, blind, events, ct, boxes);
        return await RestoreFromPackageV2Async(archivePath, detachedSignature, servingNodePublicKey, who, blind, events, ct, boxes);
    }

    /// <summary>
    /// A format-2 <c>full</c> package: the reader makes the checks of spec 8.6 (the serving node's signatures against the key the caller
    /// got out of band, every entry's hash, the baseline's signature, its container and blob list) before anything is looked at; the
    /// baseline's producer must be a superadmin of the package's own whitelist, which the serving node signed (the restore code's
    /// identity is the trust root here, as for a format-1 package). Then the restore proper opens the baseline with the recovered keys.
    /// </summary>
    public async Task<RestoreResult> RestoreFromPackageV2Async(
        string archivePath, byte[] detachedSignature, byte[] servingNodePublicKey, RestoreIdentity who, RestoreBlindPeer blind,
        IReadOnlyList<SyncEvent> events, CancellationToken ct = default, RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        BlindManifestV2 claimed;
        try
        {
            claimed = BlindManifestV2.Parse(await Format2Package.ReadEntryAsync(archivePath, BlindManifestV2.FileName, MaximumBlindManifestBytes, ct)
                ?? throw new InvalidDataException("The package has no blind-manifest.json."));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The package's blind manifest is not readable.", ex);
        }
        var directory = Path.Combine(Path.GetTempPath(), $"bmb-blind-restore-v2-{Guid.NewGuid():N}");
        try
        {
            var verified = await PackageV2Reader.ExtractVerifiedAsync(archivePath, detachedSignature, servingNodePublicKey,
                node => claimed.Whitelist.SingleOrDefault(p => p.NodeId == node) is { } row
                    ? (Convert.FromBase64String(row.PublicKeyB64), row.IsSuperadmin)
                    : null,
                directory, ct);
            using var source = new PackageRestoreSource(verified);
            return await RestoreFromBaselineAsync(source, who, blind, events, ct, boxes);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch (IOException) { /* a temp folder */ }
        }
    }

    /// <summary>
    /// Restore from a baseline and the deltas after it (docs/design/step-a-spec.md 12): keys from the control tables and the password
    /// (nothing is written until they open the baseline), then the baseline, the control tables and the blobs are imported, the identity
    /// and admin are made, the deltas are replayed through the applier, and the anchor is checked after the replay. A blind node that
    /// held deltas back shows as an anchor that does not match.
    /// </summary>
    /// <param name="source">A package, an Android blind backup or a backup folder, already read and checked by its own reader.</param>
    /// <param name="blind">The blind node the restore came from, when it came from one; its position is the package's log head.</param>
    /// <param name="extraEvidence">Signed events the source also handed over (the blind node's restore route): anchors older than the cut.</param>
    /// <param name="recoverySet">The recovery set that lies next to a backup, when there is one; otherwise the source's control tables hold it.</param>
    public async Task<RestoreResult> RestoreFromBaselineAsync(
        IRestoreSourceV2 source, RestoreIdentity who, RestoreBlindPeer? blind, IReadOnlyList<SyncEvent> extraEvidence,
        CancellationToken ct = default, RestoreBoxPolicy boxes = RestoreBoxPolicy.Default, RecoverySet? recoverySet = null)
    {
        var manifest = source.Manifest;
        var baseline = source.Baseline.Manifest;

        // The serving node signs the package, the baseline's producer its baseline: a producer that is not a superadmin in the package's
        // own whitelist cannot vouch for a baseline (spec 8.6 step 3, 9.2).
        if (manifest.Whitelist.GroupBy(p => p.NodeId).Any(g => g.Count() > 1))
            throw new InvalidDataException("The package's manifest lists a node twice.");
        if (blind is not null && manifest.ProducerNodeId != blind.NodeId)
            throw new InvalidDataException("The package was not built by the node it came from.");
        if (manifest.Whitelist.SingleOrDefault(p => p.NodeId == baseline.ProducerNodeId) is not { IsSuperadmin: true })
            throw new InvalidDataException("The baseline was not produced by a superadmin of this network; it cannot be restored from.");
        if (manifest.Whitelist.Any(p => p.LamportTs < 0))
            throw new InvalidDataException("The package's manifest carries a whitelist row with a negative version.");

        string dataPath;
        using (var pathScope = scopes.CreateScope())
            dataPath = pathScope.ServiceProvider.GetRequiredService<SnapshotService>().DataPath;
        using var run = new BaselineRestoreRun(source, dataPath, logger);

        recoverySet ??= await run.ReadRecoverySetAsync();
        // The evidence an anchor is checked against: the deltas that are not sealed (anchors and the other control events, signed
        // in clear) and what the source also sent.
        var evidence = new RestoreEvidence(RestoreEvidenceFromManifestV2.Of(manifest), [.. run.ClearDeltas(), .. extraEvidence]);

        // The pull positions of the signed baseline, and the baseline's producer at the head of its own log it covers.
        var positions = baseline.PullPositions
            .Where(p => p.RemoteNodeId != manifest.ProducerNodeId)
            .GroupBy(p => p.RemoteNodeId).ToDictionary(g => g.Key, g => g.Max(p => p.LastSequence));
        positions[baseline.ProducerNodeId] = Math.Max(positions.GetValueOrDefault(baseline.ProducerNodeId), baseline.ProducerLogHead);

        var restored = await RestoreCoreAsync(
            databaseForKeys: "", recoverySet, who, blind is null ? null : blind with { CpSeq = manifest.CpSequence },
            evidence, sp => run.ImportAsync(sp, ct), ct, boxes, run, positions);
        // The decrypted baseline goes before the restore is reported: a copy that stays makes it repair-required.
        run.RemoveDatabase();
        return restored;
    }
}
