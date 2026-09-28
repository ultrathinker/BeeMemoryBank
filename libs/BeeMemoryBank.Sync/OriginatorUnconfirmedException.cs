namespace BeeMemoryBank.Sync;

/// <summary>
/// An event's originator has a whitelist row this node has not confirmed yet
/// (<see cref="BeeMemoryBank.Core.Models.WhitelistStatuses.Unconfirmed"/>, left by a restore no anchor vouched
/// for). Deferred, not permanent: the sync stops at the event and retries it, and once a superadmin confirms
/// the row the identical event verifies and applies. Only a REVOKED originator is a permanent rejection.
/// </summary>
public sealed class OriginatorUnconfirmedException(Guid nodeId)
    : UnauthorizedAccessException($"Node {nodeId} awaits confirmation on this node."), IDeferrableSyncFailure
{
    public Guid NodeId { get; } = nodeId;
}
