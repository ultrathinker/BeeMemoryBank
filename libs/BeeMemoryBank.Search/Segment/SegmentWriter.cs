using System.Buffers.Binary;
using System.Text;

namespace BeeMemoryBank.Search.Segment;

/// <summary>
/// Builds an immutable "BMBI" inverted-index segment from an in-memory set of documents and their
/// already-tokenized terms. Pure in-memory, single allocation of the final result: no file I/O,
/// no encryption, no SQLite -- persistence to disk and encryption of that persisted form are later
/// work packages' job, not this one's.
///
/// <para>
/// Byte layout (all multi-byte integers little-endian; see <see cref="SegmentLayout"/> for the
/// exact offsets/sizes as code):
/// </para>
/// <code>
/// Header (16 bytes):
///   magic          4 bytes   ASCII "BMBI"
///   formatVersion  4 bytes   int32, currently 1
///   docCount       4 bytes   int32
///   termCount      4 bytes   int32
///
/// Doc table (docCount * 32 bytes), indexed by docId 0..docCount-1:
///   articleId      16 bytes  Guid
///   folderId       16 bytes  Guid
///
/// Term dictionary (termCount * 28 bytes), sorted by termHash ascending:
///   termHash         8 bytes  ulong, hash of the term's UTF-8 bytes (see TermHashing.Default)
///   termTextOffset   4 bytes  int32, absolute byte offset (from segment start) of this term's
///                              UTF-8 text in the term text block
///   termTextLength   4 bytes  int32, byte length of that UTF-8 text
///   postingsOffset   4 bytes  int32, absolute byte offset of this term's postings run
///   postingsLength   4 bytes  int32, byte length of that run
///   docFrequency     4 bytes  int32, number of distinct docs containing the term
///
/// Term text block (variable length): the concatenated UTF-8 bytes of every term, addressable
/// only via a term dictionary entry's termTextOffset/termTextLength -- never scanned directly.
///
/// Postings block (variable length): for each term, a run of postings sorted by ascending docId:
///   docIdDelta   varint   gap from the previous posting's docId in this term's run (the first
///                          entry's delta is its docId itself, i.e. an implicit previous of 0)
///   termFreq     varint   occurrence count of the term in that document
/// </code>
///
/// <para>
/// <b>Deviation from the original design sketch:</b> the term dictionary record grew from 20 to
/// 28 bytes (adding termTextOffset/termTextLength), and postingsOffset/termTextOffset are absolute
/// segment offsets rather than block-relative. This implements collision-handling choice (a) from
/// the design brief: since termHash is a lossy 64-bit digest, two distinct terms can in theory
/// collide, and colliding terms sort into adjacent dictionary entries. Storing each term's actual
/// UTF-8 text lets <see cref="SegmentReader"/> disambiguate by exact byte comparison instead of
/// silently treating the hash as a unique key and merging two unrelated terms' postings (choice
/// (b) from the brief) -- correctness over the extra ~8-12 bytes/term this costs. Absolute offsets
/// were chosen over block-relative ones purely to keep the reader simple: every offset is
/// self-sufficient, so there is no separate "where does block X start" bookkeeping to keep in sync
/// with the writer.
/// </para>
///
/// <para>
/// <b>Memory shape:</b> the value this method returns is a single flat <c>byte[]</c> -- no
/// per-term managed objects, no <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c> held long-term.
/// That is the whole point of this format: a query engine holding many of these in memory pays
/// for raw bytes, not for hundreds of small object headers per term. The dictionaries this method
/// builds internally while accumulating postings are transient scratch space, discarded as soon as
/// <see cref="Build"/> returns.
/// </para>
/// </summary>
public static class SegmentWriter
{
    /// <summary>
    /// Builds a segment from <paramref name="documents"/>. Each document's terms are counted
    /// per-document (repeated terms collapse into one posting with a term-frequency count), then
    /// grouped across documents into a term dictionary sorted by hash.
    /// </summary>
    /// <param name="documents">
    /// The documents to index. Doc ids must form a contiguous 0-based range with no gaps or
    /// duplicates (the writer sorts by DocId internally, so input order does not matter).
    /// </param>
    /// <param name="termHasher">
    /// The hash function to use for term dictionary ordering/lookup. Defaults to
    /// <see cref="TermHashing.Default"/>; overridable so tests can force a hash collision.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown if the doc ids are not exactly the contiguous range 0..N-1.
    /// </exception>
    public static byte[] Build(IEnumerable<SegmentDocument> documents, TermHasher? termHasher = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        termHasher ??= TermHashing.Default;

        List<SegmentDocument> docs = documents.ToList();
        docs.Sort((a, b) => a.DocId.CompareTo(b.DocId));
        ValidateDocIds(docs);

        int docCount = docs.Count;

        // Transient build-time accumulation: one entry per distinct term seen anywhere in the
        // corpus, each holding the (docId, termFrequency) pairs for the docs that contain it, in
        // ascending docId order (guaranteed because docs are processed in that order below).
        var postingsByTerm = new Dictionary<string, List<(int DocId, int Freq)>>();

        byte[] docTable = new byte[docCount * SegmentLayout.DocRecordSize];
        for (int i = 0; i < docCount; i++)
        {
            SegmentDocument doc = docs[i];
            int recordOffset = i * SegmentLayout.DocRecordSize;
            doc.ArticleId.TryWriteBytes(docTable.AsSpan(recordOffset + SegmentLayout.DocRecordArticleIdOffset, 16));
            doc.FolderId.TryWriteBytes(docTable.AsSpan(recordOffset + SegmentLayout.DocRecordFolderIdOffset, 16));

            var termFrequencies = new Dictionary<string, int>();
            foreach (string term in doc.Terms)
            {
                termFrequencies[term] = termFrequencies.GetValueOrDefault(term) + 1;
            }

            foreach (KeyValuePair<string, int> entry in termFrequencies)
            {
                if (!postingsByTerm.TryGetValue(entry.Key, out List<(int DocId, int Freq)>? postings))
                {
                    postings = new List<(int DocId, int Freq)>();
                    postingsByTerm[entry.Key] = postings;
                }

                postings.Add((doc.DocId, entry.Value));
            }
        }

        // Order the dictionary by hash ascending; ties (hash collisions) are broken by the term
        // text itself so the ordering is deterministic and colliding entries end up adjacent.
        List<(string Term, ulong Hash, List<(int DocId, int Freq)> Postings)> orderedTerms = postingsByTerm
            .Select(kvp => (Term: kvp.Key, Hash: termHasher(kvp.Key), Postings: kvp.Value))
            .OrderBy(t => t.Hash)
            .ThenBy(t => t.Term, StringComparer.Ordinal)
            .ToList();

        int termCount = orderedTerms.Count;

        int termDictStart = SegmentLayout.HeaderSize + docTable.Length;
        int termTextStart = termDictStart + termCount * SegmentLayout.TermRecordSize;

        // Pass 1: lay out the term text block. Its start is already known (it does not depend on
        // the postings block), so absolute termTextOffset values can be written immediately.
        var termTextBlock = new List<byte>();
        byte[][] termTextBytes = new byte[termCount][];
        int[] termTextOffsets = new int[termCount];
        for (int i = 0; i < termCount; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(orderedTerms[i].Term);
            termTextBytes[i] = bytes;
            termTextOffsets[i] = termTextStart + termTextBlock.Count;
            termTextBlock.AddRange(bytes);
        }

        // Pass 2: now that the term text block's total length is known, the postings block's
        // absolute start is known too, so postings can be encoded with absolute offsets in a
        // single forward pass (no quadratic re-scanning of prior terms).
        int postingsStart = termTextStart + termTextBlock.Count;
        var postingsBlock = new List<byte>();
        byte[] termDict = new byte[termCount * SegmentLayout.TermRecordSize];

        for (int i = 0; i < termCount; i++)
        {
            (string _, ulong hash, List<(int DocId, int Freq)> postings) = orderedTerms[i];

            int postingsOffset = postingsStart + postingsBlock.Count;
            int prevDocId = 0;
            foreach ((int docId, int freq) in postings)
            {
                VarInt.WriteUInt64(postingsBlock, (ulong)(docId - prevDocId));
                VarInt.WriteUInt64(postingsBlock, (ulong)freq);
                prevDocId = docId;
            }

            int postingsLength = postingsStart + postingsBlock.Count - postingsOffset;

            int recordOffset = i * SegmentLayout.TermRecordSize;
            Span<byte> record = termDict.AsSpan(recordOffset, SegmentLayout.TermRecordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(SegmentLayout.TermRecordHashOffset, 8), hash);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(SegmentLayout.TermRecordTermTextOffsetOffset, 4), termTextOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(SegmentLayout.TermRecordTermTextLengthOffset, 4), termTextBytes[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(SegmentLayout.TermRecordPostingsOffsetOffset, 4), postingsOffset);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(SegmentLayout.TermRecordPostingsLengthOffset, 4), postingsLength);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(SegmentLayout.TermRecordDocFrequencyOffset, 4), postings.Count);
        }

        byte[] header = new byte[SegmentLayout.HeaderSize];
        SegmentLayout.Magic.CopyTo(header.AsSpan(SegmentLayout.HeaderMagicOffset, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(SegmentLayout.HeaderFormatVersionOffset, 4), SegmentLayout.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(SegmentLayout.HeaderDocCountOffset, 4), docCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(SegmentLayout.HeaderTermCountOffset, 4), termCount);

        int totalLength = header.Length + docTable.Length + termDict.Length + termTextBlock.Count + postingsBlock.Count;
        byte[] result = new byte[totalLength];
        int pos = 0;
        header.CopyTo(result, pos);
        pos += header.Length;
        docTable.CopyTo(result, pos);
        pos += docTable.Length;
        termDict.CopyTo(result, pos);
        pos += termDict.Length;
        termTextBlock.CopyTo(result, pos);
        pos += termTextBlock.Count;
        postingsBlock.CopyTo(result, pos);

        return result;
    }

    private static void ValidateDocIds(List<SegmentDocument> sortedDocs)
    {
        for (int i = 0; i < sortedDocs.Count; i++)
        {
            if (sortedDocs[i].DocId != i)
            {
                throw new ArgumentException(
                    $"Document ids must be exactly the contiguous range 0..{sortedDocs.Count - 1} " +
                    $"with no gaps or duplicates; found id {sortedDocs[i].DocId} at sorted position {i}.",
                    nameof(sortedDocs));
            }
        }
    }
}
