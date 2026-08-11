using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Indexing;

/// <summary>
/// One immutable sealed segment plus the small amount of bookkeeping <see cref="IndexBuilder"/>
/// needs on top of the raw <see cref="Segment.SegmentReader"/> API:
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Vocabulary</b> -- the distinct terms this segment contains. <see cref="SegmentReader"/>
/// itself only supports point lookups by exact term text (<c>GetPostings</c>/
/// <c>GetDocumentFrequency</c>); it has no "enumerate every term in the dictionary" method. A merge
/// needs to visit every term to recombine postings, so <see cref="IndexBuilder"/> captures this
/// vocabulary at seal time (when it already has every document's term list in hand, before
/// <see cref="SegmentWriter.Build"/> throws that structure away into flat bytes) rather than trying
/// to recover it later from the segment's raw bytes.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>ArticleToDocId</b> -- a reverse index so tombstoning/merge can check "does this segment
/// physically hold articleId X" in O(1) instead of a linear scan of the doc table.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Tombstones</b> -- the set of articleIds whose occurrence in this segment is stale (superseded
/// by a later update, delete, or a merge that has since moved them elsewhere).
/// </description>
/// </item>
/// </list>
///
/// <para>
/// Instances are immutable after construction, including <see cref="Tombstones"/>: adding a
/// tombstone produces a *new* <see cref="SealedSegment"/> via <see cref="WithTombstone"/> rather
/// than mutating this one in place. That is what lets <see cref="IndexBuilder"/> publish the
/// current sealed-segment list as a single copy-on-write snapshot -- a reader holding a reference
/// to an older snapshot's <see cref="SealedSegment"/> instances never sees them change underneath
/// it, even while a concurrent tombstone or merge is building the next snapshot.
/// </para>
/// </summary>
internal sealed class SealedSegment
{
    /// <summary>Monotonically increasing id, unique for the lifetime of the owning <see cref="IndexBuilder"/>. Used only for the merge-invariant check in <see cref="IndexBuilder"/>; never persisted.</summary>
    public int Id { get; }

    public SegmentReader Reader { get; }

    public IReadOnlySet<string> Vocabulary { get; }

    public IReadOnlyDictionary<Guid, int> ArticleToDocId { get; }

    public IReadOnlySet<Guid> Tombstones { get; }

    public int DocumentCount => Reader.DocumentCount;

    public int TombstoneCount => Tombstones.Count;

    public SealedSegment(
        int id,
        SegmentReader reader,
        IReadOnlySet<string> vocabulary,
        IReadOnlyDictionary<Guid, int> articleToDocId,
        IReadOnlySet<Guid> tombstones)
    {
        Id = id;
        Reader = reader;
        Vocabulary = vocabulary;
        ArticleToDocId = articleToDocId;
        Tombstones = tombstones;
    }

    /// <summary>True if this segment holds <paramref name="articleId"/> and it has not been tombstoned.</summary>
    public bool IsLive(Guid articleId) => ArticleToDocId.ContainsKey(articleId) && !Tombstones.Contains(articleId);

    /// <summary>
    /// Returns a new segment identical to this one but with <paramref name="articleId"/> added to
    /// the tombstone set, or this same instance if <paramref name="articleId"/> is not physically
    /// present here or is already tombstoned (so callers can call this unconditionally without
    /// needing to check <see cref="IsLive"/> first).
    /// </summary>
    public SealedSegment WithTombstone(Guid articleId)
    {
        if (!ArticleToDocId.ContainsKey(articleId) || Tombstones.Contains(articleId))
        {
            return this;
        }

        var updatedTombstones = new HashSet<Guid>(Tombstones) { articleId };
        return new SealedSegment(Id, Reader, Vocabulary, ArticleToDocId, updatedTombstones);
    }
}
