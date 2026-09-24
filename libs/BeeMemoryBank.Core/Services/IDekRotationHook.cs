namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Host-specific work that must happen immediately BEFORE a DEK rotation's destructive rewrap, while
/// the session still holds the outgoing master DEK. Runs on every apply path — the initiator's
/// accept and a peer's auto-accept alike.
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
/// Best-effort by contract: a failing hook is logged and the rotation proceeds. Aborting a peer's
/// rotation over host-side data would leave the whole node on a retired DEK — every article synced
/// afterwards unreadable — which is far worse than whatever the hook failed to move.
/// </para>
/// </summary>
public interface IDekRotationHook
{
    Task BeforeRewrapAsync(CancellationToken ct);
}
