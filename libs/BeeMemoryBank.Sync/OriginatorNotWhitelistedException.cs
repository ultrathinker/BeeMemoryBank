namespace BeeMemoryBank.Sync;

/// <summary>
/// An event's originator (SyncEvent.NodeId) has no whitelist row on this node at all yet.
/// Distinct from the plain <see cref="UnauthorizedAccessException"/> thrown for a REVOKED
/// originator in <see cref="EventApplier.ApplyAsync"/>, which is a permanent answer about a peer
/// this node has fully resolved. This one is not: in a mesh, the
/// whitelist_add for a brand-new or newly-relayed peer can easily arrive after an event it
/// authorized, if the two travel different gossip paths or the add is still propagating. Once
/// that add lands, the identical event verifies and applies with no further action.
/// </summary>
public sealed class OriginatorNotWhitelistedException(Guid nodeId)
    : UnauthorizedAccessException($"Node {nodeId} is not in the whitelist."), IDeferrableSyncFailure
{
    public Guid NodeId { get; } = nodeId;
}
