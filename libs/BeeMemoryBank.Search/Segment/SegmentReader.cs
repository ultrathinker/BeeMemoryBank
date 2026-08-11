using System.Buffers.Binary;
using System.Text;

namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// Reads a "BMBI" segment produced by <see cref="SegmentWriter"/>. Holds only a reference to the
/// raw segment bytes plus the handful of header fields parsed once in the constructor -- doc
/// lookups, term lookups, and postings decoding all work directly off byte offsets, with no
/// per-document or per-term managed objects allocated until a query actually asks for one.
/// </summary>
public sealed class SegmentReader
{
    private readonly ReadOnlyMemory<byte> _segment;
    private readonly TermHasher _termHasher;
    private readonly int _docTableStart;
    private readonly int _termDictStart;

    /// <summary>Total number of documents in this segment.</summary>
    public int DocumentCount { get; }

    /// <summary>Total number of distinct terms in this segment's dictionary.</summary>
    public int TermCount { get; }

    /// <summary>
    /// Parses a segment's header and validates the magic bytes and format version.
    /// </summary>
    /// <param name="segment">The raw segment bytes, as produced by <see cref="SegmentWriter.Build"/>.</param>
    /// <param name="termHasher">
    /// Must match the hasher the segment was built with (defaults to <see cref="TermHashing.Default"/>
    /// the same way <see cref="SegmentWriter.Build"/> does). A mismatched hasher would compute the
    /// wrong lookup key for every query term.
    /// </param>
    public SegmentReader(ReadOnlyMemory<byte> segment, TermHasher? termHasher = null)
    {
        ReadOnlySpan<byte> span = segment.Span;
        if (span.Length < SegmentLayout.HeaderSize)
        {
            throw new ArgumentException("Segment is too small to contain a valid header.", nameof(segment));
        }

        if (!span.Slice(SegmentLayout.HeaderMagicOffset, 4).SequenceEqual(SegmentLayout.Magic))
        {
            throw new ArgumentException("Segment does not start with the expected \"BMBI\" magic bytes.", nameof(segment));
        }

        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(SegmentLayout.HeaderFormatVersionOffset, 4));
        if (formatVersion != SegmentLayout.FormatVersion)
        {
            throw new NotSupportedException(
                $"Unsupported segment format version {formatVersion} (this reader only understands {SegmentLayout.FormatVersion}).");
        }

        DocumentCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(SegmentLayout.HeaderDocCountOffset, 4));
        TermCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(SegmentLayout.HeaderTermCountOffset, 4));

        _segment = segment;
        _termHasher = termHasher ?? TermHashing.Default;
        _docTableStart = SegmentLayout.HeaderSize;
        _termDictStart = _docTableStart + DocumentCount * SegmentLayout.DocRecordSize;
    }

    /// <summary>Looks up the article/folder identifiers for <paramref name="docId"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="docId"/> is negative or &gt;= <see cref="DocumentCount"/>.
    /// </exception>
    public (Guid ArticleId, Guid FolderId) GetDocument(int docId)
    {
        if ((uint)docId >= (uint)DocumentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), docId, "Doc id is out of range for this segment.");
        }

        ReadOnlySpan<byte> span = _segment.Span;
        int recordOffset = _docTableStart + docId * SegmentLayout.DocRecordSize;
        var articleId = new Guid(span.Slice(recordOffset + SegmentLayout.DocRecordArticleIdOffset, 16));
        var folderId = new Guid(span.Slice(recordOffset + SegmentLayout.DocRecordFolderIdOffset, 16));
        return (articleId, folderId);
    }

    /// <summary>
    /// Number of distinct documents containing <paramref name="term"/>, or 0 if the term is not
    /// present in this segment. Stored in the dictionary for a later work package's BM25 scoring.
    /// </summary>
    public int GetDocumentFrequency(string term)
    {
        int index = FindTermRecordIndex(term);
        return index < 0 ? 0 : ReadRecordInt32(index, SegmentLayout.TermRecordDocFrequencyOffset);
    }

    /// <summary>
    /// Returns the postings for <paramref name="term"/> as <c>(docId, termFrequency)</c> pairs, in
    /// ascending docId order. Returns an empty sequence -- never throws -- if the term was never
    /// indexed. Postings are decoded lazily: this does not materialize the whole postings block,
    /// only the (bounded, query-result-sized) list of matches for this one term.
    /// </summary>
    public IEnumerable<(int DocId, int TermFrequency)> GetPostings(string term)
    {
        int index = FindTermRecordIndex(term);
        if (index < 0)
        {
            yield break;
        }

        int postingsOffset = ReadRecordInt32(index, SegmentLayout.TermRecordPostingsOffsetOffset);
        int postingsLength = ReadRecordInt32(index, SegmentLayout.TermRecordPostingsLengthOffset);

        int offset = postingsOffset;
        int end = postingsOffset + postingsLength;
        int docId = 0;
        while (offset < end)
        {
            ulong delta = VarInt.ReadUInt64(_segment, ref offset);
            ulong freq = VarInt.ReadUInt64(_segment, ref offset);
            docId += (int)delta;
            yield return (docId, (int)freq);
        }
    }

    /// <summary>
    /// Finds the term dictionary index for <paramref name="term"/>, or -1 if it is not present.
    /// Binary-searches for the term's hash (the dictionary is sorted by hash ascending), then
    /// linear-scans the (normally single-entry, occasionally larger on a hash collision) run of
    /// same-hash entries, comparing UTF-8 bytes to find the exact match.
    /// </summary>
    private int FindTermRecordIndex(string term)
    {
        ulong targetHash = _termHasher(term);
        byte[] termBytes = Encoding.UTF8.GetBytes(term);
        ReadOnlySpan<byte> span = _segment.Span;

        int index = LowerBound(targetHash);
        while (index < TermCount && ReadRecordUInt64(index, SegmentLayout.TermRecordHashOffset) == targetHash)
        {
            int textOffset = ReadRecordInt32(index, SegmentLayout.TermRecordTermTextOffsetOffset);
            int textLength = ReadRecordInt32(index, SegmentLayout.TermRecordTermTextLengthOffset);
            if (span.Slice(textOffset, textLength).SequenceEqual(termBytes))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Returns the index of the first term dictionary entry with hash &gt;= <paramref name="targetHash"/>.</summary>
    private int LowerBound(ulong targetHash)
    {
        int lo = 0;
        int hi = TermCount;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            ulong midHash = ReadRecordUInt64(mid, SegmentLayout.TermRecordHashOffset);
            if (midHash < targetHash)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private int ReadRecordInt32(int termIndex, int fieldOffset)
    {
        int offset = _termDictStart + termIndex * SegmentLayout.TermRecordSize + fieldOffset;
        return BinaryPrimitives.ReadInt32LittleEndian(_segment.Span.Slice(offset, 4));
    }

    private ulong ReadRecordUInt64(int termIndex, int fieldOffset)
    {
        int offset = _termDictStart + termIndex * SegmentLayout.TermRecordSize + fieldOffset;
        return BinaryPrimitives.ReadUInt64LittleEndian(_segment.Span.Slice(offset, 8));
    }
}
