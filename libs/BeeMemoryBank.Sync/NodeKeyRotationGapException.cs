namespace BeeMemoryBank.Sync;

/// <summary>
/// A <c>node_key_rotated</c> whose counter is more than one above the counter this node holds for the node: a rotation in between has not
/// arrived yet. Deferrable: once it lands, this one applies (plan WS-D D1, "counter is exactly the stored counter + 1").
/// </summary>
public sealed class NodeKeyRotationGapException(Guid nodeId, long stored, long received)
    : InvalidOperationException($"Node {nodeId} key rotation {received} arrived while this node holds rotation {stored}; an earlier one is still on its way."),
      IDeferrableSyncFailure
{
    public Guid NodeId { get; } = nodeId;
}
