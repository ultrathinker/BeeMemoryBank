using BeeMemoryBank.Search.Indexing;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// Unit-level coverage of <see cref="IndexBuilder"/>'s add/update/remove/lookup contract, the
/// hot-buffer seal trigger, and both merge triggers, independent of the large-scale
/// differential-oracle test in <see cref="IndexBuilderOracleTests"/>.
/// </summary>
public class IndexBuilderTests
{
    // IndexBuilder.Lookup expects an already-tokenized-and-stemmed term, exactly like the terms it
    // stores internally -- never a raw word. These tests use plain English words as document
    // content for readability, but every Lookup() call must go through this helper (the same
    // tokenizer/stemmer pipeline IndexBuilder uses by default) rather than passing a raw word
    // literal, since this library's truncation-only stemmer changes many ordinary words (e.g.
    // "shared" -> "shar", "replacement" -> a shorter prefix).
    private static readonly ITokenizer Tokenizer = new DefaultTokenizer();
    private static readonly IStemmer Stemmer = new DefaultStemmer();

    private static string Stem(string word) => Stemmer.Stem(Tokenizer.Tokenize(word).First());

    [Fact]
    public void AddOrUpdateDocument_NewArticle_IsFindableByItsTerms()
    {
        var builder = new IndexBuilder();
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        builder.AddOrUpdateDocument(articleId, folderId, "the quick brown fox jumps");

        builder.Lookup(Stem("fox")).Should().Contain(articleId);
        builder.Lookup(Stem("nonexistentterm")).Should().BeEmpty();
    }

    [Fact]
    public void RemoveDocument_UnknownArticleId_DoesNotThrow()
    {
        var builder = new IndexBuilder();

        Action act = () => builder.RemoveDocument(Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Fact]
    public void AddOrUpdateDocument_CalledTwiceBeforeAnySeal_OnlyLatestContentIsFindable()
    {
        // Self-check #3: updating an article's content twice in a row (before any seal) must never
        // leave two versions findable -- only the latest.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1000);
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        builder.AddOrUpdateDocument(articleId, folderId, "alpha bravo charlie");
        builder.AddOrUpdateDocument(articleId, folderId, "delta echo foxtrot");

        builder.Lookup(Stem("alpha")).Should().BeEmpty("the first version's terms must no longer match");
        builder.Lookup(Stem("bravo")).Should().BeEmpty();
        builder.Lookup(Stem("charlie")).Should().BeEmpty();
        builder.Lookup(Stem("delta")).Should().Contain(articleId);
        builder.Lookup(Stem("echo")).Should().Contain(articleId);
        builder.Lookup(Stem("foxtrot")).Should().Contain(articleId);
        builder.HotBufferCount.Should().Be(1, "the second update must replace, not add to, the hot buffer entry");
    }

    [Fact]
    public void RemoveDocument_ArticleInSealedSegment_IsUnfindableImmediatelyViaTombstone()
    {
        // Self-check #4: deleting an article whose content lives in an already-sealed segment must
        // make it unfindable immediately (via the tombstone), even before the next merge physically
        // removes it.
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        builder.AddOrUpdateDocument(articleId, folderId, "unique searchable content");

        // hotBufferSealThreshold: 1 means the single AddOrUpdateDocument call above already sealed
        // this document into a segment -- confirm that before proceeding, so the test actually
        // exercises the sealed-segment tombstone path rather than the hot-buffer path.
        builder.SealedSegmentCount.Should().Be(1);
        builder.HotBufferCount.Should().Be(0);
        builder.Lookup(Stem("searchable")).Should().Contain(articleId);

        builder.RemoveDocument(articleId);

        builder.Lookup(Stem("searchable")).Should().BeEmpty();
        builder.Lookup(Stem("unique")).Should().BeEmpty();
        // The tombstone is recorded, but the merge threshold is set unreachably high above, so the
        // segment itself is still physically there -- only its tombstone makes the article unfindable.
        builder.SealedSegmentCount.Should().Be(1, "no merge should have run yet -- the tombstone alone must suffice");
    }

    [Fact]
    public void AddOrUpdateDocument_ArticleAlreadyInSealedSegment_TombstonesOldAndHotBufferHoldsNew()
    {
        // Threshold 2 (not 1): a single AddOrUpdateDocument call must not immediately re-seal the
        // hot buffer, so the test can observe the old-sealed / new-hot coexistence it is checking.
        var builder = new IndexBuilder(hotBufferSealThreshold: 2, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        var articleId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        builder.AddOrUpdateDocument(articleId, folderId, "original content here");
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "filler document to force a seal");
        builder.SealedSegmentCount.Should().Be(1);
        builder.HotBufferCount.Should().Be(0);

        builder.AddOrUpdateDocument(articleId, folderId, "replacement content now");

        builder.Lookup(Stem("original")).Should().BeEmpty();
        builder.Lookup(Stem("replacement")).Should().Contain(articleId);
        builder.HotBufferCount.Should().Be(1);
        builder.SealedSegmentCount.Should().Be(1, "the old sealed segment is tombstoned, not physically rewritten, until a merge runs");
    }

    [Fact]
    public void HotBuffer_ReachesSealThreshold_SealsIntoOneSegmentAndClears()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 10, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);

