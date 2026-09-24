namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Host-specific work that must be COMPLETE before a DEK rotation's destructive rewrap starts, while
/// the session still holds the outgoing master DEK. Runs on every apply path — the initiator's
/// propose and accept, and a peer's auto-accept alike.
///
/// <para>
/// The case it exists for: data a host keeps OUTSIDE the vault database but still sealed directly
/// under the master DEK. The rewrap transaction cannot reach another file, so anything left sealed
/// under the old DEK when the rotation commits is readable only through the in-memory retired-DEK
/// cache — which is gone at the next restart. A hook moves such data onto a key that the rewrap
/// DOES carry forward — a node data key in <c>tbl_node_data_key</c> (the Api moves its chat.db
/// rows onto the <c>'chat'</c> key).
/// </para>
///
/// <para>
/// Mandatory by contract. A hook returns only once its data is fully moved, and throws otherwise;
/// any exception aborts the rotation BEFORE the rewrap transaction opens, surfaced as
/// <see cref="Exceptions.DekRotationPreconditionException"/>. Nothing has changed at that point, so
/// the rotation can be retried: a peer keeps the rotation at Committing and retries it on the next
/// unlock. Letting the rotation commit anyway would make the unmoved data permanently unreadable
/// after a restart.
/// </para>
/// </summary>
public interface IDekRotationHook
{
    Task BeforeRewrapAsync(CancellationToken ct);
}
