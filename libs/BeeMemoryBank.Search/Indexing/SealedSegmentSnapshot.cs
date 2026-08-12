using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Indexing;

/// <summary>
/// A read-only, point-in-time view of one sealed segment as exposed by
/// <see cref="IndexBuilder.GetSealedSegments"/>: the segment's <see cref="SegmentReader"/> plus
/// enough tombstone information for a caller (e.g. a future query-engine work package) to filter
/// out articleIds whose presence in this segment is stale, without needing to re-derive that from
/// anywhere else. <see cref="IndexBuilder"/> does that filtering itself for <see cref="IndexBuilder.Lookup"/>;
/// this type exists so lower-level callers that need direct postings/doc-table access (e.g. for
/// ranking) can still respect tombstones correctly.
///
/// <para>
/// This snapshot never changes after it is handed out: a later tombstone or merge builds a new
/// <see cref="SealedSegment"/>/snapshot rather than mutating this one, so it is always safe to hold
/// and query even while <see cref="IndexBuilder"/> is concurrently mutated on another thread.
/// </para>
/// </summary>
public sealed class SealedSegmentSnapshot
{
    private readonly IReadOnlySet<Guid> _tombstones;

    /// <summary>The immutable segment's reader. Safe to query concurrently from any thread.</summary>
    public SegmentReader Reader { get; }

    internal SealedSegmentSnapshot(SegmentReader reader, IReadOnlySet<Guid> tombstones)
    {
        Reader = reader;
        _tombstones = tombstones;
    }

    /// <summary>True if <paramref name="articleId"/>'s occurrence in this segment has not been tombstoned (i.e. is still the current content for that article, if it is present here at all).</summary>
    public bool IsLive(Guid articleId) => !_tombstones.Contains(articleId);

    /// <summary>Number of articleIds tombstoned in this segment at the time this snapshot was taken.</summary>
    public int TombstoneCount => _tombstones.Count;
}
