using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

public class ConflictResolverTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public void HigherLamport_Wins()
    {
        ConflictResolver.IncomingWins(new RowVersion(5, NodeA), new RowVersion(10, NodeB))
            .Should().BeTrue();
    }

    [Fact]
    public void LowerLamport_Loses()
    {
        ConflictResolver.IncomingWins(new RowVersion(10, NodeA), new RowVersion(5, NodeB))
            .Should().BeFalse();
    }

    [Fact]
    public void EqualLamport_NodeIdTiebreak_HigherNodeWins()
    {
        // NodeB > NodeA (b > a in string comparison)
        ConflictResolver.IncomingWins(new RowVersion(10, NodeA), new RowVersion(10, NodeB))
            .Should().BeTrue();
    }

    [Fact]
    public void EqualLamport_NodeIdTiebreak_LowerNodeLoses()
    {
        ConflictResolver.IncomingWins(new RowVersion(10, NodeB), new RowVersion(10, NodeA))
            .Should().BeFalse();
    }

    [Fact]
    public void Tiebreak_IsDeterministic()
    {
        // Both nodes should arrive at the same result
        var resultAB = ConflictResolver.IncomingWins(new RowVersion(10, NodeA), new RowVersion(10, NodeB));
        var resultBA = ConflictResolver.IncomingWins(new RowVersion(10, NodeB), new RowVersion(10, NodeA));
        // One should win, the other should lose
        (resultAB != resultBA).Should().BeTrue();
    }

    /// <summary>
    /// A version that ties on BOTH fields is the same write arriving twice, and must not win.
    /// Every gate treats "wins" as "act on this event", so a true here would re-apply a
    /// redelivered event — writing a second conflict-version row for a body that never lost
    /// anything, and re-running the delete path on an article already deleted by that exact event.
    /// Redelivery is routine (a peer that never got our position report resends from its cursor),
    /// so this is the common case, not the corner one.
    /// </summary>
    [Fact]
    public void IdenticalVersion_Loses()
    {
        ConflictResolver.IncomingWins(new RowVersion(10, NodeA), new RowVersion(10, NodeA))
            .Should().BeFalse();
    }

    /// <summary>
    /// Rows written before source tracking existed have no node id. RowVersion.Of maps that to
    /// Guid.Empty, which sorts below every real id — so at equal Lamport an attributed write beats
    /// an unattributed one. That is the right way round: the unattributed row is the older
    /// convention, and the alternative (nulls winning ties) would let ancient rows pin down ids
    /// that no live node can ever overwrite.
    /// </summary>
    [Fact]
    public void MissingNodeId_LosesTieToAttributedWrite()
    {
        ConflictResolver.IncomingWins(RowVersion.Of(10, null), new RowVersion(10, NodeA))
            .Should().BeTrue();

        ConflictResolver.IncomingWins(new RowVersion(10, NodeA), RowVersion.Of(10, null))
            .Should().BeFalse();
    }

    /// <summary>
    /// The property the whole design rests on: for any two distinct versions, exactly one node's
    /// answer is "incoming wins" and the other's is "existing wins". If both answered the same
    /// way, the two nodes would keep different rows and never reconcile — which is precisely what
    /// the hand-written gates did at equal Lamport before they were routed through here.
    ///
    /// Enumerated over the pairs the gates actually see, including the ties that used to diverge.
    /// </summary>
    [Theory]
    [InlineData(10, "a", 11, "a")]  // plain newer
    [InlineData(11, "a", 10, "b")]  // plain older, different node
    [InlineData(10, "a", 10, "b")]  // tie, incoming node higher
    [InlineData(10, "b", 10, "a")]  // tie, incoming node lower
    [InlineData(0, "a", 10, "b")]   // never-versioned row vs a real write
    [InlineData(10, "b", 0, "a")]
    public void ExactlyOneSideWins_FromEitherNodesPointOfView(
        long leftTs, string leftNode, long rightTs, string rightNode)
    {
        var left = new RowVersion(leftTs, Node(leftNode));
        var right = new RowVersion(rightTs, Node(rightNode));

        // The same pair judged from both directions: one node holds `left` and receives `right`,
        // its peer holds `right` and receives `left`.
        var leftNodeSays = ConflictResolver.IncomingWins(left, right);
        var rightNodeSays = ConflictResolver.IncomingWins(right, left);

        leftNodeSays.Should().NotBe(rightNodeSays,
            $"({leftTs}, {leftNode}) vs ({rightTs}, {rightNode}) must resolve the same way on both nodes");
    }

    private static Guid Node(string letter) =>
        Guid.Parse($"{new string(letter[0], 8)}-0000-0000-0000-000000000000");
}
