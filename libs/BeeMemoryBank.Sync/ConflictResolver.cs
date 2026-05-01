namespace BeeMemoryBank.Sync;

/// <summary>
/// Last Writer Wins by Lamport timestamp + node_id tiebreak.
/// Deterministic: any two nodes will reach the same result.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Returns true if the incoming event wins over the existing state.
    /// </summary>
    public static bool IncomingWins(
        long existingLamport, Guid existingNodeId,
        long incomingLamport, Guid incomingNodeId)
    {
        if (incomingLamport > existingLamport) return true;
        if (incomingLamport < existingLamport) return false;

        // Equal Lamport: deterministic tiebreak by node_id (higher wins)
        return string.Compare(
            incomingNodeId.ToString("D"),
            existingNodeId.ToString("D"),
            StringComparison.Ordinal) > 0;
    }
}
