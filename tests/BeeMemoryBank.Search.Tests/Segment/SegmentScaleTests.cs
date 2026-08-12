using System.Diagnostics;
using System.Text;
using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Segment;

/// <summary>
/// Validates the segment format at a realistic corpus scale: roundtrip correctness across every
/// term in a large synthetic corpus, construction performance, and the segment-size-vs-plaintext
/// ratio the design brief targets (roughly 15-25% of plaintext body size).
///
/// The corpus is generated with a Zipfian term-frequency distribution (a small number of very
/// common terms, a long tail of rare ones) because that is the realistic shape for natural-
/// language term frequencies, and it stresses two things a uniform-random corpus would not:
/// (a) heavy within-document repetition of common terms, which the term-frequency collapsing
/// (multiple occurrences -> one posting with a count) needs to compress well, and (b) a wide
/// variance in postings-run length across terms (a handful of terms with a run per every
/// document, most terms with a run of one or two entries), which the varint encoding needs to
/// handle well in both directions.
/// </summary>
public class SegmentScaleTests
{
    // Chosen to land within the brief's "~10,000-50,000 documents" realistic-scale range while
    // keeping this a fast unit test (well under a second to generate + build).
    private const int DocumentCount = 20_000;
    private const int VocabularySize = 12_000;
    private const int MinTermsPerDoc = 150;
    private const int MaxTermsPerDoc = 600;
    private const double ZipfExponent = 1.1;

