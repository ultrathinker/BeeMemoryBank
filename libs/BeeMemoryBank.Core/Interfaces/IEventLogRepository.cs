using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IEventLogRepository
{
    Task AppendAsync(SyncEvent evt);
    /// <summary>Atomically inserts the event if not already present. Returns true if inserted, false if duplicate.</summary>
    Task<bool> AppendIfNotExistsAsync(SyncEvent evt);
    Task<bool> ExistsAsync(Guid eventId);
    Task<long> GetMaxLamportTimestampAsync();
    /// <summary>Events from this node after the specified position (for serving to other nodes).</summary>
    Task<List<SyncEvent>> GetAfterSequenceAsync(long afterSequenceNum, int limit = 1000);
    /// <summary>All events (all nodes) after the specified position (for internal synchronization).</summary>
    Task<List<SyncEvent>> GetAllAfterSequenceAsync(long afterSequenceNum, int limit = 1000);
    /// <summary>
    /// Recent events, newest first. Pass <paramref name="eventType"/> to filter at SQL level —
    /// filtering in memory after LIMIT silently drops matches when the window is smaller than the
    /// event stream (this is a real issue at ~50+ mixed events).
    /// </summary>
    Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0, string? eventType = null);
    Task<int> GetTotalCountAsync();
    Task<List<SyncEvent>> GetByArticleAsync(Guid articleId, int limit = 50);
    Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000);
    /// <summary>All events except those originating from the specified node (for relay/gossip push).</summary>
    Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000);
    Task<bool> IsHardDeletedAsync(string entityId, long lamportTs);
    Task<long?> GetMinSequenceAsync();
    Task<long> GetMaxSequenceAsync();
    Task<int> DeleteUpToAsync(long cpSequenceNum);
    Task<long?> GetLastCompactionCpAsync();
    /// <summary>
    /// Returns the sequence_num of the Nth-oldest event (1-indexed). Returns null if the log has fewer than N events.
    /// Used by count-based compaction: "give me the seq of the event I'd delete up to, to leave exactly (total - N) events".
    /// </summary>
    Task<long?> GetSequenceAtRankAsync(int rank);
    /// <summary>
    /// Counts events with sequence_num greater than the given value — i.e., how many events a peer
    /// at that position would be "behind" in our log.
    /// </summary>
    Task<int> CountEventsAfterSequenceAsync(long seqNum);
    Task<SyncEvent?> GetByIdAsync(string eventId);
}
