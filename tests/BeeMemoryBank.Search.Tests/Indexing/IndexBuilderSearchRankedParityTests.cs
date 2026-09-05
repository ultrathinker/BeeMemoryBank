using System.Text;
using BeeMemoryBank.Search.Indexing;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// The load-bearing correctness test for the search-scale performance refactor of
/// <see cref="IndexBuilder.SearchRanked"/>: a differential oracle proving the new, efficient
/// implementation returns results that are BIT-FOR-BIT IDENTICAL to the retained authoritative
/// reference (<c>IndexBuilder.SearchRankedReference</c>, the unchanged original algorithm) -- same
/// articleIds, same BM25 scores (compared on their raw IEEE bits, not approximately), and the same
/// descending-score order INCLUDING tie ordering and top-K truncation.
///
/// <para>
/// It runs many randomized corpora (seeded, deterministic) built with small vocabularies and short
/// documents so that conjunctive-AND candidate sets, score ties, and tie-at-the-top-K-boundary
/// cases actually occur, and it exercises the hot buffer, sealed segments, seals, merges, and
/// tombstones together via a churn phase -- then compares the two implementations across a battery
/// of representative queries (single selective term, single broad term, multi-term AND, mixed, and
/// non-existent terms) at several <c>topK</c> values. A handful of explicit hand-built scenarios
/// pin down the individual representative query shapes named in the brief on their own.
/// </para>
/// </summary>
public class IndexBuilderSearchRankedParityTests
{
    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();

    private static string Stem(string word) => Stemmer.Stem(Tokenizer.Tokenize(word).First());

    /// <summary>
    /// Asserts the production and reference implementations agree exactly: same count, same articleId
    /// at every position, and the same score down to the raw float bits at every position.
    /// </summary>
    private static void AssertIdentical(
        IReadOnlyList<(Guid ArticleId, float Score)> actual,
        IReadOnlyList<(Guid ArticleId, float Score)> expected,
        string because)
    {
        actual.Count.Should().Be(expected.Count, "result counts must match ({0})", because);
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].ArticleId.Should().Be(expected[i].ArticleId,
                "articleId at position {0} must match ({1})", i, because);

