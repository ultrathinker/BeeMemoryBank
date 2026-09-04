using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Last Writer Wins by Lamport timestamp + node_id tiebreak.
/// Deterministic: any two nodes will reach the same result.
///
/// <para>
/// This is the ONLY place a replicated row's version may be compared. Gates that wrote the
/// comparison by hand drifted from it and from each other — see <see cref="RowVersion"/> for what
/// that cost. If a new gate needs "does this event supersede what I have", it calls
/// <see cref="IncomingWins"/>; there is no case that needs its own rule.
/// </para>
///
/// <para>
/// There is deliberately no four-argument <c>(long, Guid, long, Guid)</c> overload. It existed, and
/// every call site had to hand-write <c>x.SourceNodeId ?? Guid.Empty</c> first and then get four
/// positional values in the right order — two adjacent <c>long</c>s and two adjacent
/// <see cref="Guid"/>s, where swapping existing and incoming compiles and silently inverts every
/// conflict on the node. Taking <see cref="RowVersion"/> makes that swap visible at the call site.
/// </para>
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Returns true if <paramref name="incoming"/> supersedes <paramref name="existing"/>.
    ///
    /// <para>
    /// Strict: an incoming version that ties on BOTH fields does not win. That matters at the
    /// gates, because a tie on both fields means the same write — a redelivered event — and
    /// re-applying it must be a no-op rather than a second conflict-version row.
    /// </para>
    /// </summary>
    public static bool IncomingWins(RowVersion existing, RowVersion incoming)
    {
        if (incoming.LamportTs > existing.LamportTs) return true;
        if (incoming.LamportTs < existing.LamportTs) return false;

        // Equal Lamport: deterministic tiebreak by node_id (higher wins). Compared as the "D"
        // string rather than by Guid.CompareTo, because .NET orders Guid fields in a way that does
        // not match their textual form, and the textual form is what every node's log, every audit
        // row and every debugging session shows. Two nodes must reach the same answer, and the
        // answer has to be one a human can reproduce by looking at the two ids.
        return string.Compare(
            incoming.SourceNodeId.ToString("D"),
            existing.SourceNodeId.ToString("D"),
            StringComparison.Ordinal) > 0;
    }
}
