using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Services.BlindStatus;

/// <summary>
/// The base fields of <c>GET /api/blind/status</c> that belong to no subsystem: what this node is
/// (version, protocol, id, role), whom it talks to (<c>peers</c>), where its data lives and how
/// much of it there is. Everything else is a section another contributor adds.
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
    IArticleRepository articleRepo,
    IBlobRepository blobRepo,
    INodeRole? role = null,
    string? dataPath = null) : IBlindStatusContributor
{
    private readonly INodeRole _role = role ?? new EnvironmentNodeRole();
    private readonly string _dataPath =
        dataPath ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH") ?? Directory.GetCurrentDirectory();

    public async Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var identity = await nodeRepo.GetAsync();
        var whitelist = await whitelistRepo.GetAllActiveAsync();

        b.Set("version", AppVersion.Current);
        b.Set("protocol", SyncProtocolVersion.Current);
        b.Set("node_id", identity?.NodeId.ToString());
        b.Set("node_name", identity?.DisplayName);
        b.Set("role", _role.IsBlind ? "blind" : "full");
        b.Set("data_path", _dataPath);
        // backups_path belongs to BlindBackupStatusContributor, which always sets it (null when
        // no repository is configured).

        await AddPeersAsync(b, whitelist, identity?.NodeId);
        await AddStorageAsync(b);
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
            ["articles"] = await articleRepo.CountAsync(),
            ["blobs"] = blobCount,
            // Everything the node stores for the mesh: the vault database, the blob table's
            // ciphertext and the encrypted media files. The console shows this as "Stored".
            ["bytes"] = dbBytes + blobBytes + mediaBytes,
        });
        b.Set("free_bytes", new DriveInfo(Path.GetFullPath(_dataPath)).AvailableFreeSpace);
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
