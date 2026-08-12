using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// The load-bearing correctness test for WP-12's <see cref="IndexBuilder.SearchRanked"/>: BM25
/// ranking, implicit-AND multi-term semantics, tombstone respect, hot-buffer/sealed-segment
/// tier-correctness, and <c>topK</c> truncation.
///
/// <para>
/// Every hand-checked expected score below reproduces <see cref="IndexBuilder.SearchRanked"/>'s
/// documented formula independently (same <c>k1 = 1.2</c>, <c>b = 0.75</c>, same
/// <c>idf(q) = ln(1 + (N - df + 0.5) / (df + 0.5))</c>), computed from corpus facts this test
/// constructs and therefore knows exactly (document count, term frequencies, document lengths) --
/// not by reading them back out of the builder under test. This is the same
/// independent-oracle spirit as <see cref="IndexBuilderOracleTests"/>, scaled down to
/// individually hand-verifiable cases per the WP-12 brief's requirement for "a few concrete,
/// hand-checkable cases, not just 'it returns something in some order'".
/// </para>
/// </summary>
public class IndexBuilderSearchRankedTests
{
    // Matches IndexBuilder.SearchRanked's private Bm25K1/Bm25B constants -- duplicated here
    // deliberately (not read via reflection) since these are the standard, cited textbook default
    // values (Robertson & Zaragoza 2009), not private implementation details.
    private const double K1 = 1.2;
    private const double B = 0.75;

    // IndexBuilder.SearchRanked expects already-tokenized-and-stemmed terms, exactly like Lookup --
    // never a raw word. See IndexBuilderTests' identical helper/comment for why.
    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();

    private static string Stem(string word) => Stemmer.Stem(Tokenizer.Tokenize(word).First());

    private static double ExpectedIdf(int corpusSize, int documentFrequency) =>
        Math.Log(1.0 + (corpusSize - documentFrequency + 0.5) / (documentFrequency + 0.5));

    [Fact]
    public void SearchRanked_ReturnsEveryDocumentContainingAllQueryTerms_ImplicitAnd()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string alpha = Stem("zzzalpha");
        string beta = Stem("zzzbeta");

        Guid docBoth1 = Guid.NewGuid();
        Guid docBoth2 = Guid.NewGuid();
        Guid docAlphaOnly = Guid.NewGuid();
        Guid docBetaOnly = Guid.NewGuid();

        builder.AddOrUpdateDocument(docBoth1, Guid.NewGuid(), "zzzalpha zzzbeta zzzgamma");
        builder.AddOrUpdateDocument(docBoth2, Guid.NewGuid(), "zzzalpha zzzbeta zzzalpha");
        builder.AddOrUpdateDocument(docAlphaOnly, Guid.NewGuid(), "zzzalpha zzzgamma");
        builder.AddOrUpdateDocument(docBetaOnly, Guid.NewGuid(), "zzzbeta zzzgamma");

        var results = builder.SearchRanked([alpha, beta], topK: 10);

