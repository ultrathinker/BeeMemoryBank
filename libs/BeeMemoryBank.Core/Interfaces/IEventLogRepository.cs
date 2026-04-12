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
    Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0);
    Task<int> GetTotalCountAsync();
    Task<List<SyncEvent>> GetByArticleAsync(Guid articleId, int limit = 50);
    Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000);
    /// <summary>All events except those originating from the specified node (for relay/gossip push).</summary>
    Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000);
}
