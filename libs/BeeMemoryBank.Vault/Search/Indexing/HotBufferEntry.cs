namespace BeeMemoryBank.Search.Indexing;

/// <summary>
/// One article's content sitting in <see cref="IndexBuilder"/>'s hot buffer -- the cheap, mutable
/// tier of the index lifecycle. Holds the already-tokenized-and-stemmed term list exactly as
/// <see cref="Segment.SegmentDocument.Terms"/> expects it (duplicates included, one entry per
/// occurrence), plus a distinct-term set computed once so "does this document contain term T"
/// lookups are O(1) instead of a linear scan of the (possibly long) term list.
/// </summary>
internal sealed class HotBufferEntry
{
    public Guid FolderId { get; }

    /// <summary>The document's terms with duplicates, ready to feed straight into <see cref="Segment.SegmentWriter.Build"/>.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>The distinct terms in <see cref="Terms"/>, for O(1) containment checks.</summary>
    public IReadOnlySet<string> DistinctTerms { get; }

    public HotBufferEntry(Guid folderId, IReadOnlyList<string> terms)
    {
        FolderId = folderId;
        Terms = terms;
        DistinctTerms = new HashSet<string>(terms, StringComparer.Ordinal);
    }
}
