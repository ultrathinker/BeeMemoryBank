namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Whether this node still carries the bridge from an old master DEK (release 2.12): v1 doors, which no rotation can re-seal, and the chain
/// material (NEW under OLD) that lets them reach the current DEK. The small question a node's state report asks; the Master key page shows
/// the whole answer.
/// </summary>
public interface IDekBridgeStatus
{
    /// <summary>How many v1 doors (key slots) the node has.</summary>
    Task<int> GetV1DoorCountAsync();

    /// <summary>True when the node keeps chain material of at least one rotation.</summary>
    Task<bool> IsBridgePresentAsync();
}
