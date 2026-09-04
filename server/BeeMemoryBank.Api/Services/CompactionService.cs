using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Exceptions;
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
    /// <summary>
    /// How many of the most recent events survive a compaction, and therefore also how far a peer
    /// may fall behind before compacting would cut it off. 1500 by default; override with
    /// <c>BMB_COMPACTION_KEEP_COUNT</c>.
    ///
    /// <para>It is a setting rather than a constant because the two things it controls pull in
    /// opposite directions and the right balance depends on the mesh. Too low and a peer that is
    /// offline for a long weekend has to wipe and rejoin — with ~20 writers, 1500 events is a day
    /// or two. Too high and <c>tbl_event</c> never shrinks. Neither answer is right for every
    /// deployment, and an operator whose phone keeps getting cut off should be able to raise this
    /// rather than being told the number is compiled in.</para>
    ///
    /// <para>Floored at 100: below that the log stops being a usable sync window at all, and a
    /// typo (or a stray <c>0</c>) would silently turn every compaction into a mesh-wide
    /// wipe-and-rejoin. An unparseable or out-of-range value falls back to the default and is not
    /// an error — same shape as BMB_AUDIT_RETENTION_DAYS.</para>
    /// </summary>
    private static int TargetKeepCount
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BMB_COMPACTION_KEEP_COUNT");
            return int.TryParse(raw, out var v) && v >= 100 ? v : DefaultKeepCount;
        }
    }

    private const int DefaultKeepCount = 1500;
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
        if (totalEvents <= TargetKeepCount)
        {
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: $"Log already has {totalEvents} events — target keep-count is {TargetKeepCount}, so nothing to remove.",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: totalEvents);
        }

        var syncedPeers = allPeers.Where(p => p.LastPushedSeq != null).ToList();
        var neverSyncedPeers = allPeers.Where(p => p.LastPushedSeq == null).ToList();

        // Peer-safety check: every synced peer must be WITHIN the last TargetKeepCount events
        // of head — otherwise compaction would cut them off. Count = events with seq > peer_pos.
        //
        // The result is collected here rather than recomputed later. The "is anyone at risk?"
        // decision below used to re-run this same COUNT for every peer inside a LINQ predicate,
        // via .GetAwaiter().GetResult() — sync-over-async on a request thread, and twice the
        // queries, to answer a question this loop had already answered.
        var atRiskPeers = new List<Guid>();

        foreach (var peer in syncedPeers)
        {
            var peerBehindCount = await eventLogRepo.CountEventsAfterSequenceAsync(peer.LastPushedSeq!.Value);
            if (peerBehindCount >= TargetKeepCount)
            {
                atRiskPeers.Add(peer.NodeId);
                warnings.Add($"Peer {peer.NodeId} is {peerBehindCount} operations behind — would be cut off (target keep-count is {TargetKeepCount}). Wait for it to sync, raise BMB_COMPACTION_KEEP_COUNT, revoke it, or compact with acceptCuttingOffPeers.");
            }
            if (peer.PushedAt != null && (DateTime.UtcNow - peer.PushedAt.Value).TotalDays > 14)
            {
                warnings.Add($"Peer {peer.NodeId} last reported {(DateTime.UtcNow - peer.PushedAt.Value).TotalDays:F0} days ago");
            }
        }

        foreach (var ns in neverSyncedPeers)
        {
            atRiskPeers.Add(ns.NodeId);
            warnings.Add($"Peer {ns.NodeId} is in whitelist but has never synced — would be cut off if compaction proceeds. Wait for it to sync, revoke it, or compact with acceptCuttingOffPeers.");
        }

        if (atRiskPeers.Count > 0)
        {
            return new CompactionPreview(
                HeadSeq: headSeq, MinSeq: minSeq.Value, TotalEvents: totalEvents,
                ActivePeerCount: allPeers.Count,
                ProposedCp: 0, CanCompact: false,
                Reason: $"{atRiskPeers.Count} peer(s) would be cut off (more than {TargetKeepCount} operations behind, or never synced). See warnings.",
                Warnings: warnings, PeerPositions: peerPositions,
                EventsToDelete: 0, EventsRemaining: totalEvents,
                AtRiskPeers: atRiskPeers);
        }

        // Count-based compute: delete oldest (totalEvents - TargetKeepCount) events.
        // proposedCp = sequence_num of the Nth oldest event (= highest seq we'll delete).
        var eventsToDelete = totalEvents - TargetKeepCount;
        var cpAtRank = await eventLogRepo.GetSequenceAtRankAsync(eventsToDelete);
        if (cpAtRank == null)
        {
            // Shouldn't happen (we checked totalEvents > TargetKeepCount above), but be defensive.
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
            Reason: $"Keep the {TargetKeepCount} most recent operations, delete the rest.",
            Warnings: warnings, PeerPositions: peerPositions,
            EventsToDelete: eventsToDelete, EventsRemaining: TargetKeepCount);
    }

    public async Task<CompactionResult> ExecuteAsync(
        long? explicitCp = null, string reason = "manual", bool acceptCuttingOffPeers = false)
    {
        // ConflictException, not a bare InvalidOperationException: "someone else is already doing
        // this" is a 409 the caller can retry, while everything else ExecuteAsync refuses is a bad
        // request. The endpoint used to map every InvalidOperationException to 400, so a second
        // operator pressing Compact was told their request was malformed. Same distinction
        // DekRotationService.ProposeRotationAsync makes for the same reason.
        if (!await _executeLock.WaitAsync(0))
            throw new ConflictException("Another compaction is already in progress");

        try
        {
            return await ExecuteCoreAsync(explicitCp, reason, acceptCuttingOffPeers);
        }
        finally
        {
            _executeLock.Release();
        }
    }

    private async Task<CompactionResult> ExecuteCoreAsync(
        long? explicitCp, string reason, bool acceptCuttingOffPeers)
    {
        var preview = await PreviewAsync();

        // The peer-safety check runs on EVERY path, not only when the caller lets the preview
        // choose the checkpoint. It used to live in PreviewAsync alone, and ExecuteCoreAsync
        // consulted it only through preview.ProposedCp — so a caller passing an explicit cp
        // sailed straight past it and stranded peers with nothing logged and nothing said. The
        // explicit-cp path is exactly the one an operator reaches for when the button is greyed
        // out, which made it the likeliest way to hit the case the check exists for.
        //
        // acceptCuttingOffPeers is the only way through, and it is deliberately not a no-op alias
        // for "use an explicit cp": a dormant phone otherwise blocks every compaction forever, so
        // there has to be a documented exit — but one that names what it is doing in the log.
        var atRisk = preview.AtRiskPeers ?? [];
        if (atRisk.Count > 0)
        {
            if (!acceptCuttingOffPeers)
                throw new ConflictException(
                    $"{atRisk.Count} peer(s) would be cut off by this compaction: " +
                    string.Join(", ", atRisk) + ". Wait for them to sync, raise " +
                    "BMB_COMPACTION_KEEP_COUNT, revoke them, or repeat with acceptCuttingOffPeers.");

            logger.LogWarning(
                "Compaction proceeding with acceptCuttingOffPeers: {Count} peer(s) will be unable to " +
                "resume from their last position and must wipe and rejoin — {Peers}",
                atRisk.Count, string.Join(", ", atRisk));
        }

        // ProposedCp is 0 when the preview refused, so an override has to supply the checkpoint
        // itself; the range guards below turn a missing one into a clear 400 rather than a
        // confusing attempt to compact to sequence zero.
        var cp = explicitCp ?? preview.ProposedCp;

        // ArgumentException: an out-of-range explicitCp is the caller's parameter being wrong,
        // which is a 400 — distinct from the 409 above and from the 409 that a genuine state
        // problem (no node identity) produces further down.
        if (cp <= preview.MinSeq)
            throw new ArgumentException($"Cannot compact to {cp} — current min is {preview.MinSeq}");
        if (cp > preview.HeadSeq)
            throw new ArgumentException($"Cannot compact to {cp} — current head is {preview.HeadSeq}");

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

/// <param name="AtRiskPeers">
/// Peers that this compaction would cut off — behind by more than the keep-count, or never synced.
/// Non-empty means <c>CanCompact</c> is false unless the caller explicitly accepts it. Named
/// individually rather than counted, because "which machine am I about to strand" is the question
/// an operator actually has to answer before overriding.
/// </param>
public record CompactionPreview(
    long HeadSeq, long MinSeq, int TotalEvents, int ActivePeerCount,
    long ProposedCp, bool CanCompact, string Reason,
    List<string> Warnings, List<PeerPosition> PeerPositions,
    int EventsToDelete, int EventsRemaining,
    List<Guid>? AtRiskPeers = null);

public record PeerPosition(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt);
public record CompactionResult(long CpAfter, int EventsDeleted, string SnapshotFileName);
/// <param name="AcceptCuttingOffPeers">
/// Proceed even though the preview says peers would be stranded. Off by default, and it exists for
/// a deadlock that had no exit: one phone that has been off for a week blocks every compaction, so
/// <c>tbl_event</c> grows without bound and nobody can do anything about it but revoke the phone.
///
/// <para>It also closes a hole rather than opening one. The peer check lived only in
/// <c>PreviewAsync</c>, so passing an explicit checkpoint skipped it entirely and stranded peers
/// with no warning at all. ExecuteAsync now runs the same check on every path; this flag is the
/// only way past it, and it is recorded in the log line with the list of peers it stranded.</para>
/// </param>
public record CompactionRequest(long? ExplicitCp = null, string Reason = "manual", bool AcceptCuttingOffPeers = false);
