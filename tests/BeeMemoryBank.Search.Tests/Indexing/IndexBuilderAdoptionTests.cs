using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// WP-11 Gap 1 coverage: folding a segment reloaded from disk (a fresh <see cref="SegmentReader"/>
/// over raw bytes, with no in-memory Vocabulary/ArticleToDocId of its own) back into a live
/// <see cref="IndexBuilder"/> via <see cref="IndexBuilder.AdoptPersistedSegment"/>, and the
/// tombstone-event plumbing <see cref="IndexBuilder.AddOrUpdateDocument"/>/
/// <see cref="IndexBuilder.RemoveDocument"/> now return (Gap 2's durability hook).
/// </summary>
public class IndexBuilderAdoptionTests
{
    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();

    private static string Stem(string word) => Stemmer.Stem(Tokenizer.Tokenize(word).First());

    [Fact]
    public void AdoptPersistedSegment_NoTombstones_AllDocumentsAreImmediatelyFindable()
    {
        var article0 = Guid.NewGuid();
        var article1 = Guid.NewGuid();
        byte[] bytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, article0, Guid.NewGuid(), ["alpha", "shared"]),
            new SegmentDocument(1, article1, Guid.NewGuid(), ["beta", "shared"]),
        ]);
        var reader = new SegmentReader(bytes);

        var builder = new IndexBuilder();
        builder.AdoptPersistedSegment(reader, new HashSet<Guid>());

        builder.Lookup("alpha").Should().Contain(article0);
        builder.Lookup("beta").Should().Contain(article1);
        builder.Lookup("shared").Should().BeEquivalentTo([article0, article1]);
        builder.SealedSegmentCount.Should().Be(1);
    }

    [Fact]
    public void AdoptPersistedSegment_WithDurableTombstone_TombstonedArticleIsNotFindable()
    {
        // Simulates Gap 2's restart-recovery path: a tombstone that was durably persisted before
        // the restart must be honored the moment the segment is adopted, without ever making the
        // stale content findable even transiently.
        var liveArticle = Guid.NewGuid();
        var tombstonedArticle = Guid.NewGuid();
        byte[] bytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, liveArticle, Guid.NewGuid(), ["survivorterm"]),
            new SegmentDocument(1, tombstonedArticle, Guid.NewGuid(), ["staleterm"]),
        ]);
        var reader = new SegmentReader(bytes);

        var builder = new IndexBuilder();
        builder.AdoptPersistedSegment(reader, new HashSet<Guid> { tombstonedArticle });

        builder.Lookup("survivorterm").Should().Contain(liveArticle);
        builder.Lookup("staleterm").Should().BeEmpty("the article's occurrence was durably tombstoned before adoption");
    }

    [Fact]
    public void AdoptPersistedSegment_ThenTriggeringMergeByAddingDocuments_IncludesAdoptedSegmentsLiveDocuments()
    {
        // Self-check #4/#5 from wp-11.md: adoption must be a first-class citizen of the merge
        // logic, not a special case merge silently ignores. Adopt one segment, then push enough
        // new documents through the normal seal path to cross the merge-segment-count threshold,
        // and confirm the adopted segment's still-live documents survive into the merge output.
        var adoptedArticle = Guid.NewGuid();
        byte[] bytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, adoptedArticle, Guid.NewGuid(), ["adoptedterm"]),
        ]);
        var reader = new SegmentReader(bytes);

        var builder = new IndexBuilder(hotBufferSealThreshold: 2, mergeSegmentCountThreshold: 2, mergeTombstoneFractionThreshold: 1.0);
        builder.AdoptPersistedSegment(reader, new HashSet<Guid>());
        builder.SealedSegmentCount.Should().Be(1);

        // Two seals' worth of fresh documents (threshold 2 each): pushes sealed-segment count from
        // 1 (adopted) to 2 then 3, crossing mergeSegmentCountThreshold=2 and forcing a merge.
        for (int i = 0; i < 4; i++)
        {
            builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), $"fresh document {i} newterm");
        }

        builder.SealedSegmentCount.Should().Be(1, "the merge threshold must have collapsed every sealed segment (adopted + freshly sealed) into one");
        builder.Lookup("adoptedterm").Should().Contain(adoptedArticle, "the adopted segment's live document must survive the merge, not be silently dropped");
    }

    [Fact]
    public void AdoptPersistedSegment_ReturnsInternalIdCorrelatingWithFutureTombstoneEvents()
    {
        var articleId = Guid.NewGuid();
        byte[] bytes = SegmentWriter.Build([new SegmentDocument(0, articleId, Guid.NewGuid(), ["term"])]);
        var reader = new SegmentReader(bytes);

        var builder = new IndexBuilder(hotBufferSealThreshold: 1000, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        int adoptedSegmentId = builder.AdoptPersistedSegment(reader, new HashSet<Guid>());

        IReadOnlyList<SegmentTombstoneEvent> events = builder.RemoveDocument(articleId);

        events.Should().ContainSingle();
        events[0].SegmentId.Should().Be(adoptedSegmentId, "the tombstone event must correlate back to the same internal id the adoption returned");
        events[0].ArticleId.Should().Be(articleId);
        builder.Lookup("term").Should().BeEmpty();
    }

    // ── AddOrUpdateDocument/RemoveDocument's new SegmentTombstoneEvent return value ─────

    [Fact]
    public void AddOrUpdateDocument_NoPriorOccurrence_ReturnsEmptyEventList()
    {
        var builder = new IndexBuilder();
        IReadOnlyList<SegmentTombstoneEvent> events = builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "hello world");

        events.Should().BeEmpty();
    }

    [Fact]
    public void AddOrUpdateDocument_ArticleAlreadyInSealedSegment_ReportsTombstoneEventForThatSegment()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        builder.AddOrUpdateDocument(articleId, folderId, "original content");
        builder.SealedSegmentCount.Should().Be(1, "hotBufferSealThreshold: 1 seals immediately");

        IReadOnlyList<SegmentTombstoneEvent> events = builder.AddOrUpdateDocument(articleId, folderId, "replacement content");

        events.Should().ContainSingle();
        events[0].ArticleId.Should().Be(articleId);
    }

    [Fact]
    public void RemoveDocument_UnknownArticle_ReturnsEmptyEventList()
    {
        var builder = new IndexBuilder();
        IReadOnlyList<SegmentTombstoneEvent> events = builder.RemoveDocument(Guid.NewGuid());

        events.Should().BeEmpty();
    }

    // ── GetMostRecentlySealedSegmentForPersistence ──────────────────────────────────

    [Fact]
    public void GetMostRecentlySealedSegmentForPersistence_BeforeAnySeal_ReturnsNull()
    {
        var builder = new IndexBuilder();
        builder.GetMostRecentlySealedSegmentForPersistence().Should().BeNull();
    }

    [Fact]
    public void GetMostRecentlySealedSegmentForPersistence_AfterASeal_ReturnsBytesThatRoundtripTheSealedContent()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1);
        var articleId = Guid.NewGuid();
        builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), "findable content");

        SealedSegmentPersistenceInfo? info = builder.GetMostRecentlySealedSegmentForPersistence();

        info.Should().NotBeNull();
        info!.Value.DocumentCount.Should().Be(1);
        var reader = new SegmentReader(info.Value.Bytes);
        reader.DocumentCount.Should().Be(1);
        reader.GetDocument(0).ArticleId.Should().Be(articleId);
        reader.GetPostings(Stem("findable")).Should().Equal((0, 1));
    }
}
