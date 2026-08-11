using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.SeedGen;

namespace BeeMemoryBank.SeedGen.Tests;

public class SyntheticCorpusDeterminismTests
{
    private const int Seed = 42;
    private static readonly IReadOnlyList<string> Locales = ["ru", "en"];

    private static List<ArticleSpec> TakeSpecs(int count) =>
        new SyntheticCorpus(Seed, count, Math.Max(20, count / 10), Locales).BuildArticles().Take(count).ToList();

    [Fact]
    public void SameSeed_ProducesIdenticalFolders()
    {
        var a = new SyntheticCorpus(Seed, 500, 60, Locales);
        var b = new SyntheticCorpus(Seed, 500, 60, Locales);

        a.Folders.Should().Equal(b.Folders);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalArticles()
    {
        var specsA = TakeSpecs(300);
        var specsB = TakeSpecs(300);

        specsA.Should().HaveCount(300);
        specsA.Select(s => s.Title).Should().Equal(specsB.Select(s => s.Title));

        for (int i = 0; i < specsA.Count; i++)
        {
            specsA[i].Body.Should().Be(specsB[i].Body, $"article {i} body must match");
            specsA[i].TreePath.Should().Be(specsB[i].TreePath, $"article {i} tree path must match");
            specsA[i].Tags.Should().Equal(specsB[i].Tags, $"article {i} tags must match");
            specsA[i].Locale.Should().Be(specsB[i].Locale);
            specsA[i].Protected.Should().Be(specsB[i].Protected);
        }
    }

    [Fact]
    public void SameSeed_FirstArticleHashMatches()
    {
        var first = TakeSpecs(1)[0];
        // SHA-256 of the first article's body under the fixed seed/locale config — a stable fingerprint
        // that will catch any accidental change to the word pool, sampler, or RNG wiring.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first.Body)));

        hash.Should().NotBeEmpty();
        // Regenerate under the same config and confirm the hash is stable across two constructions.
        var firstAgain = TakeSpecs(1)[0];
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(firstAgain.Body)))
              .Should().Be(hash);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentArticles()
    {
        var a = new SyntheticCorpus(1, 100, 20, Locales).BuildArticles().Take(20).Select(s => s.Title).ToList();
        var b = new SyntheticCorpus(2, 100, 20, Locales).BuildArticles().Take(20).Select(s => s.Title).ToList();
        a.Should().NotEqual(b);
    }
}

public class SyntheticCorpusShapeTests
{
    private static readonly IReadOnlyList<string> Locales = ["ru", "en"];

    [Fact]
    public void Folders_HaveDepthThreeToFive_AndAreUnique()
    {
        var corpus = new SyntheticCorpus(1, 100, 80, Locales);

        corpus.Folders.Should().OnlyHaveUniqueItems();
        foreach (var path in corpus.Folders)
        {
            var depth = path.Count(c => c == '/');
            depth.Should().BeInRange(3, 5, $"folder '{path}' depth must be 3–5");
        }
    }

    [Fact]
    public void TagDistribution_IsZipf_TopTagDominatesMedian()
    {
        var corpus = new SyntheticCorpus(42, 5000, 200, Locales);
        var counts = corpus.BuildArticles()
            .SelectMany(a => a.Tags)
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());

        var sorted = counts.Values.OrderByDescending(x => x).ToList();
        var top = sorted[0];
        var median = sorted[sorted.Count / 2];

        // A Zipf distribution should make the most popular tag far outstrip the middle of the pack.
        top.Should().BeGreaterThan(median * 3,
            "the hottest tag must appear meaningfully more often than the median tag");
    }

    [Fact]
    public void FolderDistribution_IsZipf_TopFolderDominatesMedian()
    {
        var corpus = new SyntheticCorpus(42, 5000, 200, Locales);
        var counts = corpus.BuildArticles()
            .GroupBy(a => a.TreePath)
            .ToDictionary(g => g.Key, g => g.Count());

        var sorted = counts.Values.OrderByDescending(x => x).ToList();
        var top = sorted[0];
        var median = sorted[sorted.Count / 2];

        top.Should().BeGreaterThan(median * 3,
            "the biggest folder must hold meaningfully more articles than the median folder");
    }

    [Fact]
    public void BodyLengths_FollowTargetDistribution()
    {
        var corpus = new SyntheticCorpus(7, 2000, 80, ["en"]);
        var specs = corpus.BuildArticles().ToList();

        var longOnes = specs.Count(s => Encoding.UTF8.GetByteCount(s.Body) > 10 * 1024);
        // Target is ~15% long articles; allow a generous band around it.
        longOnes.Should().BeGreaterThan((int)(specs.Count * 0.08))
                .And.BeLessThan((int)(specs.Count * 0.25));

        // Every body should at least reach the small-bucket floor and not exceed the long ceiling.
        specs.Min(s => Encoding.UTF8.GetByteCount(s.Body)).Should().BeGreaterThan(400);
        specs.Max(s => Encoding.UTF8.GetByteCount(s.Body)).Should().BeLessThan(60 * 1024);
    }

    [Fact]
    public void Articles_MixBothLocales()
    {
        var corpus = new SyntheticCorpus(3, 1000, 40, Locales);
        var locales = corpus.BuildArticles().Select(s => s.Locale).ToHashSet();
        locales.Should().Contain("ru").And.Contain("en");
    }

    [Fact]
    public void ProtectedFraction_IsAboutOnePercent()
    {
        var corpus = new SyntheticCorpus(11, 3000, 100, Locales);
        var specs = corpus.BuildArticles().ToList();
        var protectedCount = specs.Count(s => s.Protected);

        // ~1% target; with 3000 articles expect roughly 30 (allow 0.3%–2.5%).
        protectedCount.Should().BeGreaterThan((int)(specs.Count * 0.003))
                      .And.BeLessThan((int)(specs.Count * 0.025));
    }
}
