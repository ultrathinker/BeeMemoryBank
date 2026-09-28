namespace BeeMemoryBank.Sync;

/// <summary>
/// An event that only a superadmin may author came from a peer this node's whitelist does not
/// (yet) mark as superadmin.
///
/// <para>
/// Deferred, like <see cref="OriginatorNotWhitelistedException"/>, not permanent (plan 4.2): the
/// promotion of a PC is issued by the hub, the superadmin-only event (e.g. adding a blind node) by
/// the PC itself, and the two travel different paths — a phone can easily receive the PC's
/// whitelist_add before the hub's whitelist_update that makes the PC superadmin. Quarantining the
/// add after a few tries would lose it for good; waiting lets it apply once the promotion lands.
/// A peer that is never promoted still ends in quarantine, only after the longer deferred budget.
/// </para>
/// </summary>
public sealed class OriginatorNotSuperadminException(Guid nodeId, string eventType)
    : UnauthorizedAccessException(
        $"Event type {eventType} requires superadmin privilege; node {nodeId} is not authorized."),
      IDeferrableSyncFailure
{
    public Guid NodeId { get; } = nodeId;
}
