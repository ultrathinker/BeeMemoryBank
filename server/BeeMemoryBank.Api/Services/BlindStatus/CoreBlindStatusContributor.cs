using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services.BlindStatus;

/// <summary>
/// The base fields of <c>GET /api/blind/status</c> that belong to no subsystem: what this node is
/// (version, protocol, id, role), whom it talks to (<c>peers</c>), where its data lives and how
/// much of it there is. Everything else is a section another contributor adds.
///
/// <para>No note count (owner decision 6, step-a-spec 10.1): from 2.6.0 on, activated or not, <c>stored</c> says how many
/// blobs and bytes the node keeps, and a blind host adds its baseline (age, key generation) and its deltas instead. The
/// protocol is the one this node declares (<see cref="ISyncProtocolPolicy"/>: 3, or 4 once activated).</para>
///
/// <para>The role comes from the DI-registered <see cref="INodeRole"/> when there is one, and
/// from the environment otherwise: that registration is part of the blind-role wiring (BMB-52)
/// and must not be a load-bearing dependency of this contributor — a status endpoint that 500s
/// because a role service was not registered yet is strictly worse than one that answers
/// honestly.</para>
/// </summary>
public sealed class CoreBlindStatusContributor(
    INodeIdentityRepository nodeRepo,
    IWhitelistRepository whitelistRepo,
    ISyncPositionRepository syncPositionRepo,
    ISyncPushPositionRepository pushPositionRepo,
    IEventLogRepository eventLogRepo,
    IDbConnectionFactory connFactory,
    IBlobRepository blobRepo,
    INodeRole? role = null,
    string? dataPath = null,
    ISyncProtocolPolicy? protocolPolicy = null,
    IBlindStoreSummarySource? storeSummary = null,
    IBaselineStore? baselineStore = null) : IBlindStatusContributor
{
    private readonly INodeRole _role = role ?? new EnvironmentNodeRole();
    private readonly string _dataPath =
        dataPath ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH") ?? Directory.GetCurrentDirectory();

    public async Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var identity = await nodeRepo.GetAsync();
        var whitelist = await whitelistRepo.GetAllActiveAsync();

        b.Set("version", AppVersion.Current);
        b.Set("protocol", protocolPolicy?.Declared ?? SyncProtocolVersion.Current);
        b.Set("activated", protocolPolicy?.SealsMetadata ?? false);
        b.Set("node_id", identity?.NodeId.ToString());
        b.Set("node_name", identity?.DisplayName);
        b.Set("role", _role.IsBlind ? "blind" : "full");
        b.Set("data_path", _dataPath);
        // backups_path belongs to BlindBackupStatusContributor, which always sets it (null when
        // no repository is configured).

        await AddPeersAsync(b, whitelist, identity?.NodeId);
        await AddStorageAsync(b);
        if (storeSummary is not null)
            await AddStoreAsync(b, storeSummary, ct);
    }

    private async Task AddPeersAsync(BlindStatusBuilder b, List<WhitelistEntry> whitelist, Guid? selfId)
    {
        // Both position tables, because contact is bidirectional: peers pull from a blind node, and
        // the blind node itself calls the listening hubs (plan §4.5). "Last contact" is whichever
        // side talked more recently; "lag" is how many events the peer has not pulled from us yet —
        // the same number /api/sync/delivery-status computes, so the two views cannot disagree.
        var pullPositions = (await syncPositionRepo.GetAllAsync())
            .ToDictionary(p => p.RemoteNodeId);
        var pushPositions = (await pushPositionRepo.GetAllActivePeersWithPushPositionsAsync())
            .ToDictionary(p => p.NodeId);

        foreach (var entry in whitelist.Where(w => w.NodeId != selfId))
        {
            // Value tuples: TryGetValue's success flag is the null marker, not the out value.
            var havePull = pullPositions.TryGetValue(entry.NodeId, out var ourPull);
            var havePush = pushPositions.TryGetValue(entry.NodeId, out var theirPull);

            DateTime? lastContact = havePull ? ourPull!.UpdatedAt : null;
            if (havePush && theirPull.PushedAt is { } pushed && (lastContact is null || pushed > lastContact))
                lastContact = pushed;

            long? lag = null;
            if (havePush && theirPull.LastPushedSeq is { } lastPulled)
                lag = await eventLogRepo.CountEventsAfterSequenceAsync(lastPulled);

            b.AddPeer(new BlindPeerStatus(
                entry.NodeId.ToString(),
                entry.DisplayName,
                lastContact,
                lag));
        }
    }

    private async Task AddStorageAsync(BlindStatusBuilder b)
    {
        var (blobCount, blobBytes) = await blobRepo.GetStatsAsync();
        var dbBytes = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
        var mediaBytes = DirSize(Path.Combine(_dataPath, "media"));

        b.Set("stored", new Dictionary<string, object?>
        {
            // What this NODE stores, whatever the asking caller may read: counted from the tables, never through a
            // caller's scope. No note count (step-a-spec 10.1): how many notes a vault holds is metadata a blind copy
            // must not show, from 2.6.0 on, activated or not.
            ["blobs"] = blobCount,
            // Everything the node stores for the mesh: the vault database, the blob table's
            // ciphertext and the encrypted media files. The console shows this as "Stored".
            ["bytes"] = dbBytes + blobBytes + mediaBytes,
        });
        b.Set("free_bytes", new DriveInfo(Path.GetFullPath(_dataPath)).AvailableFreeSpace);
    }

    /// <summary>
    /// A blind host's store (step-a-spec 10.1): the current format-2 baseline (null until one is adopted), the deltas held
    /// beside it, and the copies from before the first adoption that still hold plaintext metadata (the owner retires
    /// them; nothing here deletes them).
    /// </summary>
    private async Task AddStoreAsync(BlindStatusBuilder b, IBlindStoreSummarySource source, CancellationToken ct)
    {
        var s = await source.GetSummaryAsync(ct);
        var current = baselineStore is null ? null : await baselineStore.GetCurrentAsync(ct);
        b.Set("baseline", current?.Files.Manifest is { } m
            ? new Dictionary<string, object?>
            {
                ["id"] = m.BaselineId.ToString(),
                ["producer"] = m.ProducerNodeId.ToString(),
                ["created_at"] = m.CreatedAt,
                ["age_seconds"] = (long)Math.Max(0, (DateTime.UtcNow - m.CreatedAt.ToUniversalTime()).TotalSeconds),
                ["key_generation"] = m.KeyGeneration,
                ["container_bytes"] = m.ContainerBytes,
            }
            : null);
        b.Set("deltas", new Dictionary<string, object?> { ["count"] = s.DeltaCount, ["bytes"] = s.DeltaBytes });
        b.Set("legacy_copies", LegacyCopies());
        // Until the first format-2 adoption purged the legacy content (spec 9.3, 9.8), the database a backup copies may
        // still hold plaintext metadata.
        using var conn = connFactory.CreateConnection();
        b.Set("metadata_in_backups", await Dapper.SqlMapper.ExecuteScalarAsync<string?>(
            conn, "SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'") != "1");
    }

    /// <summary>
    /// Copies of this node's database from before its first format-2 adoption that may still hold plaintext metadata
    /// (step-a-spec 9.3): a <c>.pre-seed</c> copy an older build kept, and the old database of a cutover that never
    /// finished. Reported, never deleted.
    /// </summary>
    private List<string> LegacyCopies()
    {
        var found = new List<string>();
        foreach (var path in new[]
                 {
                     DbPath + ".pre-seed", DbPath + ".pre-seed-wal",
                     Path.Combine(_dataPath, "blind-cutover", "old.db"),
                 })
            if (File.Exists(path)) found.Add(path);
        return found;
    }

    private string DbPath => Path.Combine(_dataPath, "beememorybank.db");

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* a file being written or swept mid-walk is not worth failing the status over */ }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }
}
