namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The version of one replicated row: the Lamport timestamp that produced it and the node that
/// produced it. Every replicated table already carries both columns; this type is what makes them
/// one value instead of two arguments that can be passed in the wrong order, or forgotten.
///
/// <para>
/// It exists because the comparison was being written by hand at each gate, and the hand-written
/// versions had drifted apart. The article tombstone gate compared <c>tombstone.LamportTs &gt;=
/// evt.LamportTs</c> with no tiebreak at all; folder delete used a bare <c>&gt;</c>; comment delete
/// used <c>&gt;</c> against a nullable; everything else went through
/// <c>ConflictResolver.IncomingWins</c> in BeeMemoryBank.Sync. Three different answers to
/// the same question, and at equal Lamport timestamps — which happen constantly, because two nodes
/// that were in sync and each tick once produce the same number — they disagree deterministically
/// rather than occasionally. A delete on A and an edit on B, both at L=11, ended with the article
/// alive on one node and gone on the other for half of all node-id pairs, and nothing ever
/// reconciles it: both nodes believe they applied the newest write.
/// </para>
///
/// <para>
/// So the comparison has exactly one implementation and every gate calls it. A new event type gets
/// the rule by construction rather than by whoever writes it remembering to.
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
