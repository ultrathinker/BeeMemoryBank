namespace BeeMemoryBank.Sync;

/// <summary>
/// The node that answered at a peer's stored address declares another node id than the whitelist has for that address: a stale or wrong address, or something
/// else answering there. The message carries the two ids and no address; the status pages show a fixed sentence for it (<see cref="PeerContactLog.Describe"/>).
/// </summary>
public sealed class PeerIdentityMismatchException(Guid expectedNodeId, Guid declaredNodeId) : InvalidOperationException(
    $"The node that answered declares NodeId {declaredNodeId}, but we dialed it as {expectedNodeId}. Refusing to sync - this is either a stale or " +
    "incorrect address in this node's whitelist, or a peer impersonation attempt.")
{
    /// <summary>The node id the whitelist has for the address that was dialed.</summary>
    public Guid ExpectedNodeId { get; } = expectedNodeId;

    /// <summary>The node id the answering node declared.</summary>
    public Guid DeclaredNodeId { get; } = declaredNodeId;
}

/// <summary>
/// The node that answered declares the right node id and ANOTHER public key than the whitelist has on record for it (BMB-307 fix round): the node id is only
/// a claim, the key is what the network knows the node by. Refused, never "updated": a changed key is not something an address change may carry.
/// </summary>
public sealed class PeerKeyMismatchException(Guid nodeId) : InvalidOperationException(
    $"The node that answered declares NodeId {nodeId} but another public key than this node has on record for it. Refusing to sync.")
{
    public Guid NodeId { get; } = nodeId;
}
