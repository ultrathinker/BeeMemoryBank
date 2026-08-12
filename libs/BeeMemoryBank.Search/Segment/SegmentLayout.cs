namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// Byte-layout constants for the "BMBI" inverted-index segment format produced by
/// <see cref="SegmentWriter"/> and consumed by <see cref="SegmentReader"/>. Centralizing every
/// offset and record size here keeps the writer and reader from silently drifting apart if the
/// layout is ever extended, and lets tests assert structural invariants (e.g. "the term
/// dictionary is sorted by hash") directly against the raw bytes without needing reflection.
///
/// All multi-byte integers in the format are little-endian.
/// </summary>
public static class SegmentLayout
{
    /// <summary>The ASCII "BMBI" magic bytes every segment starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "BMBI"u8;

    /// <summary>The only format version this WP produces/understands.</summary>
    public const int FormatVersion = 1;

    // --- Header: magic(4) + formatVersion(4) + docCount(4) + termCount(4) ---
    public const int HeaderMagicOffset = 0;
    public const int HeaderFormatVersionOffset = 4;
    public const int HeaderDocCountOffset = 8;
    public const int HeaderTermCountOffset = 12;
    public const int HeaderSize = 16;

    // --- Doc table record: articleId (16-byte Guid) + folderId (16-byte Guid) ---
    public const int DocRecordArticleIdOffset = 0;
    public const int DocRecordFolderIdOffset = 16;
    public const int DocRecordSize = 32;

    // --- Term dictionary record ---
    // This is wider than the 20-byte record sketched in the original design brief: it adds
    // termTextOffset/termTextLength so a reader can disambiguate a hash collision (two distinct
    // terms sharing a 64-bit hash) by exact byte comparison against the term text block, rather
    // than treating the hash alone as a unique key. postingsOffset and termTextOffset are both
    // absolute byte offsets from the start of the whole segment (not block-relative as the brief
    // sketched), so a reader never has to separately track where each block begins -- every
    // offset is self-sufficient. See SegmentWriter's XML doc for the full rationale.
    public const int TermRecordHashOffset = 0;
    public const int TermRecordTermTextOffsetOffset = 8;
    public const int TermRecordTermTextLengthOffset = 12;
    public const int TermRecordPostingsOffsetOffset = 16;
    public const int TermRecordPostingsLengthOffset = 20;
    public const int TermRecordDocFrequencyOffset = 24;
    public const int TermRecordSize = 28;
}
