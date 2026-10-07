using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ISyncPositionRepository
{
    Task<SyncPosition?> GetAsync(Guid remoteNodeId);
    Task UpsertAsync(SyncPosition position);

    /// <summary>
    /// Creates the pull position for <paramref name="remoteNodeId"/> only if there is none: true when this call created
    /// it, false when a position already existed (written by anyone, a moment ago included). Never moves an existing
    /// cursor - what adopting a blind peer's checkpoint needs to happen once, whatever runs at the same time.
    /// </summary>
    Task<bool> TryAdoptAsync(Guid remoteNodeId, long lastSequenceNum);
    Task<List<SyncPosition>> GetAllAsync();
    Task<List<(Guid NodeId, long LastSequenceNum, DateTime UpdatedAt)>> GetAllActivePositionsAsync();
}
