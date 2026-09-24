namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The version of one replicated row: the Lamport timestamp that produced it and the node that
/// produced it. Every replicated table already carries both columns; this type is what makes them
/// one value instead of two arguments that can be passed in the wrong order, or forgotten.
///
/// <para>
/// Every gate must compare versions through this one implementation, never a hand-written
/// <c>&gt;</c>/<c>&gt;=</c>. Equal Lamport timestamps are common (two in-sync nodes that each tick
/// once produce the same number), and gates with different tiebreaks disagree deterministically
/// there: a delete on A and an edit on B at the same Lamport leave the row alive on one node and
/// gone on the other, and nothing ever reconciles it because both believe they applied the newest
/// write. A new event type gets the rule by construction.
/// </para>
/// </summary>
/// <param name="LamportTs">Lamport timestamp of the write that produced this version.</param>
/// <param name="SourceNodeId">
/// The node that produced it. <see cref="Guid.Empty"/> for a row that predates source tracking, or
/// a local write on a node that has no identity yet — it sorts lowest, so a row with a real node id
/// wins a tie against one without, which is the right way round: an unattributed row is the older
/// convention.
/// </param>
public readonly record struct RowVersion(long LamportTs, Guid SourceNodeId)
{
    /// <summary>
    /// The version of a row whose <c>source_node_id</c> is nullable in the schema — the common
    /// shape, since that column was added after the tables it sits on.
    /// </summary>
    public static RowVersion Of(long lamportTs, Guid? sourceNodeId) =>
        new(lamportTs, sourceNodeId ?? Guid.Empty);
}