            // Bit-for-bit float comparison: the refactor must not perturb the BM25 arithmetic at all.
            BitConverter.SingleToInt32Bits(actual[i].Score).Should().Be(
                BitConverter.SingleToInt32Bits(expected[i].Score),
                "score at position {0} (articleId {1}) must be bit-identical ({2})",
                i, expected[i].ArticleId, because);
        }
    }

    private static void AssertQueriesIdentical(IndexBuilder builder, IEnumerable<string[]> queries, string scenario)
    {
        int[] topKs = [1, 2, 3, 5, 10, 1000];
        foreach (string[] query in queries)
        {
            foreach (int topK in topKs)
            {
                IReadOnlyList<(Guid, float)> expected = builder.SearchRankedReference(query, topK);
                IReadOnlyList<(Guid, float)> actual = builder.SearchRanked(query, topK);
                AssertIdentical(actual, expected,
                    $"{scenario}: query [{string.Join(", ", query)}] topK={topK}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(20260901)]
    [InlineData(20260905)]
    public void SearchRanked_MatchesReference_AcrossRandomizedCorporaAndQueries(int seed)
    {
        var random = new Random(seed);

        // Small vocabulary + short documents => frequent ties and non-trivial AND-candidate sets.
        // Digit-free nonsense words (stemmed exactly as the query pipeline would) to avoid any
        // stemmer edge case; vocab[0] is deliberately biased common in RandomBody so the "single
        // broad term" and "AND with a broad term" paths are exercised, alongside rarer ones.
        string[] vocabRaw =
        [
            "zzalpha", "zzbeta", "zzgamma", "zzdelta", "zzepsilon",
            "zzzeta", "zzeta", "zztheta", "zziota", "zzkappa",
        ];
        string[] vocab = vocabRaw.Select(Stem).ToArray();

        // Randomize the lifecycle thresholds so different runs stress hot-only, sealed, seal+merge,
        // and heavy-tombstone states.
        int sealThreshold = 5 + random.Next(60);
        int mergeCount = 2 + random.Next(6);
        double mergeFraction = 0.1 + random.NextDouble() * 0.4;
        var builder = new IndexBuilder(
            hotBufferSealThreshold: sealThreshold,
            mergeSegmentCountThreshold: mergeCount,
            mergeTombstoneFractionThreshold: mergeFraction);

        var live = new List<Guid>();

        // Initial population.
        int initial = 200 + random.Next(400);
        for (int i = 0; i < initial; i++)
        {
            Guid id = Guid.NewGuid();
            builder.AddOrUpdateDocument(id, Guid.NewGuid(), RandomBody(random, vocab));
            live.Add(id);
        }

        // Churn: updates, deletes, and fresh adds -- forces tombstones and merges on top of the seals.
        int churn = 150 + random.Next(350);
        for (int i = 0; i < churn; i++)
        {
            double roll = random.NextDouble();
            if (roll < 0.5 && live.Count > 0)
            {
                Guid id = live[random.Next(live.Count)];
                builder.AddOrUpdateDocument(id, Guid.NewGuid(), RandomBody(random, vocab));
            }
            else if (roll < 0.75 && live.Count > 0)
            {
                int idx = random.Next(live.Count);
                builder.RemoveDocument(live[idx]);
                live.RemoveAt(idx);
            }
            else
            {
                Guid id = Guid.NewGuid();
                builder.AddOrUpdateDocument(id, Guid.NewGuid(), RandomBody(random, vocab));
                live.Add(id);
            }
        }

        // Query battery: every single term, several random 2- and 3-term combinations (both existing
        // and, occasionally, including a never-indexed term to exercise the empty-result path), and a
        // duplicate-term query (must be de-duplicated exactly like the reference does).
        var queries = new List<string[]>();
        foreach (string term in vocab)
        {
            queries.Add([term]);
        }

        queries.Add([vocab[0], vocab[0]]); // duplicate term
        queries.Add([Stem("zznever")]);     // never indexed
        queries.Add([vocab[1], Stem("zznever")]);

        for (int i = 0; i < 40; i++)
        {
            int size = 2 + random.Next(3); // 2..4 terms
            var terms = new List<string>();
            for (int j = 0; j < size; j++)
            {
                terms.Add(vocab[random.Next(vocab.Length)]);
            }

            queries.Add(terms.ToArray());
        }

        AssertQueriesIdentical(builder, queries, $"seed {seed}");
    }

    [Fact]
    public void SearchRanked_MatchesReference_ForSingleBroadTerm_WithHeavyTieBoundary()
    {
        // A large corpus where one term is in ~80% of documents with only a few distinct term
        // frequencies, so many documents tie on score and the top-K boundary lands squarely inside a
        // tie -- the case most likely to expose any tie-ordering/truncation divergence.
        var random = new Random(4242);
        string broad = Stem("zzbroad");
        string filler = Stem("zzfiller");

        var builder = new IndexBuilder(hotBufferSealThreshold: 128, mergeSegmentCountThreshold: 4);
        for (int i = 0; i < 2000; i++)
        {
            var sb = new StringBuilder();
            int tf = 1 + random.Next(3); // 1..3 occurrences => lots of ties
            if (random.NextDouble() < 0.8)
            {
                for (int k = 0; k < tf; k++)
                {
                    sb.Append("zzbroad ");
                }
            }

            int fillers = random.Next(4);
            for (int k = 0; k < fillers; k++)
            {
                sb.Append("zzfiller ");
            }

            builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), sb.ToString());
        }

        foreach (int topK in new[] { 1, 5, 17, 50, 500, 5000 })
        {
            AssertIdentical(
                builder.SearchRanked([broad], topK),
                builder.SearchRankedReference([broad], topK),
                $"single broad term, topK={topK}");
            AssertIdentical(
                builder.SearchRanked([broad, filler], topK),
                builder.SearchRankedReference([broad, filler], topK),
                $"broad AND filler, topK={topK}");
        }
    }

    [Fact]
    public void SearchRanked_MatchesReference_ForRepresentativeQueryShapes()
    {
        // One hand-built corpus, then the four representative query shapes the brief names, each
        // asserted identical to the reference.
        var builder = new IndexBuilder(hotBufferSealThreshold: 3, mergeSegmentCountThreshold: 2, mergeTombstoneFractionThreshold: 0.5);

        string selective = Stem("zzselective");
        string common = Stem("zzcommon");
        string other = Stem("zzother");

        var ids = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            Guid id = Guid.NewGuid();
            var sb = new StringBuilder();
            sb.Append("zzcommon "); // in every document
            if (i % 7 == 0)
            {
                sb.Append("zzselective zzselective "); // rare
            }

            if (i % 2 == 0)
            {
                sb.Append("zzother ");
            }

            builder.AddOrUpdateDocument(id, Guid.NewGuid(), sb.ToString());
            ids.Add(id);
        }

        // Force a mix of hot-buffer and sealed state, plus a tombstone, then re-add.
        builder.RemoveDocument(ids[3]);
        builder.AddOrUpdateDocument(ids[5], Guid.NewGuid(), "zzcommon zzselective zzother");

        string[][] representative =
        [
            [selective],           // single selective term
            [common],              // single broad term
            [selective, common],   // multi-term AND
            [common, other],       // mixed
            [selective, common, other],
        ];

        foreach (int topK in new[] { 1, 3, 100 })
        {
            foreach (string[] query in representative)
            {
                AssertIdentical(
                    builder.SearchRanked(query, topK),
                    builder.SearchRankedReference(query, topK),
                    $"representative query [{string.Join(", ", query)}] topK={topK}");
            }
        }
    }

    /// <summary>Random short body drawn from <paramref name="vocab"/> with occasional repeats (to create ties/tf variety).</summary>
    private static string RandomBody(Random random, string[] vocab)
    {
        int wordCount = 3 + random.Next(10);
        var sb = new StringBuilder();
        for (int i = 0; i < wordCount; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            // Bias toward the first vocab entry so it becomes a genuinely common ("broad") term.
            int pick = random.NextDouble() < 0.3 ? 0 : random.Next(vocab.Length);
            sb.Append(vocab[pick]);
        }

        return sb.ToString();
    }
}