        for (int i = 0; i < 9; i++)
        {
            builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), $"document number {i}");
        }

        builder.HotBufferCount.Should().Be(9);
        builder.SealedSegmentCount.Should().Be(0);

        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "document number 9");

        builder.HotBufferCount.Should().Be(0, "reaching the threshold must seal and clear the hot buffer");
        builder.SealedSegmentCount.Should().Be(1);
    }

    [Fact]
    public void SealedSegmentCount_ExceedsCountThreshold_TriggersMergeDownToOneSegment()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 5, mergeSegmentCountThreshold: 3, mergeTombstoneFractionThreshold: 1.0);

        // 4 seals' worth of documents: the 4th seal pushes the segment count from 3 to 4, which
        // exceeds the threshold of 3 and must trigger a merge back down to a single segment.
        for (int i = 0; i < 20; i++)
        {
            builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), $"content item {i} alpha bravo");
        }

        builder.SealedSegmentCount.Should().Be(1, "exceeding the segment-count threshold must trigger a merge");
    }

    [Fact]
    public void TombstoneFraction_ExceedsThreshold_TriggersMergeEvenBelowSegmentCountThreshold()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 10, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 0.3);
        var articleIds = new List<Guid>();

        for (int i = 0; i < 10; i++)
        {
            var id = Guid.NewGuid();
            articleIds.Add(id);
            builder.AddOrUpdateDocument(id, Guid.NewGuid(), $"content item {i} alpha bravo charlie");
        }

        builder.SealedSegmentCount.Should().Be(1);

        // Tombstone 4 of the 10 (40%), which exceeds the 30% threshold -- even though there is only
        // ever one sealed segment (well below any segment-count threshold), this alone must trigger
        // a merge (a merge of one segment against itself, reclaiming its tombstoned space).
        for (int i = 0; i < 4; i++)
        {
            builder.RemoveDocument(articleIds[i]);
        }

        builder.SealedSegmentCount.Should().Be(1);
        // After the merge, the removed articles' tombstones are gone because their postings are
        // gone entirely -- confirm the merge actually re-wrote the segment by checking the survivors
        // are still findable and the removed ones are still not.
        string alpha = Stem("alpha");
        for (int i = 0; i < 4; i++)
        {
            builder.Lookup(alpha).Should().NotContain(articleIds[i]);
        }

        for (int i = 4; i < 10; i++)
        {
            builder.Lookup(alpha).Should().Contain(articleIds[i]);
        }
    }

    [Fact]
    public void Merge_PreservesLiveDocumentsAcrossMultipleSegments()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 3, mergeSegmentCountThreshold: 2, mergeTombstoneFractionThreshold: 1.0);
        var survivors = new List<Guid>();

        // Three seals' worth (9 docs, 3 per segment): the 3rd seal pushes segment count to 3, over
        // the threshold of 2, triggering a merge.
        for (int i = 0; i < 9; i++)
        {
            var id = Guid.NewGuid();
            survivors.Add(id);
            builder.AddOrUpdateDocument(id, Guid.NewGuid(), $"survivorterm{i} shared");
        }

        builder.SealedSegmentCount.Should().Be(1);
        string shared = Stem("shared");
        foreach (Guid id in survivors)
        {
            builder.Lookup(shared).Should().Contain(id);
        }

        builder.Lookup(shared).Should().HaveCount(9, "the merge must not lose or duplicate any surviving document");
    }

    [Fact]
    public void Lookup_TermNeverIndexed_ReturnsEmpty()
    {
        var builder = new IndexBuilder();
        builder.AddOrUpdateDocument(Guid.NewGuid(), Guid.NewGuid(), "hello world");

        builder.Lookup(Stem("nope")).Should().BeEmpty();
    }

    [Fact]
    public void GetSealedSegments_ReflectsCurrentTombstonesAndIsLive()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 1, mergeSegmentCountThreshold: 1000, mergeTombstoneFractionThreshold: 1.0);
        var articleId = Guid.NewGuid();
        builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), "content");

        IReadOnlyList<SealedSegmentSnapshot> before = builder.GetSealedSegments();
        before.Should().HaveCount(1);
        before[0].IsLive(articleId).Should().BeTrue();

        builder.RemoveDocument(articleId);

        // The pre-existing snapshot must not change underneath the caller (immutability guarantee).
        before[0].IsLive(articleId).Should().BeTrue("a previously-taken snapshot must never mutate");

        IReadOnlyList<SealedSegmentSnapshot> after = builder.GetSealedSegments();
        after[0].IsLive(articleId).Should().BeFalse("a fresh snapshot must reflect the new tombstone");
    }

    [Fact]
    public void Constructor_InvalidThresholds_Throws()
    {
        FluentActions.Invoking(() => new IndexBuilder(hotBufferSealThreshold: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new IndexBuilder(mergeSegmentCountThreshold: -1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new IndexBuilder(mergeTombstoneFractionThreshold: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new IndexBuilder(mergeTombstoneFractionThreshold: 1.5)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
