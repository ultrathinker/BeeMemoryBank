using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

public interface IRestoreInitiator : IRestoreRetrier
{
    /// <summary>
    /// False when accepting a restore_network cannot harm this node, so it happens for every such
    /// event — inline, as part of applying it — rather than only for originators whose whitelist row
    /// says auto-accept. Only the blind node's initiator says so: it merely flags itself for a reseed.
    /// </summary>
    bool RequiresApproval => true;

    Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent);
}
