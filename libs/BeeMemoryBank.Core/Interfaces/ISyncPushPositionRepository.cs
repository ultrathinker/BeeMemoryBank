using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncPushPositionRepository
{
    Task<SyncPushPosition?> GetAsync(Guid remoteNodeId);
    Task UpsertAsync(SyncPushPosition position);
    Task<List<SyncPushPosition>> GetAllAsync();
    Task UpdatePositionAsync(Guid remoteNodeId, long lastPushedSeq);
}
