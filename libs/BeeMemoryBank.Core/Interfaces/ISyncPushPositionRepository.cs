using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncPushPositionRepository
{
    Task<SyncPushPosition?> GetAsync(Guid remoteNodeId);
    Task UpsertAsync(SyncPushPosition position);
    Task<List<SyncPushPosition>> GetAllAsync();
    Task UpdatePositionAsync(Guid remoteNodeId, long lastPushedSeq);

    /// <summary>
    /// Returns push-positions for ACTIVE whitelist peers — i.e. how far each
    /// active peer has reached in OUR event log. Used by compaction to decide
    /// the safe truncation point.
    /// </summary>
    Task<List<(Guid NodeId, long LastPushedSeq, DateTime PushedAt)>> GetAllActivePushPositionsAsync();

    /// <summary>
    /// Returns ALL active whitelist peers with their push position (or null if never synced).
    /// Used by compaction to detect peers that haven't reported yet and warn about them.
    /// </summary>
    Task<List<(Guid NodeId, long? LastPushedSeq, DateTime? PushedAt)>> GetAllActivePeersWithPushPositionsAsync();
}