        results.Select(r => r.ArticleId).Should().BeEquivalentTo(
            [docBoth1, docBoth2],
            "only documents containing every distinct query term qualify under implicit-AND semantics -- "
                + "a document missing even one query term must never appear, regardless of how well it "
                + "matches the others");
    }

    [Fact]
    public void SearchRanked_RarerQueryTerm_ScoresSameDocumentHigherThanCommonTerm()
    {
        // Isolates the idf effect: `target` has term frequency 1 for both "rare" and "common", so
        // any score difference between the two single-term queries below comes purely from
        // df(rare) < df(common) -> idf(rare) > idf(common) -- the standard BM25 "rarer term matters
        // more" intuition, checked as two separate single-term queries (rather than folded into one
        // AND query) precisely so no other factor (candidate set, avgdl, N) differs between them.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string rare = Stem("zzzrareterm");
        string common = Stem("zzzcommonterm");

        Guid target = Guid.NewGuid();
        builder.AddOrUpdateDocument(target, Guid.NewGuid(), "zzzrareterm zzzcommonterm zzzfiller");

        // "common" appears in several other documents (df grows); "rare" appears only in `target`.
        for (int i = 0; i < 8; i++)
        {
            builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzcommonterm zzzother");
        }

        float rareScore = builder.SearchRanked([rare], topK: 10).Single(r => r.ArticleId == target).Score;
        float commonScore = builder.SearchRanked([common], topK: 10).Single(r => r.ArticleId == target).Score;

        rareScore.Should().BeGreaterThan(commonScore,
            "the same document with the same term frequency (1) must score higher for a rarer query term");
    }

    [Fact]
    public void SearchRanked_HigherTermFrequency_ScoresHigherThanLowerTermFrequency_SameDocLength()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string target = Stem("zzztargetword");

        Guid highTf = Guid.NewGuid();
        Guid lowTf = Guid.NewGuid();

        // Both documents have the same total length (5 words); only the term-frequency mix differs.
        builder.AddOrUpdateDocument(highTf, Guid.NewGuid(), "zzztargetword zzztargetword zzztargetword zzzfillerword zzzfillerword");
        builder.AddOrUpdateDocument(lowTf, Guid.NewGuid(), "zzztargetword zzzfillerword zzzfillerword zzzfillerword zzzfillerword");

        var results = builder.SearchRanked([target], topK: 10);

        results[0].ArticleId.Should().Be(highTf, "the higher-term-frequency document must rank first");
        float highScore = results.Single(r => r.ArticleId == highTf).Score;
        float lowScore = results.Single(r => r.ArticleId == lowTf).Score;
        highScore.Should().BeGreaterThan(lowScore);

        // Hand-checked exact values: N=2, avgdl=(5+5)/2=5, df(target)=2 (both docs contain it),
        // lengthRatio=5/5=1 for both (equal length), so the only difference is tf.
        double idf = ExpectedIdf(corpusSize: 2, documentFrequency: 2);
        double denomHigh = 3 + K1 * (1 - B + B * 1.0);
        double denomLow = 1 + K1 * (1 - B + B * 1.0);
        double expectedHigh = idf * (3 * (K1 + 1)) / denomHigh;
        double expectedLow = idf * (1 * (K1 + 1)) / denomLow;

        highScore.Should().BeApproximately((float)expectedHigh, 1e-4f);
        lowScore.Should().BeApproximately((float)expectedLow, 1e-4f);
    }

    [Fact]
    public void SearchRanked_TombstonedDocument_InHotBuffer_NeverAppears()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string term = Stem("zzzephemeral");
        Guid id = Guid.NewGuid();
        builder.AddOrUpdateDocument(id, Guid.NewGuid(), "zzzephemeral content");

        builder.RemoveDocument(id);

        builder.SearchRanked([term], topK: 10).Should().BeEmpty();
    }

    [Fact]
    public void SearchRanked_TombstonedDocument_InSealedSegment_NeverAppears()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        string term = Stem("zzzsealedterm");
        Guid id = Guid.NewGuid();
        builder.AddOrUpdateDocument(id, Guid.NewGuid(), "zzzsealedterm content");
        builder.SealedSegmentCount.Should().Be(1, "hotBufferSealThreshold: 1 must have sealed this document immediately");

        builder.RemoveDocument(id);

        builder.SearchRanked([term], topK: 10).Should().BeEmpty();
    }

    [Fact]
    public void SearchRanked_ScoresTheSameDocumentCorrectly_InHotBufferAndAfterItIsSealed()
    {
        // Deliberately gives `target` a different length than the filler documents so the
        // hot-buffer phase's exact-length normalization and the sealed-segment phase's
        // assumed-average-length normalization produce genuinely different, independently
        // computable scores -- not coincidentally the same value.
        var builder = new IndexBuilder(hotBufferSealThreshold: 5, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        string term = Stem("zzzcarryover");

        Guid target = Guid.NewGuid();
        // length 8, tf(term)=2
        builder.AddOrUpdateDocument(target, Guid.NewGuid(), "zzzcarryover zzzcarryover zzzpad zzzpad zzzpad zzzpad zzzpad zzzpad");
        // three filler documents, length 2 each, term absent
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzpad zzzpad");
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzpad zzzpad");
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzpad zzzpad");

        builder.HotBufferCount.Should().Be(4, "threshold is 5 -- these 4 documents must not have sealed yet");

        var beforeSeal = builder.SearchRanked([term], topK: 10);
        beforeSeal.Should().ContainSingle(r => r.ArticleId == target);

        // N=4, avgdl=(8+2+2+2)/4=3.5, df(term)=1, lengthRatio=8/3.5.
        double idfBefore = ExpectedIdf(corpusSize: 4, documentFrequency: 1);
        double lengthRatioBefore = 8.0 / 3.5;
        double denomBefore = 2 + K1 * (1 - B + B * lengthRatioBefore);
        double expectedBefore = idfBefore * (2 * (K1 + 1)) / denomBefore;
        beforeSeal.Single(r => r.ArticleId == target).Score.Should().BeApproximately((float)expectedBefore, 1e-4f);

        // A 5th document crosses the seal threshold, folding `target` (and the fillers) into a
        // sealed segment.
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzpad zzzpad");
        builder.SealedSegmentCount.Should().Be(1);
        builder.HotBufferCount.Should().Be(0);

        var afterSeal = builder.SearchRanked([term], topK: 10);
        afterSeal.Should().ContainSingle(r => r.ArticleId == target);

        // N=5, df(term) still 1. Per IndexBuilder.SearchRanked's documented approximation, a
        // sealed-segment document's length is assumed to equal avgdl exactly (lengthRatio=1) --
        // deliberately NOT the same normalization factor as the hot-buffer case above, and NOT
        // computed from `target`'s real length (8) or the segment's real avgdl (16/5=3.2).
        double idfAfter = ExpectedIdf(corpusSize: 5, documentFrequency: 1);
        double denomAfter = 2 + K1 * (1 - B + B * 1.0);
        double expectedAfter = idfAfter * (2 * (K1 + 1)) / denomAfter;
        afterSeal.Single(r => r.ArticleId == target).Score.Should().BeApproximately((float)expectedAfter, 1e-4f);

        // Sanity: the two tiers really do produce different scores here -- proving this test
        // actually exercises two different code paths, not the same formula twice by coincidence.
        Math.Abs(expectedBefore - expectedAfter).Should().BeGreaterThan(1e-3,
            "the hot-buffer and sealed-segment formulas must genuinely differ in this scenario");
    }

    [Fact]
    public void SearchRanked_AfterAdoptingPersistedSegment_AvgDocLengthAccountsForAdoptedContent()
    {
        // Regression test for a review finding: AdoptPersistedSegment (WP-11's warm-start path,
        // which folds a segment reloaded from disk back into a live IndexBuilder) must contribute
        // its live documents' total length to the same running total SealLocked/MergeLocked
        // maintain for SearchRanked's avgdl approximation. Without that, avgdl computed right after
        // a warm-start would reflect only the hot buffer's own (typically much shorter) documents,
        // ignoring however much real content was just adopted from disk -- which is the NORMAL
        // state immediately after every process restart with existing indexed content, not an edge
        // case.
        //
        // This is engineered so the bug is not just "a small numeric error" but visibly FLIPS the
        // ranking order between a hot-buffer document and the adopted sealed document: the adopted
        // document is long (100 terms) and the hot-buffer document is short (2 terms), both
        // containing the query term exactly once. Sealed-segment documents are always scored at
        // lengthRatio == 1 regardless of avgdl (see IndexBuilder's own documented approximation),
        // so the adopted document's score is identical whether or not the bug is present -- only
        // the hot-buffer document's score depends on avgdl being correct, via its
        // lengthRatio = |D| / avgdl. Without the fix, avgdl would be (2 + 0) / 2 = 1.0 (the adopted
        // document's length silently missing), making the short hot-buffer document's lengthRatio
        // 2.0 instead of the correct ~0.039 -- inflating its BM25 denominator enough to rank it
        // BELOW the adopted document instead of above it.
        string queryTerm = Stem("zzzquery");

        var adoptedArticle = Guid.NewGuid();
        var adoptedTerms = new List<string> { queryTerm };
        adoptedTerms.AddRange(Enumerable.Repeat("zzzsealedfiller", 99)); // adopted document length: 100
        byte[] bytes = SegmentWriter.Build([new SegmentDocument(0, adoptedArticle, Guid.NewGuid(), adoptedTerms)]);
        var reader = new SegmentReader(bytes);

        var builder = new IndexBuilder(hotBufferSealThreshold: 1000, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        builder.AdoptPersistedSegment(reader, new HashSet<Guid>());
        builder.SealedSegmentCount.Should().Be(1);

        var hotArticle = Guid.NewGuid();
        builder.AddOrUpdateDocument(hotArticle, Guid.NewGuid(), "zzzquery zzzpad"); // hot-buffer document length: 2
        builder.HotBufferCount.Should().Be(1);

        var results = builder.SearchRanked([queryTerm], topK: 10);

        results.Should().HaveCount(2);

        // Hand-computed with the CORRECT avgdl (which must include the adopted document's length):
        // N=2, avgdl=(2+100)/2=51, df(queryTerm)=2 (both documents contain it).
        double idf = ExpectedIdf(corpusSize: 2, documentFrequency: 2);
        double hotLengthRatio = 2.0 / 51.0;
        double denomHot = 1 + K1 * (1 - B + B * hotLengthRatio);
        double expectedHotScore = idf * (1 * (K1 + 1)) / denomHot;
        double denomSealed = 1 + K1 * (1 - B + B * 1.0); // sealed documents are always scored at lengthRatio == 1
        double expectedSealedScore = idf * (1 * (K1 + 1)) / denomSealed;

        expectedHotScore.Should().BeGreaterThan(expectedSealedScore,
            "sanity check on the hand-computed values themselves: this test must actually exercise a "
                + "case where getting avgdl right changes the ranking, not just its magnitude");

        results[0].ArticleId.Should().Be(hotArticle,
            "with avgdl correctly including the adopted segment's length, the short hot-buffer "
                + "document must rank first -- a buggy avgdl missing that contribution would flip this");
        results[1].ArticleId.Should().Be(adoptedArticle);

        results.Single(r => r.ArticleId == hotArticle).Score.Should().BeApproximately((float)expectedHotScore, 1e-4f);
        results.Single(r => r.ArticleId == adoptedArticle).Score.Should().BeApproximately((float)expectedSealedScore, 1e-4f);
    }

    [Fact]
    public void SearchRanked_TopKTruncation_KeepsOnlyTheHighestScoringDocuments()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string term = Stem("zzzranked");

        var idsByTf = new List<(Guid Id, int Tf)>();
        for (int tf = 1; tf <= 6; tf++)
        {
            Guid id = Guid.NewGuid();
            // Every document has the same total length (6 words): same idf/N/lengthRatio for all,
            // so tf alone determines the (strict, tie-free) ordering.
            string body = string.Join(' ', Enumerable.Repeat("zzzranked", tf).Concat(Enumerable.Repeat("zzzpad", 6 - tf)));
            builder.AddOrUpdateDocument(id, Guid.NewGuid(), body);
            idsByTf.Add((id, tf));
        }

        var top3 = builder.SearchRanked([term], topK: 3);

        top3.Should().HaveCount(3, "topK=3 must never return more than 3 results");
        Guid[] expectedTop3 =
        [
            idsByTf.Single(x => x.Tf == 6).Id,
            idsByTf.Single(x => x.Tf == 5).Id,
            idsByTf.Single(x => x.Tf == 4).Id,
        ];
        top3.Select(r => r.ArticleId).Should().Equal(expectedTop3,
            "the 3 kept results must be exactly the 3 highest-tf (== highest-scoring) documents, in descending order");

        var all = builder.SearchRanked([term], topK: 100);
        float minKept = top3.Min(r => r.Score);
        float maxDiscarded = all.Except(top3).Max(r => r.Score);
        minKept.Should().BeGreaterThanOrEqualTo(maxDiscarded, "every kept score must be >= every discarded score");
    }

    [Fact]
    public void SearchRanked_NoDocumentContainsQueryTerm_ReturnsEmpty_NotException()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzsomeword zzzanotherword");

        Action act = () => builder.SearchRanked([Stem("zzznonexistent")], topK: 10);

        act.Should().NotThrow();
        builder.SearchRanked([Stem("zzznonexistent")], topK: 10).Should().BeEmpty();
    }

    [Fact]
    public void SearchRanked_EmptyOrNonPositiveInputs_ReturnEmpty_NotException()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "zzzsomeword");

        builder.SearchRanked([], topK: 10).Should().BeEmpty("no query terms at all can never match anything");
        builder.SearchRanked([Stem("zzzsomeword")], topK: 0).Should().BeEmpty("topK=0 must return nothing");
        builder.SearchRanked([Stem("zzzsomeword")], topK: -1).Should().BeEmpty("a negative topK must return nothing, not throw");
    }

    [Fact]
    public void SearchRanked_EmptyIndex_ReturnsEmpty_NotException()
    {
        var builder = new IndexBuilder();

        Action act = () => builder.SearchRanked([Stem("zzzanything")], topK: 10);

        act.Should().NotThrow();
        builder.SearchRanked([Stem("zzzanything")], topK: 10).Should().BeEmpty();
    }

    [Fact]
    public void SearchRanked_CorpusOfSizeOne_AverageDocumentLengthEqualsThatDocumentsLength_NoNaN()
    {
        // Self-check #5 from the brief: corpus of size one must yield avgdl == that document's own
        // length (so lengthRatio == 1 exactly), and the arithmetic must never NaN.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        string term = Stem("zzzsolo");
        Guid id = Guid.NewGuid();
        builder.AddOrUpdateDocument(id, Guid.NewGuid(), "zzzsolo zzzsolo zzzpad"); // length 3, tf=2

        var results = builder.SearchRanked([term], topK: 10);

        results.Should().ContainSingle();
        float score = results[0].Score;
        float.IsNaN(score).Should().BeFalse();
        float.IsInfinity(score).Should().BeFalse();

        double idf = ExpectedIdf(corpusSize: 1, documentFrequency: 1);
        double denom = 2 + K1 * (1 - B + B * 1.0); // lengthRatio == 1: the only document IS the average
        double expected = idf * (2 * (K1 + 1)) / denom;
        score.Should().BeApproximately((float)expected, 1e-4f);
    }

    [Fact]
    public void SearchRanked_DocumentWithZeroTerms_DoesNotCrash_AndNeverMatches()
    {
        // Self-check #5 from the brief: a document with zero terms (should not happen in practice,
        // but must not crash the length-normalization arithmetic) alongside a normal document.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        Guid emptyDoc = Guid.NewGuid();
        Guid normalDoc = Guid.NewGuid();

        builder.AddOrUpdateDocument(emptyDoc, Guid.NewGuid(), ""); // tokenizes to zero terms
        builder.AddOrUpdateDocument(normalDoc, Guid.NewGuid(), "zzzcontent zzzcontent");

        Action act = () => builder.SearchRanked([Stem("zzzcontent")], topK: 10);
        act.Should().NotThrow();

        var results = builder.SearchRanked([Stem("zzzcontent")], topK: 10);
        results.Select(r => r.ArticleId).Should().NotContain(emptyDoc, "a document with no terms can never match any non-empty query");
        results.Select(r => r.ArticleId).Should().Contain(normalDoc);
        float.IsNaN(results.Single(r => r.ArticleId == normalDoc).Score).Should().BeFalse();
    }

    [Fact]
    public void SearchRanked_CorpusIsSingleZeroTermDocument_DoesNotDivideByZeroOrCrash()
    {
        // The pathological corner of the two self-checks above combined: avgdl's naive computation
        // (0 total length / 1 document) would be exactly 0 without IndexBuilder's defensive guard.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "");

        Action act = () => builder.SearchRanked([Stem("zzzanything")], topK: 10);

        act.Should().NotThrow();
        builder.SearchRanked([Stem("zzzanything")], topK: 10).Should().BeEmpty();
    }
}
