using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Indexing;

/// <summary>
/// One durable-persistence-relevant tombstone, reported by <see cref="IndexBuilder.AddOrUpdateDocument"/>/
/// <see cref="IndexBuilder.RemoveDocument"/> for every currently-live sealed segment that just had
/// <paramref name="ArticleId"/>'s occurrence tombstoned. <paramref name="SegmentId"/> is
/// <see cref="SealedSegment"/>'s own internal, process-lifetime-only id -- it means nothing outside
/// this <see cref="IndexBuilder"/> instance. A caller that separately persists sealed segments to
/// disk (WP-11's <c>SearchIndexLifecycleService</c>) is expected to keep its own mapping from this
/// id to whatever external identifier (e.g. a Guid) it used when it persisted that segment, so it
/// can write a durable tombstone row against the right file. <see cref="IndexBuilder"/> itself has
/// no concept of persistence -- this is purely a correlation key, returned synchronously (not
/// raised as a fire-and-forget event) so a caller never has to run its own I/O from inside the
/// writer-lock-protected mutation path.
/// </summary>
public readonly record struct SegmentTombstoneEvent(int SegmentId, Guid ArticleId);

/// <summary>
/// The most recently sealed segment's own internal id, raw bytes, and document count -- see
/// <see cref="IndexBuilder.GetMostRecentlySealedSegmentForPersistence"/>.
/// </summary>
public readonly record struct SealedSegmentPersistenceInfo(int SegmentId, byte[] Bytes, int DocumentCount);

/// <summary>
/// Ties this library's tokenizer/stemmer to the immutable "BMBI" segment format (see
/// <see cref="Segment.SegmentWriter"/>/<see cref="Segment.SegmentReader"/>) with a small LSM-lite
/// lifecycle, so a caller can feed it plaintext article bodies and later ask "which articles
/// currently contain term T" without this component ever re-tokenizing anything after ingestion.
///
/// <para><b>Lifecycle: hot buffer -&gt; seal -&gt; sealed segments -&gt; merge.</b></para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Hot buffer.</b> <see cref="AddOrUpdateDocument"/> tokenizes+stems the plaintext and stores the
/// resulting term list in an in-memory dictionary keyed by articleId -- the cheap, mutable tier.
/// Content here is immediately findable via <see cref="Lookup"/>. Because the dictionary is keyed
/// by articleId, re-adding the same articleId simply overwrites its entry: there is never a window
/// where both an old and a new hot-buffer version of the same article are findable.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Seal.</b> Once the hot buffer reaches <see cref="HotBufferSealThreshold"/> documents, its
/// entire contents are handed to <see cref="Segment.SegmentWriter.Build"/> as one immutable segment,
/// which is appended to the sealed-segment list; the hot buffer is then cleared. Sealing never
/// re-tokenizes -- it reuses the term lists <see cref="AddOrUpdateDocument"/> already computed.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Sealed segments + tombstones.</b> Sealed segments are immutable byte blobs; once built they
/// are never edited in place. When an articleId that lives in a sealed segment is updated or
/// deleted, that segment's copy is marked with a tombstone for that articleId (a per-segment
/// <c>HashSet&lt;Guid&gt;</c>) instead of the segment being rewritten. <see cref="Lookup"/> filters
/// tombstoned articleIds out of its results, so a tombstoned occurrence stops being findable the
/// instant the tombstone is recorded -- well before any merge physically reclaims its space.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Merge.</b> Tombstones are garbage that accumulates in sealed segments over time. When either
/// threshold configured in the constructor trips (segment count, or tombstoned-document fraction),
/// every currently-sealed segment's *live* (non-tombstoned) documents are recombined -- purely from
/// already-computed postings, never by re-tokenizing anything -- into one new sealed segment that
/// replaces all of them, and their tombstones are dropped (a merged article is fresh output now,
/// not stale-in-an-old-segment anymore). See <see cref="MergeLocked"/> for how the merge
/// reconstructs each surviving article's term list from postings alone.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// <b>Concurrency.</b> Every mutation (add/update/remove, and the seal/merge it may trigger) is
/// serialized behind a single writer lock. Reads of the sealed-segment list
/// (<see cref="GetSealedSegments"/>, and internally <see cref="Lookup"/>) never take that lock: they
/// do one volatile read of the current segment-list reference. Because that list, and every
/// <see cref="SealedSegment"/> in it, are immutable once published -- a merge or a new tombstone
/// always builds a brand-new list/segment and swaps the reference atomically, never mutating a
/// previously-published one -- a reader that captured a snapshot reference keeps seeing a fully
/// consistent view of it for as long as it holds that reference, even while a merge is concurrently
/// replacing the field with a different snapshot underneath it.
/// </para>
/// </summary>
public sealed class IndexBuilder
{
    /// <summary>
    /// Default number of documents the hot buffer holds before it is sealed into an immutable
    /// segment. 500 is small enough that a seal's <see cref="Segment.SegmentWriter.Build"/> call (an
    /// O(hot buffer size) pass) stays fast and the hot buffer's plain-dictionary memory footprint
    /// stays modest, but large enough that ingesting a large corpus does not spend most of its time
    /// building tiny segments -- each sealed segment is one more fan-out target every future query
    /// has to check until the next merge, so segments should not be too small or too numerous.
    /// </summary>
    public const int DefaultHotBufferSealThreshold = 500;

