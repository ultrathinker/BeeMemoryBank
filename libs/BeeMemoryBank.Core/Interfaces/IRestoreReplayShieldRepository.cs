namespace BeeMemoryBank.Core.Interfaces;

public interface IRestoreReplayShieldRepository
{
    Task<long?> GetShieldThresholdAsync(string peerNodeId);
    Task UpsertShieldAsync(string peerNodeId, long ignoreEventsBeforeLamportTs, string shieldEventId, string createdAt);
    Task DeleteShieldAsync(string peerNodeId);
    Task<List<(string PeerNodeId, long IgnoreEventsBeforeLamportTs, string ShieldEventId)>> GetAllAsync();
}
