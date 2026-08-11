using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Segment;

/// <summary>
/// Verifies collision-handling choice (a) from the design brief: the term dictionary stores each
/// term's actual UTF-8 text (via termTextOffset/termTextLength) so that when two distinct terms
/// hash to the same 64-bit value, the reader disambiguates them by exact byte comparison instead
/// of merging their postings into a single, incorrect result.
///
/// A real XxHash64 collision is not something a unit test can practically produce (finding one
/// needs on the order of 2^32 hashes, per the birthday bound). Instead these tests inject a
/// deliberately broken <see cref="TermHasher"/> -- via the same seam the design brief suggests --
/// that maps several different terms onto identical hash values, and confirm the writer/reader
/// still behave correctly through that forced collision.
/// </summary>
public class SegmentCollisionTests
{
    private static ulong ConstantHasher(string term) => 42UL;

    [Fact]
    public void ForcedCollision_TwoTermsSameHash_BothQueryCorrectlyWithoutMerging()
    {
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["apple"]),
            new SegmentDocument(1, Guid.NewGuid(), Guid.NewGuid(), ["banana", "banana"]),
        ],
        ConstantHasher);

        var reader = new SegmentReader(segment, ConstantHasher);

        reader.TermCount.Should().Be(2, "the two colliding terms must still get separate dictionary entries");
        reader.GetPostings("apple").Should().Equal((0, 1));
        reader.GetPostings("banana").Should().Equal((1, 2));
        reader.GetDocumentFrequency("apple").Should().Be(1);
        reader.GetDocumentFrequency("banana").Should().Be(1);
    }

    [Fact]
    public void ForcedCollision_ManyTermsSameHash_EachIsIndependentlyQueryable()
    {
        string[] terms = ["term-a", "term-b", "term-c", "term-d", "term-e"];
        var docs = terms.Select((t, i) => new SegmentDocument(i, Guid.NewGuid(), Guid.NewGuid(), [t])).ToList();

        byte[] segment = SegmentWriter.Build(docs, ConstantHasher);
        var reader = new SegmentReader(segment, ConstantHasher);

        reader.TermCount.Should().Be(terms.Length);
        for (int i = 0; i < terms.Length; i++)
        {
            reader.GetPostings(terms[i]).Should().Equal(new[] { (i, 1) }, $"term '{terms[i]}' must resolve to its own posting despite the shared hash");
        }
    }

    [Fact]
    public void ForcedCollision_QueryForTermNotInCollidingGroup_ReturnsEmpty()
    {
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["apple", "banana"]),
        ],
        ConstantHasher);

        var reader = new SegmentReader(segment, ConstantHasher);

        // "cherry" hashes to the same bucket (all terms do, under ConstantHasher) but was never
        // indexed -- the byte-comparison disambiguation must still reject it, not return a false
        // positive match against "apple" or "banana" just because the hash lines up.
        reader.GetPostings("cherry").Should().BeEmpty();
        reader.GetDocumentFrequency("cherry").Should().Be(0);
    }

    [Fact]
    public void ForcedCollision_MismatchedHasherBetweenWriterAndReader_FailsToFindTerms()
    {
        // Sanity check on the test seam itself: if the reader does NOT use the same hasher the
        // segment was built with, lookups must not accidentally still work (which would mean the
        // hash isn't actually driving the lookup, defeating the point of this test file).
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["apple"]),
        ],
        ConstantHasher);

        var reader = new SegmentReader(segment); // default XxHash64-based hasher, mismatched on purpose

        reader.GetPostings("apple").Should().BeEmpty();
    }
}
