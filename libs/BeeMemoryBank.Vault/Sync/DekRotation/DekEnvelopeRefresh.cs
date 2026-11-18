namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>A rotation envelope made for this node's previous identity key (plan WS-D D4): "not yet", the rotation stays Committing.</summary>
public sealed class DekEnvelopeStaleException(Guid commitEventId)
    : BeeMemoryBank.Core.Exceptions.DekRotationPreconditionException(
        $"The DEK rotation {commitEventId} was sealed for this node's previous identity key; a fresh envelope is fetched from the node that rotated.")
{
    public Guid CommitEventId { get; } = commitEventId;
}
