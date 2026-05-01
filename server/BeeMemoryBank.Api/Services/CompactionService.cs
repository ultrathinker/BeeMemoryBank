using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public class CompactionService(
    IEventLogRepository eventLogRepo,
    ISyncPushPositionRepository syncPushPositionRepo,
    SnapshotService snapshotService,
    IEventLogger eventLogger,
    INodeIdentityRepository nodeRepo,
    SnapshotJoinCache cache,
    DbConnectionFactory connFactory,
    ILogger<CompactionService> logger)
{
    // Count-based target: we want to keep exactly this many most-recent events after compaction.
    // Peer safety: no peer may be more than TARGET_KEEP_COUNT events behind head (else they'd be cut off).
    private const int TARGET_KEEP_COUNT = 1500;
    // Shared with SnapshotService.ApplyNetworkRestoreAsync / RestoreAsync — both flows
    // bulk-rewrite tbl_event and must not interleave.
    private static readonly SemaphoreSlim _executeLock = HeavyOperationLock.Instance;

    public async Task<CompactionPreview> PreviewAsync()
    {
        var headSeq = await eventLogRepo.GetMaxSequenceAsync();
        var minSeq = await eventLogRepo.GetMinSequenceAsync();
        var totalEvents = await eventLogRepo.GetTotalCountAsync();

        // How far peers have read OUR event log (tbl_sync_push_position — filled when peers
        // POST /api/sync/report-position). LEFT JOIN on whitelist so never-synced peers are visible.
        var allPeers = await syncPushPositionRepo.GetAllActivePeersWithPushPositionsAsync();

        List<string> warnings = [];
        var peerPositions = allPeers.Select(p => new PeerPosition(
            p.NodeId, p.LastPushedSeq ?? -1, p.PushedAt ?? DateTime.MinValue)).ToList();

        if (headSeq == 0 || minSeq == null || totalEvents == 0)
        {
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq ?? 0, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: "Event log is empty",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: 0);
        }

        // Already at or below target — nothing to compact.
        if (totalEvents <= TARGET_KEEP_COUNT)
        {
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: $"Log already has {totalEvents} events — target keep-count is {TARGET_KEEP_COUNT}, so nothing to remove.",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: totalEvents);
        }

        var syncedPeers = allPeers.Where(p => p.LastPushedSeq != null).ToList();
        var neverSyncedPeers = allPeers.Where(p => p.LastPushedSeq == null).ToList();

        // Peer-safety check: every synced peer must be WITHIN the last TARGET_KEEP_COUNT events
        // of head — otherwise compaction would cut them off. Count = events with seq > peer_pos.
        foreach (var peer in syncedPeers)
        {
            var peerBehindCount = await eventLogRepo.CountEventsAfterSequenceAsync(peer.LastPushedSeq!.Value);
            if (peerBehindCount >= TARGET_KEEP_COUNT)
            {
                warnings.Add($"Peer {peer.NodeId} is {peerBehindCount} operations behind — would be cut off (target keep-count is {TARGET_KEEP_COUNT}). Wait for it to sync or revoke.");
            }
            if (peer.PushedAt != null && (DateTime.UtcNow - peer.PushedAt.Value).TotalDays > 14)
            {
                warnings.Add($"Peer {peer.NodeId} last reported {(DateTime.UtcNow - peer.PushedAt.Value).TotalDays:F0} days ago");
            }
        }

        foreach (var ns in neverSyncedPeers)
        {
            warnings.Add($"Peer {ns.NodeId} is in whitelist but has never synced — would be cut off if compaction proceeds. Wait for it to sync or revoke.");
        }

        // Any peer that would be cut off => refuse compaction
        var anyPeerAtRisk = syncedPeers.Any(p => {
            var peerBehind = eventLogRepo.CountEventsAfterSequenceAsync(p.LastPushedSeq!.Value).GetAwaiter().GetResult();
            return peerBehind >= TARGET_KEEP_COUNT;
        }) || neverSyncedPeers.Count > 0;

        if (anyPeerAtRisk)
        {
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: $"At least one peer would be cut off (they are more than {TARGET_KEEP_COUNT} operations behind, or have never synced). See warnings.",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: totalEvents);
        }

        // Count-based compute: delete oldest (totalEvents - TARGET_KEEP_COUNT) events.
        // proposedCp = sequence_num of the Nth oldest event (= highest seq we'll delete).
        var eventsToDelete = totalEvents - TARGET_KEEP_COUNT;
        var cpAtRank = await eventLogRepo.GetSequenceAtRankAsync(eventsToDelete);
        if (cpAtRank == null)
        {
            // Shouldn't happen (we checked totalEvents > TARGET_KEEP_COUNT above), but be defensive.
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: "Could not resolve target sequence number — unexpected state.",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: totalEvents);
        }

        return new CompactionPreview(
            HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
            ActivePeerCount: allPeers.Count,
            ProposedCp: cpAtRank.Value, CanCompact: true,
            Reason: $"Keep the {TARGET_KEEP_COUNT} most recent operations, delete the rest.",
            Warnings: warnings, PeerPositions: peerPositions,
            EventsToDelete: eventsToDelete, EventsRemaining: TARGET_KEEP_COUNT);
    }

    public async Task<CompactionResult> ExecuteAsync(long? explicitCp = null, string reason = "manual")
    {
        if (!await _executeLock.WaitAsync(0))
            throw new InvalidOperationException("Another compaction is already in progress");

        try
        {
            return await ExecuteCoreAsync(explicitCp, reason);
        }
        finally
        {
            _executeLock.Release();
        }
    }

    private async Task<CompactionResult> ExecuteCoreAsync(long? explicitCp, string reason)
    {
        var preview = await PreviewAsync();
        var cp = explicitCp ?? preview.ProposedCp;

        if (cp <= preview.MinSeq)
            throw new InvalidOperationException($"Cannot compact to {cp} — current min is {preview.MinSeq}");
        if (cp > preview.HeadSeq)
            throw new InvalidOperationException($"Cannot compact to {cp} — current head is {preview.HeadSeq}");

        logger.LogInformation("Generating compaction snapshot at CP={Cp}", cp);
        var snap = await snapshotService.CreateAsync(
            filterSecrets: false,
            sign: true,
            cpSequenceNum: cp);
        logger.LogInformation("Snapshot created: {FileName} ({Size} bytes)", snap.FileName, snap.SizeBytes);

        var snapPath = snapshotService.GetSnapshotPath(snap.FileName);
        var snapSha256 = await ComputeFileSha256Async(snapPath);

        var localNode = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node identity not found");

        string? prevCheckpointSha256 = null;
        using (var prevConn = connFactory.CreateConnection())
        {
            var prevPayload = await prevConn.ExecuteScalarAsync<string?>(
                @"SELECT payload FROM tbl_event
                  WHERE event_type = @t AND node_id = @localNodeId
                  ORDER BY sequence_num DESC LIMIT 1",
                new { t = EventTypes.SnapshotCheckpoint, localNodeId = localNode.NodeId });
            if (prevPayload != null)
            {
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(prevPayload));
                prevCheckpointSha256 = Convert.ToHexStringLower(hashBytes);
            }
        }

        var cpBefore = preview.MinSeq;
        int deleted;
        using (var conn = connFactory.CreateConnection())
        {
            using var tx = conn.BeginTransaction();
            try
            {
                deleted = await conn.ExecuteAsync(
                    "DELETE FROM tbl_event WHERE sequence_num <= @cp AND event_type != @excludeType",
                    new { cp, excludeType = EventTypes.SnapshotCheckpoint }, tx);

                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_compaction_log
                      (compacted_at, cp_before, cp_after, events_removed, snapshot_file_name, reason)
                      VALUES (@at, @before, @after, @removed, @file, @reason)",
                    new
                    {
                        at = DateTime.UtcNow.ToString("o"),
                        before = cpBefore,
                        after = cp,
                        removed = deleted,
                        file = snap.FileName,
                        reason
                    }, tx);

                await conn.ExecuteAsync(
                    @"DELETE FROM tbl_compaction_log WHERE id NOT IN (
                        SELECT id FROM tbl_compaction_log ORDER BY id DESC LIMIT 20
                    )", tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        await eventLogger.LogSnapshotCheckpointAsync(
            cpSeq: cp,
            eventsRemoved: deleted,
            snapshotFileName: snap.FileName,
            snapshotSha256: snapSha256,
            prevCheckpointSha256: prevCheckpointSha256,
            producedAt: DateTime.UtcNow);

        cache.Invalidate();

        var pruned = snapshotService.PruneOldSnapshots(keepCount: 2);
        if (pruned > 0)
            logger.LogInformation("Pruned {Count} old snapshots", pruned);

        logger.LogInformation("Compaction done. Deleted {Count} events up to seq={Cp}", deleted, cp);

        return new CompactionResult(cp, deleted, snap.FileName);
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexStringLower(hash);
    }
}

public record CompactionPreview(
    long HeadSeq, long MinSeq, int TotalEvents, int ActivePeerCount,
    long ProposedCp, bool CanCompact, string Reason,
    List<string> Warnings, List<PeerPosition> PeerPositions,
    int EventsToDelete, int EventsRemaining);

public record PeerPosition(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt);
public record CompactionResult(long CpAfter, int EventsDeleted, string SnapshotFileName);
public record CompactionRequest(long? ExplicitCp = null, string Reason = "manual");
