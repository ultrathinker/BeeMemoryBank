using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncPositionRepository
{
    Task<SyncPosition?> GetAsync(Guid remoteNodeId);
    Task UpsertAsync(SyncPosition position);
    Task<List<SyncPosition>> GetAllAsync();
}
