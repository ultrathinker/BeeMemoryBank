using System.Buffers.Binary;
using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Segment;

public class SegmentRoundtripTests
{
    [Fact]
    public void Build_ZeroDocuments_ProducesEmptySegmentWithHeaderOnly()
    {
        byte[] segment = SegmentWriter.Build([]);
        var reader = new SegmentReader(segment);

        reader.DocumentCount.Should().Be(0);
        reader.TermCount.Should().Be(0);
        reader.GetPostings("anything").Should().BeEmpty();
    }

    [Fact]
    public void Build_OneDocumentZeroTerms_RoundtripsDocumentWithNoPostings()
    {
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        byte[] segment = SegmentWriter.Build([new SegmentDocument(0, articleId, folderId, [])]);

        var reader = new SegmentReader(segment);

        reader.DocumentCount.Should().Be(1);
        reader.TermCount.Should().Be(0);
        reader.GetDocument(0).Should().Be((articleId, folderId));
    }

    [Fact]
    public void Build_SingleDocumentWithTerms_RoundtripsTermFrequencies()
    {
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, articleId, folderId, ["apple", "banana", "apple", "apple", "cherry"]),
        ]);

        var reader = new SegmentReader(segment);

        reader.GetPostings("apple").Should().Equal((0, 3));
        reader.GetPostings("banana").Should().Equal((0, 1));
        reader.GetPostings("cherry").Should().Equal((0, 1));
        reader.GetDocumentFrequency("apple").Should().Be(1);
    }

    [Fact]
    public void Query_TermThatWasNeverIndexed_ReturnsEmptyNotAnException()
    {
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["hello"]),
        ]);

        var reader = new SegmentReader(segment);

        Action act = () => reader.GetPostings("nonexistent").ToList();
        act.Should().NotThrow();
        reader.GetPostings("nonexistent").Should().BeEmpty();
        reader.GetDocumentFrequency("nonexistent").Should().Be(0);
    }

    [Fact]
    public void Build_TermInEveryDocument_PostingsCoverAllDocsInAscendingOrder()
    {
        var docs = Enumerable.Range(0, 10)
            .Select(i => new SegmentDocument(i, Guid.NewGuid(), Guid.NewGuid(), ["common", $"unique{i}"]))
            .ToList();

        byte[] segment = SegmentWriter.Build(docs);
        var reader = new SegmentReader(segment);

        reader.GetDocumentFrequency("common").Should().Be(10);
        reader.GetPostings("common").Select(p => p.DocId).Should().Equal(Enumerable.Range(0, 10));
        reader.GetPostings("common").Should().OnlyContain(p => p.TermFrequency == 1);
    }

    [Fact]
    public void Build_TermInOnlyOneDocument_PostingsContainExactlyThatDoc()
    {
        var docs = Enumerable.Range(0, 10)
            .Select(i => new SegmentDocument(i, Guid.NewGuid(), Guid.NewGuid(), [$"unique{i}"]))
            .ToList();

        byte[] segment = SegmentWriter.Build(docs);
        var reader = new SegmentReader(segment);

        reader.GetPostings("unique7").Should().Equal((7, 1));
        reader.GetDocumentFrequency("unique7").Should().Be(1);
    }

    [Fact]
    public void Build_DocIdsNotContiguousFromZero_Throws()
    {
        var docs = new[]
        {
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["a"]),
            new SegmentDocument(2, Guid.NewGuid(), Guid.NewGuid(), ["b"]),
        };

        Action act = () => SegmentWriter.Build(docs);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_DocIdsOutOfInputOrder_StillProducesCorrectDocTable()
    {
        var article0 = Guid.NewGuid();
        var article1 = Guid.NewGuid();
        var folder0 = Guid.NewGuid();
        var folder1 = Guid.NewGuid();

        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(1, article1, folder1, ["b"]),
            new SegmentDocument(0, article0, folder0, ["a"]),
        ]);

        var reader = new SegmentReader(segment);
        reader.GetDocument(0).Should().Be((article0, folder0));
        reader.GetDocument(1).Should().Be((article1, folder1));
    }

    [Fact]
    public void GetDocument_OutOfRangeDocId_Throws()
    {
        byte[] segment = SegmentWriter.Build([new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["a"])]);
        var reader = new SegmentReader(segment);

        Action tooHigh = () => reader.GetDocument(1);
        Action negative = () => reader.GetDocument(-1);

        tooHigh.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_MultiTermCorpus_TermDictionaryIsSortedByHashAscending()
    {
        var docs = new[]
        {
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["alpha", "beta", "gamma", "delta", "epsilon"]),
            new SegmentDocument(1, Guid.NewGuid(), Guid.NewGuid(), ["zeta", "eta", "theta", "iota", "kappa"]),
        };

        byte[] segment = SegmentWriter.Build(docs);
        var reader = new SegmentReader(segment);

        // Reach into the raw bytes via the public SegmentLayout constants -- exactly what a
        // binary-search-based reader implementation (which SegmentReader itself is) depends on.
        int termDictStart = SegmentLayout.HeaderSize + reader.DocumentCount * SegmentLayout.DocRecordSize;
        var hashes = new List<ulong>();
        for (int i = 0; i < reader.TermCount; i++)
        {
            int offset = termDictStart + i * SegmentLayout.TermRecordSize + SegmentLayout.TermRecordHashOffset;
            hashes.Add(BinaryPrimitives.ReadUInt64LittleEndian(segment.AsSpan(offset, 8)));
        }

        reader.TermCount.Should().Be(10);
        hashes.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Build_TermWithRepeatsAcrossManyDocs_PostingsRemainSortedAscendingByDocId()
    {
        // Docs deliberately built out of ascending order in the input list; the writer must sort
        // by DocId before accumulating postings, or the delta encoding (which assumes ascending
        // docId within a term's run) would produce garbage negative-looking deltas.
        var docIds = new[] { 5, 1, 9, 0, 3, 7, 2, 8, 4, 6 };
        var docs = docIds.Select(id => new SegmentDocument(id, Guid.NewGuid(), Guid.NewGuid(), ["shared"])).ToList();

        byte[] segment = SegmentWriter.Build(docs);
        var reader = new SegmentReader(segment);

        var postingDocIds = reader.GetPostings("shared").Select(p => p.DocId).ToList();
        postingDocIds.Should().BeInAscendingOrder();
        postingDocIds.Should().Equal(Enumerable.Range(0, 10));
    }

    [Fact]
    public void Build_UnicodeTerms_RoundtripExactly()
    {
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["кириллица", "café", "日本語"]),
        ]);

        var reader = new SegmentReader(segment);

        reader.GetPostings("кириллица").Should().Equal((0, 1));
        reader.GetPostings("café").Should().Equal((0, 1));
        reader.GetPostings("日本語").Should().Equal((0, 1));
    }
}
