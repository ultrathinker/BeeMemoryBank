using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The scoring rules of the related list (<see cref="RelatedArticleRanker"/>), without a database:
/// rarity weights, the commonplace cut-off, how the tag part and the meaning part are scaled and combined,
/// and what each row says about why it is listed.
/// </summary>
public class RelatedArticleRankerTests
{
    private static RelatedCandidate Tagged(string title, params (string Name, double Weight)[] tags) =>
        new(Guid.NewGuid(), title, "/", tags, null, 0f);

    private static RelatedCandidate Near(string title, float similarity, float z, params (string Name, double Weight)[] tags) =>
        new(Guid.NewGuid(), title, "/", tags, similarity, z);

    // ---- weights -------------------------------------------------------------------------------------------------

    [Fact]
    public void ARarerTagWeighsMore()
    {
        RelatedArticleRanker.TagWeight(1000, 3).Should().BeGreaterThan(RelatedArticleRanker.TagWeight(1000, 30));
    }

    [Fact]
    public void WeightIsTheNaturalLogOfTheInverseFrequency()
    {
        RelatedArticleRanker.TagWeight(99, 4).Should().BeApproximately(Math.Log(100.0 / 5.0), 1e-9);
    }

    [Theory]
    [InlineData(100, 20, false)]   // the floor of 20 articles protects a small vault
    [InlineData(100, 21, true)]
    [InlineData(1000, 50, false)]  // 5% of 1000 is 50
    [InlineData(1000, 51, true)]
    [InlineData(10000, 500, false)]
    [InlineData(10000, 501, true)]
    public void ATagOnMoreThanTheLargerOfTwentyAndFivePercentIsACommonplace(int vault, int df, bool commonplace)
    {
        RelatedArticleRanker.IsCommonplace(vault, df).Should().Be(commonplace);
        if (commonplace)
            RelatedArticleRanker.TagWeight(vault, df).Should().Be(0);
        else
            RelatedArticleRanker.TagWeight(vault, df).Should().BeGreaterThan(0);
    }

    [Fact]
    public void ATagEveryArticleCarriesWeighsNothing()
    {
        // df == N: ln((N + 1) / (N + 1)) = 0, even inside the small-vault floor.
        RelatedArticleRanker.TagWeight(5, 5).Should().Be(0);
    }

    // ---- ranking -------------------------------------------------------------------------------------------------

    [Fact]
    public void ACandidateWithNeitherAWeightedTagNorNearnessIsNotRelated()
    {
        var rows = RelatedArticleRanker.Rank([Tagged("lonely")], 7);
        rows.Should().BeEmpty();
    }

    [Fact]
    public void TheBestTagRowOfATagsOnlyListScoresSixty()
    {
        var rows = RelatedArticleRanker.Rank([Tagged("a", ("x", 2.0)), Tagged("b", ("y", 1.0))], 7);

        rows.Select(r => r.Title).Should().Equal("a", "b");
        rows[0].Strength.Should().Be(60);   // 0.6 * (2/2)
        rows[1].Strength.Should().Be(30);   // 0.6 * (1/2)
    }

    [Fact]
    public void TheBestMeaningRowOfAMeaningOnlyListScoresForty()
    {
        var rows = RelatedArticleRanker.Rank([Near("a", 0.93f, 4f), Near("b", 0.90f, 2f)], 7);

        rows.Select(r => r.Title).Should().Equal("a", "b");
        rows[0].Strength.Should().Be(40);   // 0.4 * (4/4)
        rows[1].Strength.Should().Be(20);   // 0.4 * (2/4)
    }

    [Fact]
    public void AnArticleThatIsBestByTagsAndByMeaningScoresAHundred()
    {
        var rows = RelatedArticleRanker.Rank([Near("both", 0.96f, 5f, ("rare", 3.0)), Tagged("tags", ("rare", 3.0))], 7);

        rows[0].Title.Should().Be("both");
        rows[0].Strength.Should().Be(100);
        rows[1].Strength.Should().Be(60);
    }

