using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

public class ReciprocalRankFusionTests
{
    [Fact]
    public void Combine_SingleList_PreservesItsOrder()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var result = ReciprocalRankFusion.Combine([[a, b, c]], topK: 10);
        result.Should().Equal(a, b, c);
    }

    [Fact]
    public void Combine_IdInBothLists_RanksAboveIdInOnlyOneList()
    {
        var inBoth = Guid.NewGuid();
        var onlyKeyword = Guid.NewGuid();
        var onlySemantic = Guid.NewGuid();

        // inBoth is 2nd in each list; onlyKeyword/onlySemantic are 1st in their own list only.
        var keyword = new List<Guid> { onlyKeyword, inBoth };
        var semantic = new List<Guid> { onlySemantic, inBoth };

        var result = ReciprocalRankFusion.Combine([keyword, semantic], topK: 10);

        result[0].Should().Be(inBoth, "an id both sources agree on should outrank an id only one source ranked, even at a worse individual rank");
    }

    [Fact]
    public void Combine_EmptyLists_ReturnsEmpty()
    {
        ReciprocalRankFusion.Combine([[], []], topK: 10).Should().BeEmpty();
    }

    [Fact]
    public void Combine_TopKSmallerThanCandidateCount_TruncatesToTopK()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var result = ReciprocalRankFusion.Combine([ids], topK: 2);
        result.Should().HaveCount(2);
        result.Should().Equal(ids[0], ids[1]);
    }

    [Fact]
    public void Combine_TopKZeroOrNegative_ReturnsEmpty()
    {
        var ids = new List<Guid> { Guid.NewGuid() };
        ReciprocalRankFusion.Combine([ids], topK: 0).Should().BeEmpty();
        ReciprocalRankFusion.Combine([ids], topK: -1).Should().BeEmpty();
    }

    [Fact]
    public void Combine_MatchesHandComputedScore()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        // a: rank 1 in list1 (1/61), rank 2 in list2 (1/62). b: rank 2 in list1 (1/62), rank 1 in list2 (1/61).
        // Both total exactly the same score (1/61 + 1/62) by symmetry -- tie broken by Guid ascending.
        var list1 = new List<Guid> { a, b };
        var list2 = new List<Guid> { b, a };

        var result = ReciprocalRankFusion.Combine([list1, list2], topK: 10, k: 60);

        result.Should().HaveCount(2);
        var expected = a.CompareTo(b) < 0 ? new[] { a, b } : new[] { b, a };
        result.Should().Equal(expected);
    }

    [Fact]
    public void Combine_KParameter_IsActuallyUsedInTheFormula()
    {
        var a = Guid.NewGuid(); // rank 1 in list1 only: score = 1/(k+1)
        var b = Guid.NewGuid(); // rank 2 in list1, rank 2 in list2: score = 2/(k+2)

        var list1 = new List<Guid> { a, b };
        var list2 = new List<Guid> { Guid.NewGuid(), b };

        // At k=0 exactly, 1/(0+1) == 2/(0+2) -- an exact tie, broken by Guid ascending. For any k>0
        // (including the default), b's two-list agreement wins outright. Seeing the tie appear
        // specifically at k=0 and disappear at a positive k proves k is wired into the formula, not
        // ignored. list2's own rank-1 filler also scores 1/(0+1) at k=0 (same magnitude as a's own
        // single-list rank-1 score) -- filter the combined result down to just {a, b} before
        // comparing so that unrelated three-way tie doesn't make this assertion order-dependent on
        // an id this test doesn't otherwise care about.
        var atKZero = ReciprocalRankFusion.Combine([list1, list2], topK: 10, k: 0)
            .Where(id => id == a || id == b).ToList();
        var expectedTieOrder = a.CompareTo(b) < 0 ? new[] { a, b } : new[] { b, a };
        atKZero.Should().Equal(expectedTieOrder);

        var atDefaultK = ReciprocalRankFusion.Combine([list1, list2], topK: 10);
        atDefaultK[0].Should().Be(b, "for any k > 0, two-list agreement at rank 2 outscores single-list rank 1");
    }
}