    /// <summary>
    /// Default sealed-segment-count merge trigger. Once more than this many sealed segments exist, a
    /// merge runs regardless of tombstone fraction, bounding how many segments a query ever has to
    /// fan out across. 8 is small enough to keep per-query fan-out cheap, large enough that a normal
    /// ingest burst does not force a merge after every single seal.
    /// </summary>
    public const int DefaultMergeSegmentCountThreshold = 8;

    /// <summary>
    /// Default tombstone-fraction merge trigger. Once more than this fraction of all documents
    /// across sealed segments are tombstoned, a merge runs to reclaim that space even if the segment
    /// count alone would not have triggered one yet (e.g. a small, frequently-edited set of
    /// articles). 20% balances reclaiming space promptly against not re-merging on every single edit.
    /// </summary>
    public const double DefaultMergeTombstoneFractionThreshold = 0.2;

    private readonly ITokenizer _tokenizer;
    private readonly IStemmer _stemmer;
    private readonly int _hotBufferSealThreshold;
    private readonly int _mergeSegmentCountThreshold;
    private readonly double _mergeTombstoneFractionThreshold;

    // Single-writer lock: every mutation (add/update/remove, and any seal/merge it triggers) is
    // serialized behind this. The hot buffer is only ever touched while holding it.
    private readonly object _writeLock = new();
    private readonly Dictionary<Guid, HotBufferEntry> _hotBuffer = new();
    private int _nextSegmentId;

    // WP-11: the most recently sealed segment's raw bytes, kept around purely so a caller can
    // persist it right after the AddOrUpdateDocument call that triggered the seal (see
    // GetMostRecentlySealedSegmentForPersistence). Only ever reflects the LAST seal -- callers
    // avoid missing an earlier one by checking SealCount before/after every single
    // AddOrUpdateDocument call, which can trigger at most one seal (adding one document can only
    // ever cross the threshold once).
    private byte[]? _lastSealedSegmentBytes;
    private int _lastSealedSegmentId;
    private int _lastSealedSegmentDocumentCount;

    // The copy-on-write published view of sealed segments. Always replaced wholesale (never
    // mutated in place) under `_writeLock`; read without any lock via a single volatile access, so
    // a reader's snapshot is stable even if a merge replaces this field concurrently.
    private volatile IReadOnlyList<SealedSegment> _sealedSegments = [];

