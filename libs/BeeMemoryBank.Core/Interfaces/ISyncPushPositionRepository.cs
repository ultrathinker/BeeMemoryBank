using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncPushPositionRepository
{
    Task<SyncPushPosition?> GetAsync(Guid remoteNodeId);
    Task UpsertAsync(SyncPushPosition position);
    Task<List<SyncPushPosition>> GetAllAsync();
    Task UpdatePositionAsync(Guid remoteNodeId, long lastPushedSeq);

    /// <summary>
    /// Records how far the peer says it has read our log, in its own words
    /// (<c>POST /api/sync/report-position</c>). Kept apart from
    /// <see cref="UpdatePositionAsync"/>'s delivery watermark, which moves when a page is merely
    /// served: only an acknowledged position may be used to delete events (Codex round 2, security
    /// #3). Never moves backwards.
    /// </summary>
    Task RecordReportedPositionAsync(Guid remoteNodeId, long reportedSeq);

    /// <summary>
    /// Returns push-positions for ACTIVE whitelist peers — i.e. how far each
    /// active peer has reached in OUR event log. Used by compaction to decide
    /// the safe truncation point.
    /// </summary>
    Task<List<(Guid NodeId, long LastPushedSeq, DateTime PushedAt)>> GetAllActivePushPositionsAsync();

    /// <summary>
    /// Returns ALL active whitelist peers with their delivery watermark and their reported
    /// (acknowledged) position — either null if never synced or never reported. Used by compaction
    /// to detect peers that haven't reported yet and warn about them, and by the blind log trimmer,
    /// which cuts only at what a peer acknowledged (<c>ReportedSeq</c>).
    /// </summary>
    Task<List<(Guid NodeId, long? LastPushedSeq, DateTime? PushedAt, long? ReportedSeq)>> GetAllActivePeersWithPushPositionsAsync();
}