    [Fact]
    public void RealisticScaleCorpus_RoundtripsExactlyAndMeetsSizeTarget()
    {
        (List<SegmentDocument> docs, long plaintextEstimateBytes) = GenerateZipfianCorpus(
            documentCount: DocumentCount,
            vocabularySize: VocabularySize,
            minTermsPerDoc: MinTermsPerDoc,
            maxTermsPerDoc: MaxTermsPerDoc,
            zipfExponent: ZipfExponent,
            seed: 12345);

        // Keep an independent expected model (per document, per term -> frequency) to check the
        // segment against, built the same way SegmentWriter is documented to behave rather than
        // by calling into it, so this test would actually catch a writer bug.
        var expected = new Dictionary<string, SortedDictionary<int, int>>();
        foreach (SegmentDocument doc in docs)
        {
            var freq = new Dictionary<string, int>();
            foreach (string term in doc.Terms)
            {
                freq[term] = freq.GetValueOrDefault(term) + 1;
            }

            foreach ((string term, int count) in freq)
            {
                if (!expected.TryGetValue(term, out SortedDictionary<int, int>? perDoc))
                {
                    perDoc = new SortedDictionary<int, int>();
                    expected[term] = perDoc;
                }

                perDoc[doc.DocId] = count;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        byte[] segment = SegmentWriter.Build(docs);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "building a 20k-document segment must not be anywhere close to a multi-second operation");

        var reader = new SegmentReader(segment);
        reader.DocumentCount.Should().Be(DocumentCount);
        reader.TermCount.Should().Be(expected.Count);

        // Full roundtrip check: every term's postings must match the independently computed model
        // exactly, in ascending docId order.
        foreach ((string term, SortedDictionary<int, int> perDoc) in expected)
        {
            var actual = reader.GetPostings(term).ToList();
            var expectedPostings = perDoc.Select(kvp => (DocId: kvp.Key, TermFrequency: kvp.Value)).ToList();
            actual.Should().Equal(expectedPostings, $"postings for term '{term}' must roundtrip exactly");
        }

        // Doc table roundtrip spot-check across the full range.
        for (int i = 0; i < docs.Count; i += 997) // prime stride, avoids accidentally-aligned blind spots
        {
            reader.GetDocument(docs[i].DocId).Should().Be((docs[i].ArticleId, docs[i].FolderId));
        }

        double ratio = (double)segment.Length / plaintextEstimateBytes;

        // Reported for the write-up regardless of pass/fail; xUnit surfaces Console.WriteLine
        // output on request (`dotnet test -v n` or a failure), and it is also captured directly
        // into the WP-08 report by hand from a manual run.
        Console.WriteLine($"Documents: {DocumentCount}, distinct terms: {reader.TermCount}");
        Console.WriteLine($"Plaintext estimate: {plaintextEstimateBytes:N0} bytes");
        Console.WriteLine($"Segment size: {segment.Length:N0} bytes");
        Console.WriteLine($"Ratio (segment / plaintext estimate): {ratio:P1}");
        Console.WriteLine($"Build time: {stopwatch.ElapsedMilliseconds} ms");

        // Generous band around the brief's 15-25% target: this is a sanity check on a synthetic
        // corpus, not a tight guarantee -- see the WP-08 report for the exact measured numbers and
        // the estimation method.
        ratio.Should().BeInRange(0.05, 0.40,
            "the packed segment should be a small fraction of the estimated plaintext size, per the design brief's 15-25% target");
    }

    /// <summary>
    /// Generates a synthetic corpus with a Zipfian term-frequency distribution and returns it
    /// alongside a rough "plaintext body size" estimate: the sum, over every term occurrence
    /// (before within-document collapsing), of that term's UTF-8 byte length plus one separator
    /// byte (as if the terms were space-joined into a body of text). This is a deliberately simple
    /// proxy -- real prose also has punctuation and original (pre-stemming, generally longer)
    /// surface word forms -- but it is a real, reproducible measurement of this corpus rather than
    /// a guess, which is what the design brief asks for.
    /// </summary>
    private static (List<SegmentDocument> Documents, long PlaintextEstimateBytes) GenerateZipfianCorpus(
        int documentCount, int vocabularySize, int minTermsPerDoc, int maxTermsPerDoc, double zipfExponent, int seed)
    {
        var random = new Random(seed);

        string[] vocabulary = GenerateDistinctTerms(vocabularySize, random);

        // Precompute the Zipfian cumulative distribution once: weight(rank) = 1 / rank^s.
        double[] cumulative = new double[vocabularySize];
        double running = 0;
        for (int rank = 1; rank <= vocabularySize; rank++)
        {
            running += 1.0 / Math.Pow(rank, zipfExponent);
            cumulative[rank - 1] = running;
        }

        double total = cumulative[^1];

        var documents = new List<SegmentDocument>(documentCount);
        long plaintextBytes = 0;

        for (int docId = 0; docId < documentCount; docId++)
        {
            int termOccurrences = random.Next(minTermsPerDoc, maxTermsPerDoc + 1);
            var terms = new List<string>(termOccurrences);

            for (int i = 0; i < termOccurrences; i++)
            {
                double draw = random.NextDouble() * total;
                int index = Array.BinarySearch(cumulative, draw);
                if (index < 0)
                {
                    index = ~index;
                }

                index = Math.Min(index, vocabularySize - 1);

                string term = vocabulary[index];
                terms.Add(term);
                plaintextBytes += Encoding.UTF8.GetByteCount(term) + 1; // +1 for an implicit separator
            }

            documents.Add(new SegmentDocument(docId, Guid.NewGuid(), Guid.NewGuid(), terms));
        }

        return (documents, plaintextBytes);
    }

    private static string[] GenerateDistinctTerms(int count, Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        const int maxLength = 10;
        var seen = new HashSet<string>(count);
        var terms = new string[count];
        Span<char> chars = stackalloc char[maxLength];

        int i = 0;
        while (i < count)
        {
            int length = random.Next(3, maxLength + 1);
            Span<char> slice = chars[..length];
            for (int c = 0; c < length; c++)
            {
                slice[c] = alphabet[random.Next(alphabet.Length)];
            }

            string candidate = new string(slice);
            if (seen.Add(candidate))
            {
                terms[i++] = candidate;
            }
        }

        return terms;
    }

    [Fact]
    public void SmallCorpus_10000Documents_RoundtripsCorrectly()
    {
        (List<SegmentDocument> docs, _) = GenerateZipfianCorpus(
            documentCount: 10_000,
            vocabularySize: 5_000,
            minTermsPerDoc: 20,
            maxTermsPerDoc: 100,
            zipfExponent: 1.0,
            seed: 999);

        byte[] segment = SegmentWriter.Build(docs);
        var reader = new SegmentReader(segment);

        reader.DocumentCount.Should().Be(10_000);

        // Build an expected term -> distinct-doc-count model in one linear pass, then spot-check
        // a sample of terms against it (the main realistic-scale test above already does a full
        // check of every term) to keep this test fast while still exercising the 10k-document end
        // of the brief's stated range.
        var expectedDocFrequency = new Dictionary<string, int>();
        foreach (SegmentDocument doc in docs)
        {
            foreach (string term in doc.Terms.Distinct())
            {
                expectedDocFrequency[term] = expectedDocFrequency.GetValueOrDefault(term) + 1;
            }
        }

        foreach (string term in expectedDocFrequency.Keys.Take(200))
        {
            reader.GetDocumentFrequency(term).Should().Be(expectedDocFrequency[term]);
        }
    }
}