    /// <summary>
    /// Creates an index builder.
    /// </summary>
    /// <param name="tokenizer">Defaults to <see cref="DefaultTokenizer"/> if not supplied.</param>
    /// <param name="stemmer">Defaults to <see cref="DefaultStemmer"/> if not supplied.</param>
    /// <param name="hotBufferSealThreshold">See <see cref="DefaultHotBufferSealThreshold"/>.</param>
    /// <param name="mergeSegmentCountThreshold">See <see cref="DefaultMergeSegmentCountThreshold"/>.</param>
    /// <param name="mergeTombstoneFractionThreshold">See <see cref="DefaultMergeTombstoneFractionThreshold"/>; must be in (0, 1].</param>
    public IndexBuilder(
        ITokenizer? tokenizer = null,
        IStemmer? stemmer = null,
        int hotBufferSealThreshold = DefaultHotBufferSealThreshold,
        int mergeSegmentCountThreshold = DefaultMergeSegmentCountThreshold,
        double mergeTombstoneFractionThreshold = DefaultMergeTombstoneFractionThreshold)
    {
        if (hotBufferSealThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hotBufferSealThreshold), hotBufferSealThreshold, "Must be positive.");
        }

        if (mergeSegmentCountThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeSegmentCountThreshold), mergeSegmentCountThreshold, "Must be positive.");
        }

        if (mergeTombstoneFractionThreshold <= 0 || mergeTombstoneFractionThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeTombstoneFractionThreshold), mergeTombstoneFractionThreshold, "Must be in (0, 1].");
        }

        _tokenizer = tokenizer ?? new DefaultTokenizer();
        _stemmer = stemmer ?? new DefaultStemmer();
        _hotBufferSealThreshold = hotBufferSealThreshold;
        _mergeSegmentCountThreshold = mergeSegmentCountThreshold;
        _mergeTombstoneFractionThreshold = mergeTombstoneFractionThreshold;
    }

    /// <summary>Configured hot-buffer seal threshold (see <see cref="DefaultHotBufferSealThreshold"/>).</summary>
    public int HotBufferSealThreshold => _hotBufferSealThreshold;

    /// <summary>Number of documents currently sitting in the hot buffer.</summary>
    public int HotBufferCount
    {
        get { lock (_writeLock) { return _hotBuffer.Count; } }
    }

    /// <summary>Number of currently-live sealed segments.</summary>
    public int SealedSegmentCount => _sealedSegments.Count;

    /// <summary>
    /// Total number of seals performed over this builder's lifetime (diagnostics/tests -- e.g. to
    /// prove a particular ingestion sequence actually exercised the seal path, independent of the
    /// current <see cref="SealedSegmentCount"/>, which drops back down whenever a merge runs).
    /// </summary>
    public int SealCount { get; private set; }

    /// <summary>
    /// Total number of merges performed over this builder's lifetime (diagnostics/tests -- same
    /// rationale as <see cref="SealCount"/>).
    /// </summary>
    public int MergeCount { get; private set; }

    /// <summary>
    /// Tokenizes+stems <paramref name="plaintext"/> and records it as <paramref name="articleId"/>'s
    /// current content. If this articleId already has content anywhere in this index (the hot
    /// buffer or a sealed segment), that old occurrence is tombstoned/replaced first -- this is
    /// "add or replace", never "add a second copy": a caller re-indexing an edited article never
    /// ends up with both stale and fresh postings matching afterward.
    /// </summary>
    /// <returns>
    /// WP-11: every currently-live sealed segment that had <paramref name="articleId"/>'s prior
    /// occurrence tombstoned as a side effect of this call (empty if the article had no prior
    /// occurrence in any sealed segment). See <see cref="SegmentTombstoneEvent"/> for how a caller
    /// that persists segments to disk is expected to use this.
    /// </returns>
    public IReadOnlyList<SegmentTombstoneEvent> AddOrUpdateDocument(Guid articleId, Guid folderId, string plaintext)
    {
        List<string> terms = TokenizeAndStem(plaintext);
        List<SegmentTombstoneEvent>? events;

        lock (_writeLock)
        {
            events = RetireExistingOccurrenceLocked(articleId);
            _hotBuffer[articleId] = new HotBufferEntry(folderId, terms);

            if (_hotBuffer.Count >= _hotBufferSealThreshold)
            {
                SealLocked();
            }
        }

        return (IReadOnlyList<SegmentTombstoneEvent>?)events ?? [];
    }

    /// <summary>Removes <paramref name="articleId"/> from the index; it is no longer findable afterward.</summary>
    /// <returns>See <see cref="AddOrUpdateDocument"/>'s return value doc.</returns>
    public IReadOnlyList<SegmentTombstoneEvent> RemoveDocument(Guid articleId)
    {
        List<SegmentTombstoneEvent>? events;
        lock (_writeLock)
        {
            events = RetireExistingOccurrenceLocked(articleId);
        }

        return (IReadOnlyList<SegmentTombstoneEvent>?)events ?? [];
    }

    /// <summary>
    /// Returns the distinct articleIds whose current, live content contains <paramref name="term"/>
    /// exactly. <paramref name="term"/> must already be tokenized+stemmed by the caller the same way
    /// <see cref="AddOrUpdateDocument"/> processes plaintext internally -- this is a literal-term
    /// postings lookup, it does not tokenize or stem its input.
    /// </summary>
    public IReadOnlyCollection<Guid> Lookup(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var matches = new HashSet<Guid>();

        lock (_writeLock)
        {
            foreach ((Guid articleId, HotBufferEntry entry) in _hotBuffer)
            {
                if (entry.DistinctTerms.Contains(term))
                {
                    matches.Add(articleId);
                }
            }
        }

        // Single volatile read: a stable snapshot even if a concurrent merge swaps
        // `_sealedSegments` out for a different list right after this line runs.
        IReadOnlyList<SealedSegment> segments = _sealedSegments;
        foreach (SealedSegment segment in segments)
        {
            if (!segment.Vocabulary.Contains(term))
            {
                continue;
            }

            foreach ((int docId, int _) in segment.Reader.GetPostings(term))
            {
                Guid articleId = segment.Reader.GetDocument(docId).ArticleId;
                if (!segment.Tombstones.Contains(articleId))
                {
                    matches.Add(articleId);
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Returns a stable, point-in-time snapshot of the currently-live sealed segments. Safe to call,
    /// and to fully enumerate afterward, concurrently with writes on another thread (including
    /// merges): the returned list and its entries never change underneath the caller, because a
    /// merge always publishes a brand-new list/segments rather than mutating a previously-published
    /// one. This is the primitive a future query engine would fan a multi-term query out across.
    /// </summary>
    public IReadOnlyList<SealedSegmentSnapshot> GetSealedSegments()
    {
        IReadOnlyList<SealedSegment> segments = _sealedSegments;
        var snapshot = new SealedSegmentSnapshot[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            snapshot[i] = new SealedSegmentSnapshot(segments[i].Reader, segments[i].Tombstones);
        }

        return snapshot;
    }

    private List<string> TokenizeAndStem(string? plaintext)
    {
        var terms = new List<string>();
        foreach (string token in _tokenizer.Tokenize(plaintext))
        {
            string stem = _stemmer.Stem(token);
            if (stem.Length > 0)
            {
                terms.Add(stem);
            }
        }

        return terms;
    }

    /// <summary>
    /// Removes <paramref name="articleId"/> from the hot buffer (if present there) and tombstones it
    /// in every sealed segment that still physically holds it live (if any). Must be called with
    /// <see cref="_writeLock"/> held. Triggers a merge-threshold check afterward if any sealed
    /// segment's tombstone set changed as a result.
    /// </summary>
    /// <returns>
    /// WP-11: one <see cref="SegmentTombstoneEvent"/> per segment actually tombstoned by this call,
    /// or null if none were (kept nullable internally to avoid an allocation on the common
    /// no-prior-occurrence path; the public AddOrUpdateDocument/RemoveDocument callers normalize
    /// null to an empty list).
    /// </returns>
    private List<SegmentTombstoneEvent>? RetireExistingOccurrenceLocked(Guid articleId)
    {
        _hotBuffer.Remove(articleId);

        IReadOnlyList<SealedSegment> current = _sealedSegments;
        List<SealedSegment>? updated = null;
        List<SegmentTombstoneEvent>? events = null;
        for (int i = 0; i < current.Count; i++)
        {
            SealedSegment segment = current[i];
            if (segment.IsLive(articleId))
            {
                updated ??= new List<SealedSegment>(current);
                updated[i] = segment.WithTombstone(articleId);
                events ??= new List<SegmentTombstoneEvent>();
                events.Add(new SegmentTombstoneEvent(segment.Id, articleId));
            }
        }

        if (updated is not null)
        {
            _sealedSegments = updated;
            MaybeMergeLocked();
        }

        return events;
    }

    /// <summary>
    /// Builds an immutable segment from the current hot buffer's contents and clears the hot buffer.
    /// Reuses the term lists already computed by <see cref="AddOrUpdateDocument"/> -- no
    /// re-tokenization happens here. Must be called with <see cref="_writeLock"/> held.
    /// </summary>
    private void SealLocked()
    {
        if (_hotBuffer.Count == 0)
        {
            return;
        }

        var docs = new List<SegmentDocument>(_hotBuffer.Count);
        var vocabulary = new HashSet<string>();
        var articleToDocId = new Dictionary<Guid, int>(_hotBuffer.Count);

        int docId = 0;
        foreach ((Guid articleId, HotBufferEntry entry) in _hotBuffer)
        {
            docs.Add(new SegmentDocument(docId, articleId, entry.FolderId, entry.Terms));
            foreach (string term in entry.DistinctTerms)
            {
                vocabulary.Add(term);
            }

            articleToDocId[articleId] = docId;
            docId++;
        }

        byte[] bytes = SegmentWriter.Build(docs);
        var segment = new SealedSegment(_nextSegmentId++, new SegmentReader(bytes), vocabulary, articleToDocId, new HashSet<Guid>());

        var updated = new List<SealedSegment>(_sealedSegments) { segment };
        _sealedSegments = updated;
        _hotBuffer.Clear();
        SealCount++;

        _lastSealedSegmentId = segment.Id;
        _lastSealedSegmentBytes = bytes;
        _lastSealedSegmentDocumentCount = docs.Count;

        MaybeMergeLocked();
    }

    /// <summary>
    /// WP-11: returns the most recently sealed segment's own id, raw bytes, and document count, or
    /// null if no seal has ever happened. A narrow accessor for a caller that wants to persist a
    /// freshly sealed segment to disk (see <c>EncryptedSegmentStore.StoreAsync</c>) right after the
    /// <see cref="AddOrUpdateDocument"/> call that triggered it -- the caller's own before/after
    /// <see cref="SealCount"/> check is the signal that a new seal just happened. Deliberately not
    /// a general "get any segment's bytes" API: this only ever reflects the last seal, and calling
    /// it after a second seal without having consumed the first would silently skip the first
    /// one's persistence -- callers avoid that by checking after every single
    /// <see cref="AddOrUpdateDocument"/> call, which can trigger at most one seal.
    /// </summary>
    public SealedSegmentPersistenceInfo? GetMostRecentlySealedSegmentForPersistence()
    {
        lock (_writeLock)
        {
            return _lastSealedSegmentBytes is null
                ? null
                : new SealedSegmentPersistenceInfo(_lastSealedSegmentId, _lastSealedSegmentBytes, _lastSealedSegmentDocumentCount);
        }
    }

    /// <summary>
    /// WP-11: folds a segment reloaded from disk (via <c>EncryptedSegmentStore.LoadAsync</c> plus a
    /// fresh <see cref="Segment.SegmentReader"/> over its decrypted bytes) back into this
    /// <see cref="IndexBuilder"/>'s live sealed-segment list, so its content is immediately
    /// findable without waiting for a reindex. Reconstructs the <c>Vocabulary</c>/<c>ArticleToDocId</c>
    /// bookkeeping a freshly-sealed segment would already have (see <see cref="SealedSegment"/>'s
    /// own doc comment) purely from <paramref name="reader"/>'s public surface --
    /// <see cref="Segment.SegmentReader.EnumerateTerms"/> for the vocabulary,
    /// <see cref="Segment.SegmentReader.GetDocument"/> for every docId for the reverse lookup --
    /// never by re-tokenizing anything, since the reader has no access to plaintext at all.
    /// <paramref name="tombstones"/> should be whatever was durably persisted for this segment
    /// (see <c>SegmentTombstoneRepository</c>); passing an empty set is only correct for a segment
    /// that genuinely never had any tombstoned occurrence.
    /// </summary>
    /// <returns>
    /// The internal <see cref="SealedSegment.Id"/> assigned to the adopted segment, so a caller
    /// that separately tracks which persisted Guid this segment came from can correlate future
    /// <see cref="SegmentTombstoneEvent"/>s against it.
    /// </returns>
    public int AdoptPersistedSegment(SegmentReader reader, IReadOnlySet<Guid> tombstones)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(tombstones);

        // Computed outside the lock: `reader` is an immutable view over already-decrypted bytes
        // that IndexBuilder does not otherwise touch, so there is nothing here that needs to be
        // serialized against concurrent writers -- only publishing the resulting SealedSegment
        // into `_sealedSegments` does.
        var vocabulary = new HashSet<string>(reader.EnumerateTerms(), StringComparer.Ordinal);
        var articleToDocId = new Dictionary<Guid, int>(reader.DocumentCount);
        for (int docId = 0; docId < reader.DocumentCount; docId++)
        {
            (Guid articleId, Guid _) = reader.GetDocument(docId);
            articleToDocId[articleId] = docId;
        }

        lock (_writeLock)
        {
            var segment = new SealedSegment(_nextSegmentId++, reader, vocabulary, articleToDocId, new HashSet<Guid>(tombstones));

            // Same copy-on-write publish SealLocked/MergeLocked use -- an adopted segment is a
            // first-class citizen of the sealed-segment list from the moment it is published, not
            // a special case bolted on afterward. MaybeMergeLocked runs immediately after for the
            // same reason SealLocked always runs it: if adopting this segment (on top of whatever
            // was already sealed) already crosses a merge threshold, that must be honored right
            // away rather than waiting for the next unrelated mutation to notice.
            var updated = new List<SealedSegment>(_sealedSegments) { segment };
            _sealedSegments = updated;
            MaybeMergeLocked();

            return segment.Id;
        }
    }

    /// <summary>
    /// Runs a merge if either configured threshold (sealed-segment count, or tombstoned-document
    /// fraction) is currently exceeded. Must be called with <see cref="_writeLock"/> held.
    /// </summary>
    private void MaybeMergeLocked()
    {
        IReadOnlyList<SealedSegment> segments = _sealedSegments;
        if (segments.Count <= 1)
        {
            return;
        }

        if (segments.Count > _mergeSegmentCountThreshold)
        {
            MergeLocked(segments);
            return;
        }

        int totalDocs = 0;
        int totalTombstones = 0;
        foreach (SealedSegment segment in segments)
        {
            totalDocs += segment.DocumentCount;
            totalTombstones += segment.TombstoneCount;
        }

        if (totalDocs > 0 && (double)totalTombstones / totalDocs > _mergeTombstoneFractionThreshold)
        {
            MergeLocked(segments);
        }
    }

    /// <summary>
    /// Replaces every currently-sealed segment with one new segment built from their combined live
    /// documents. This is the crux of the "no re-tokenizing" requirement: segments only ever store
    /// terms and postings, never the original plaintext, so a merge cannot re-run the tokenizer even
    /// if it wanted to. Instead, for every segment being merged, it walks that segment's known
    /// <see cref="SealedSegment.Vocabulary"/> (captured at seal time, since <see cref="SegmentReader"/>
    /// itself has no "enumerate every term" method) and for each term reads its postings
    /// (<c>docId</c>, <c>termFrequency</c>) via <see cref="SegmentReader.GetPostings"/>, translates
    /// each posting's local <c>docId</c> back to a real <see cref="SegmentReader.GetDocument"/>
    /// (articleId, folderId), skips it if that articleId is tombstoned in this segment, and
    /// accumulates the surviving (term, frequency) pairs per articleId. Once every segment has been
    /// walked this way, each surviving articleId's accumulated term-frequency map is expanded back
    /// into a duplicates-included term sequence (<see cref="ExpandTermFrequencies"/>) and fed to
    /// <see cref="Segment.SegmentWriter.Build"/> as a fresh <see cref="SegmentDocument"/> -- a
    /// reconstruction that only ever touches already-tokenized/stemmed postings data, never
    /// plaintext. Must be called with <see cref="_writeLock"/> held.
    /// </summary>
    private void MergeLocked(IReadOnlyList<SealedSegment> segments)
    {
        var termFrequenciesByArticle = new Dictionary<Guid, Dictionary<string, int>>();
        var folderByArticle = new Dictionary<Guid, Guid>();
        var sourceSegmentByArticle = new Dictionary<Guid, int>();

        foreach (SealedSegment segment in segments)
        {
            foreach (string term in segment.Vocabulary)
            {
                foreach ((int docId, int termFrequency) in segment.Reader.GetPostings(term))
                {
                    (Guid articleId, Guid folderId) = segment.Reader.GetDocument(docId);
                    if (segment.Tombstones.Contains(articleId))
                    {
                        continue;
                    }

                    if (sourceSegmentByArticle.TryGetValue(articleId, out int sourceSegmentId) && sourceSegmentId != segment.Id)
                    {
                        // An article's live content is expected to exist in exactly one sealed
                        // segment at a time -- AddOrUpdateDocument/RemoveDocument tombstone every
                        // prior occurrence before a new one is published. Seeing it live in two
                        // segments here means that invariant broke somewhere upstream, which would
                        // otherwise corrupt the merge result (duplicated postings) silently, so this
                        // fails loudly instead of guessing which copy is "right".
                        throw new InvalidOperationException(
                            $"Article {articleId} is live in more than one sealed segment during merge; " +
                            "the tombstoning invariant was violated.");
                    }

                    sourceSegmentByArticle[articleId] = segment.Id;

                    if (!termFrequenciesByArticle.TryGetValue(articleId, out Dictionary<string, int>? frequencies))
                    {
                        frequencies = new Dictionary<string, int>();
                        termFrequenciesByArticle[articleId] = frequencies;
                        folderByArticle[articleId] = folderId;
                    }

                    frequencies[term] = termFrequency;
                }
            }
        }

        if (termFrequenciesByArticle.Count == 0)
        {
            _sealedSegments = [];
            MergeCount++;
            return;
        }

        var mergedDocs = new List<SegmentDocument>(termFrequenciesByArticle.Count);
        var mergedVocabulary = new HashSet<string>();
        var mergedArticleToDocId = new Dictionary<Guid, int>(termFrequenciesByArticle.Count);

        int mergedDocId = 0;
        foreach ((Guid articleId, Dictionary<string, int> frequencies) in termFrequenciesByArticle)
        {
            mergedDocs.Add(new SegmentDocument(mergedDocId, articleId, folderByArticle[articleId], ExpandTermFrequencies(frequencies)));
            foreach (string term in frequencies.Keys)
            {
                mergedVocabulary.Add(term);
            }

            mergedArticleToDocId[articleId] = mergedDocId;
            mergedDocId++;
        }

        byte[] mergedBytes = SegmentWriter.Build(mergedDocs);
        var mergedSegment = new SealedSegment(
            _nextSegmentId++,
            new SegmentReader(mergedBytes),
            mergedVocabulary,
            mergedArticleToDocId,
            new HashSet<Guid>());

        // Every prior segment's tombstones are moot now -- their live content either moved into
        // `mergedSegment` (fresh, no tombstone) or was already excluded above (deleted for good).
        _sealedSegments = [mergedSegment];
        MergeCount++;
    }

    /// <summary>
    /// Expands a term -&gt; frequency map back into a flat sequence with duplicates, matching the
    /// duplicates-expected shape <see cref="Segment.SegmentDocument.Terms"/> requires. This is the
    /// "reconstruct a SegmentDocument's Terms purely from postings" step: no plaintext, no
    /// re-tokenization, just replaying each term as many times as its accumulated frequency.
    /// </summary>
    private static IEnumerable<string> ExpandTermFrequencies(Dictionary<string, int> frequencies)
    {
        foreach ((string term, int frequency) in frequencies)
        {
            for (int i = 0; i < frequency; i++)
            {
                yield return term;
            }
        }
    }
}