    [Fact]
    public void TagsAndMeaningAddUp()
    {
        // 0.6 * (1/2) + 0.4 * (2/2) = 0.7
        var rows = RelatedArticleRanker.Rank([Tagged("big", ("x", 2.0)), Near("mixed", 0.9f, 3f, ("y", 1.0))], 7);

        rows.Single(r => r.Title == "mixed").Strength.Should().Be(70);
    }

    [Fact]
    public void ARowWithAnyPositiveTotalShowsAtLeastOne()
    {
        // 0.6 * (0.0001 / 100) is far below half a percent.
        var rows = RelatedArticleRanker.Rank([Tagged("big", ("x", 100.0)), Tagged("tiny", ("y", 0.0001))], 7);

        rows.Single(r => r.Title == "tiny").Strength.Should().Be(1);
    }

    [Fact]
    public void TheListIsCutToTheLimit_StrongestFirst()
    {
        var candidates = Enumerable.Range(1, 30)
            .Select(i => Tagged($"article {i:00}", ("t", i)))
            .ToList();

        var rows = RelatedArticleRanker.Rank(candidates, 7);

        rows.Should().HaveCount(7);
        rows.Select(r => r.Title).Should().Equal("article 30", "article 29", "article 28", "article 27", "article 26", "article 25", "article 24");
        rows.Select(r => r.Strength).Should().BeInDescendingOrder();
        rows.Should().OnlyContain(r => r.Strength >= 1 && r.Strength <= 100);
    }

    [Fact]
    public void EqualScoresComeOutInTheSameOrderEveryTime()
    {
        var candidates = new[] { Tagged("beta", ("t", 1.0)), Tagged("Alpha", ("t", 1.0)), Tagged("gamma", ("t", 1.0)) };

        var first = RelatedArticleRanker.Rank(candidates, 7).Select(r => r.Id).ToList();
        var second = RelatedArticleRanker.Rank(candidates.Reverse().ToList(), 7).Select(r => r.Id).ToList();

        first.Should().Equal(second);
        RelatedArticleRanker.Rank(candidates, 7).Select(r => r.Title).Should().Equal("Alpha", "beta", "gamma");
    }

    // ---- what a row says -----------------------------------------------------------------------------------------

    [Fact]
    public void AtMostThreeSharedTagsAreNamed_RarestFirst()
    {
        var row = RelatedArticleRanker.Rank([Tagged("a", ("common", 0.5), ("rare", 4.0), ("mid", 2.0), ("rarer", 5.0), ("odd", 1.0))], 7).Single();

        row.SharedConcepts.Should().Equal("rarer", "rare", "mid");
        row.Reasons.Should().Equal("shared tag: rarer", "shared tag: rare", "shared tag: mid");
    }

    [Fact]
    public void ARowCloseByMeaningSaysSoWithThePercent()
    {
        var row = RelatedArticleRanker.Rank([Near("a", 0.874f, 3f)], 7).Single();

        row.SemanticPercent.Should().Be(87);
        row.PossibleDuplicate.Should().BeFalse();
        row.SharedConcepts.Should().BeEmpty();
        row.Reasons.Should().Equal("similar meaning: 87%");
    }

    [Theory]
    [InlineData(0.949f, false)]
    [InlineData(0.95f, true)]
    [InlineData(0.99f, true)]
    public void SimilarityOfNinetyFivePercentOrMoreFlagsAPossibleDuplicate(float similarity, bool flagged)
    {
        var row = RelatedArticleRanker.Rank([Near("a", similarity, 5f)], 7).Single();

        row.PossibleDuplicate.Should().Be(flagged);
        row.Reasons.Contains("possible duplicate").Should().Be(flagged);
    }

    [Fact]
    public void ARowFoundOnlyByTagsHasNoMeaningPart()
    {
        var row = RelatedArticleRanker.Rank([Tagged("a", ("x", 1.0))], 7).Single();

        row.SemanticPercent.Should().BeNull();
        row.PossibleDuplicate.Should().BeFalse();
        row.Reasons.Should().Equal("shared tag: x");
    }
}
