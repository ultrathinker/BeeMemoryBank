using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

public class ConflictResolverTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public void HigherLamport_Wins()
    {
        ConflictResolver.IncomingWins(
            existingLamport: 5, existingNodeId: NodeA,
            incomingLamport: 10, incomingNodeId: NodeB)
            .Should().BeTrue();
    }

    [Fact]
    public void LowerLamport_Loses()
    {
        ConflictResolver.IncomingWins(
            existingLamport: 10, existingNodeId: NodeA,
            incomingLamport: 5, incomingNodeId: NodeB)
            .Should().BeFalse();
    }

    [Fact]
    public void EqualLamport_NodeIdTiebreak_HigherNodeWins()
    {
        // NodeB > NodeA (b > a in string comparison)
        ConflictResolver.IncomingWins(
            existingLamport: 10, existingNodeId: NodeA,
            incomingLamport: 10, incomingNodeId: NodeB)
            .Should().BeTrue();
    }

    [Fact]
    public void EqualLamport_NodeIdTiebreak_LowerNodeLoses()
    {
        ConflictResolver.IncomingWins(
            existingLamport: 10, existingNodeId: NodeB,
            incomingLamport: 10, incomingNodeId: NodeA)
            .Should().BeFalse();
    }

    [Fact]
    public void Tiebreak_IsDeterministic()
    {
        // Both nodes should arrive at the same result
        var resultAB = ConflictResolver.IncomingWins(10, NodeA, 10, NodeB);
        var resultBA = ConflictResolver.IncomingWins(10, NodeB, 10, NodeA);
        // One should win, the other should lose
        (resultAB != resultBA).Should().BeTrue();
    }
}
