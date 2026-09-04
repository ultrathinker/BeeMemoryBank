using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// WP-14: process-wide in-memory cache of every active article's <c>(id, embedding_projection)</c>,
/// held as two flat contiguous arrays (one <c>Guid[]</c> of ids, one <c>float[]</c> of all vectors
/// packed at a single dimension) rather than one <c>byte[]</c> per row. The packed layout is what
/// lets <see cref="Snapshot.Score"/> score every candidate with <see cref="TensorPrimitives"/>
/// (SIMD-accelerated) dot products instead of the old scalar per-candidate loop, and lets each
/// candidate's L2 norm be precomputed once at rebuild rather than recomputed on every search.
///
/// <para>
/// <b>Scope.</b> Registered as a singleton and shared by every (scoped) <see cref="ArticleRepository"/>
/// instance, so an embedding write in one request scope invalidates the cache every other scope's
/// next search sees. <see cref="ArticleRepository"/> is itself scoped only because
/// <see cref="CallerScopeHolder"/> is per-request (ACL); the vector cache carries no per-caller
/// state, so singleton is correct for it.
/// </para>
///
/// <para>
/// <b>Invalidation.</b> A generation counter (<see cref="_generation"/>) is bumped by
/// <see cref="Invalidate"/>, and <see cref="GetOrRebuild"/> compares the published snapshot's build
/// generation against the current one, rebuilding the whole cache from a fresh SQL query if they
/// differ. <see cref="ArticleRepository.CreateAsync"/>/<see cref="ArticleRepository.UpdateAsync"/>
/// only call <see cref="Invalidate"/> when given NO transaction (a caller-supplied transaction means
/// the caller is responsible for invalidating itself after commit -- see
/// <see cref="IArticleRepository.InvalidateVectorCache"/>'s doc comment). Neither
/// <c>ArticleService.CreateAsync</c> nor <c>UpdateCoreAsync</c> (both always pass a transaction) call
/// it either, deliberately: neither ever sets <c>Article.EmbeddingProjection</c> to anything other
/// than what was already on the row (null for a brand-new article, unchanged for an edit -- editing
/// an article's TEXT never touches its stored projection bytes), so there is nothing for those two
/// call sites to ever invalidate. The one path that genuinely rewrites projection bytes during
/// normal operation, <see cref="ArticleRepository.UpdateEmbeddingUnscopedAsync"/> (driven by
/// <c>PendingEmbeddingProcessor</c>/<c>EmbeddingProjectionService.ProjectArticleAsync</c>), calls
/// <see cref="UpdateOne"/> instead of a blind <see cref="Invalidate"/>: it already has the new
/// projection bytes in hand, so it patches just that one row into the published snapshot in place of
/// forcing the next <see cref="GetOrRebuild"/> to re-read the entire corpus (~150MB of SQLite reads
/// at 100k articles) for a change that touched exactly one row.
/// <see cref="ArticleRepository.MarkAllEmbeddingsPendingUnscopedAsync"/> (projection-matrix recovery)
/// still calls the blind full <see cref="Invalidate"/>, correctly -- that event means every cached
/// vector is suspect, not just one row's.
/// </para>
///
/// <para>
/// <b>Concurrency.</b> Mirrors the copy-on-write snapshot publish pattern used by
/// <c>IndexBuilder._sealedSegments</c> (<c>libs/BeeMemoryBank.Search/Indexing/IndexBuilder.cs</c>):
/// the cache is published as a single volatile reference (<see cref="_current"/>) to an immutable
/// <see cref="Snapshot"/>. A rebuild always builds a brand-new snapshot and swaps the reference
/// wholesale under <see cref="_buildLock"/>; it never mutates a previously-published snapshot in
/// place. Readers do one volatile read and then operate on whatever snapshot they captured, so a
/// reader is guaranteed a fully consistent view even while a rebuild runs concurrently underneath
/// it. No reader ever sees a torn or half-rebuilt state, and no reader ever takes the build lock.
/// </para>
///
/// <para>
/// <b>Single-dimension assumption.</b> All non-protected articles' projections share one dimension
/// (the projection-matrix dimension, <c>generator.Dimension</c>); protected articles carry an empty
/// projection. The cache therefore packs every vector at one dimension (<see cref="Snapshot.Dimension"/>,
/// taken from the first non-empty projection). A candidate whose stored projection length does not
/// match that dimension is zero-filled into its slot and naturally scores 0 (a zero vector has L2
/// norm 0, so <c>denom = 0</c>), reproducing the pre-WP-14 <c>"proj.Length != dim =&gt; score 0"</c>
/// behavior for a mismatched-dimension candidate without crashing. This is exactly the case the
/// WP-14 brief's dimension-mismatch test exercises. The only state it does NOT model is a corpus
/// containing two different non-empty projection dimensions at once -- which cannot occur here,
/// because every projection is produced through the same single projection matrix.
/// </para>
/// </summary>
public sealed class EmbeddingVectorCache
{
    private readonly DbConnectionFactory _factory;

    // The published, immutable cache snapshot. Replaced wholesale under `_buildLock` on rebuild;
    // read without any lock via a single volatile access (mirrors IndexBuilder._sealedSegments).
    private volatile Snapshot? _current;

    // Bumped by Invalidate() on every embedding-projection write. Read/written via Interlocked so
    // the generation check is safe across the writer (Invalidate) and the many reader/rebuilder
    // threads (GetOrRebuild).
    private long _generation;

    private readonly object _buildLock = new();

    // Number of times RebuildFromDb has actually re-queried SQLite for the whole corpus. Only ever
    // mutated under _buildLock (RebuildFromDb and UpdateOne's dimension-conflict fallback are the
    // only writers), so a plain field is safe -- no Interlocked needed. Exposed for tests/diagnostics
    // to prove a given write path is (or is not) forcing the expensive full rebuild.
    public long RebuildCount { get; private set; }

    public EmbeddingVectorCache(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Signals that the <c>embedding_projection</c> column may have changed in a way this cache
    /// cannot patch incrementally (an unknown number of rows, or a caller that doesn't have the new
    /// bytes in hand). The next <see cref="GetOrRebuild"/> call does a full SQL rebuild. Prefer
    /// <see cref="UpdateOne"/> when the caller already knows exactly which single row changed and to
    /// what -- see that method's doc comment.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Incrementally patches ONE article's row into the currently published snapshot instead of
    /// forcing the next <see cref="GetOrRebuild"/> to re-query all of SQLite. Safe specifically
    /// because the caller (<see cref="ArticleRepository.UpdateEmbeddingUnscopedAsync"/>) already has
    /// the new projection bytes in hand -- there is nothing left to read from the DB, only the
    /// in-memory snapshot arrays need this one row's slot updated or appended. At 100k articles this
    /// is the difference between copying a few KB and re-reading ~150MB for a single article's
    /// re-embed.
    /// </summary>
    /// <remarks>
    /// Falls back to a plain <see cref="Invalidate"/> (the next <see cref="GetOrRebuild"/> does a
    /// full SQL rebuild) whenever an in-place patch cannot be proven safe:
    /// <list type="bullet">
    /// <item>no snapshot has been published yet -- nothing to patch, and the next
    /// <see cref="GetOrRebuild"/> call builds one from scratch anyway, picking this row up for
    /// free;</item>
    /// <item>the new projection's dimension conflicts with the snapshot's already-established
    /// dimension -- only possible transiently, mid-way through a projection-matrix swap (see
    /// <see cref="ArticleRepository.MarkAllEmbeddingsPendingUnscopedAsync"/>'s doc comment).</item>
    /// </list>
    /// Both are rare compared to the steady-state "re-embed one edited article" case this exists
    /// for, and falling back to a full rebuild is always correct, merely slower -- this method never
    /// trades correctness for speed.
    /// </remarks>
    public void UpdateOne(Guid id, byte[] projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_buildLock)
        {
            Snapshot? current = _current;
            if (current == null)
            {
                // Nothing published to patch onto. The next GetOrRebuild call does a full rebuild
                // unconditionally (see its null check), so there's nothing useful to bump either.
                return;
            }

            int floatLen = projection.Length / sizeof(float);
            if (floatLen > 0 && current.Dimension > 0 && floatLen != current.Dimension)
            {
                // Can't fit this row into the snapshot's flat D-wide layout without corrupting every
                // other row's slot. Force a full rebuild, which re-derives the dimension from
                // scratch (see RebuildFromDb) instead of trying to be clever here.
                Interlocked.Increment(ref _generation);
                return;
            }

            long gen = Interlocked.Read(ref _generation);

            // Patch ONLY a snapshot that is still current. If an Invalidate() has already landed —
            // at any point since this snapshot was published, not merely while this method runs —
            // then `current` is stale by definition and a one-row patch cannot make it fresh.
            //
            // Stamping the patch with the newer generation anyway is the trap, and it is not
            // theoretical: the result publishes as "matches _generation", so the next
            // GetOrRebuild() sees a snapshot that looks fresh, skips the rebuild the Invalidate()
            // asked for, and every row that changed independently of this cache (a new article, a
            // regenerated projection matrix) stays invisible to search until some later invalidate
            // happens to race the other way. An earlier version of this comment reasoned only about
            // an Invalidate landing DURING the patch build and concluded the capture order was
            // enough; it is not, because the damaging case is an Invalidate that landed BEFORE this
            // method was ever called.
            //
            // Returning here loses nothing: the caller has already committed its row to the
            // database, so the pending full rebuild reads it from there.
            if (current.Generation != gen)
            {
                return;
            }

            _current = current.WithUpdatedRow(id, projection, floatLen, gen);
        }
    }

    /// <summary>
    /// Returns the current cache snapshot, rebuilding it from a fresh SQL query first if it was
    /// invalidated (or never built). The returned snapshot is immutable and safe to use for as long
    /// as the caller holds the reference, even while a later rebuild swaps the published snapshot
    /// underneath.
    /// </summary>
    /// <remarks>
    /// Never returns null: an empty snapshot is returned when no active article has an embedding.
    /// </remarks>
    public Snapshot GetOrRebuild()
    {
        // Fast path: one volatile read + one generation read; no lock. The snapshot captured here
        // stays consistent for the whole call because snapshots are immutable once published.
        Snapshot? current = _current;
        long gen = Interlocked.Read(ref _generation);
        if (current != null && current.Generation == gen)
        {
            return current;
        }

        return GetOrRebuildLocked();
    }

    private Snapshot GetOrRebuildLocked()
    {
        lock (_buildLock)
        {
            // Double-checked under the lock: another caller may have finished the rebuild this
            // caller is about to start. Re-read both the snapshot and the generation here (a
            // concurrent Invalidate could have bumped the generation since the unlocked check).
            Snapshot? current = _current;
            long gen = Interlocked.Read(ref _generation);
            if (current != null && current.Generation == gen)
            {
                return current;
            }

            Snapshot snapshot = RebuildFromDb(gen);
            // Volatile publish: readers acquiring `snapshot` after this assignment are guaranteed
            // to see its fully-initialized fields (this field is declared volatile, so the write is
            // a release and the read in GetOrRebuild is an acquire).
            _current = snapshot;
            return snapshot;
        }
    }

    private Snapshot RebuildFromDb(long generation)
    {
        RebuildCount++; // always called under _buildLock -- see GetOrRebuildLocked

        using var conn = _factory.CreateConnection();

        // Same predicate the pre-WP-14 first pass used: every active article whose
        // embedding_projection is non-null. We deliberately do NOT add `AND length(...) > 0`, so
        // protected articles (which store an empty -- but non-null -- projection BLOB) remain
        // candidates that score 0, exactly as before.
        var rows = conn.Query<EmbeddingRow>(
            "SELECT a.id AS Id, a.embedding_projection AS EmbeddingProjection " +
            "FROM tbl_article a " +
            "WHERE a.status = 'A' AND a.embedding_projection IS NOT NULL")
            .ToList();

        // The cache dimension is the dimension of the first non-empty projection. All non-empty
        // projections share it (single projection matrix); empty projections (protected articles)
        // are zero-filled and score 0.
        int dim = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            int floatLen = rows[i].EmbeddingProjection.Length / sizeof(float);
            if (floatLen > 0)
            {
                dim = floatLen;
                break;
            }
        }

        var ids = new Guid[rows.Count];
        var vectors = new float[rows.Count * dim]; // zero-initialized: non-conforming slots stay 0.
        var norms = new float[rows.Count];          // 0 for non-conforming => score 0.

        for (int i = 0; i < rows.Count; i++)
        {
            ids[i] = rows[i].Id;

            var floats = MemoryMarshal.Cast<byte, float>(rows[i].EmbeddingProjection.AsSpan());
            if (dim > 0 && floats.Length == dim)
            {
                floats.CopyTo(vectors.AsSpan(i * dim));
                norms[i] = MathF.Sqrt(TensorPrimitives.SumOfSquares(floats));
            }
            // else: leave the vector slot zeroed and norm 0 -- this candidate scores 0 at query
            // time, reproducing the old `proj.Length != dim => 0` behavior for mismatched/empty
            // projections without ever indexing wrong-dimension data into a flat D-wide slot.
        }

        return new Snapshot(generation, ids, vectors, norms, dim);
    }

    // Narrow DTO for the rebuild query. Dapper binds public properties by alias; using a ValueTuple
    // here would bind by ordinal (Item1/Item2) and silently defeat the aliasing, the same trap
    // ArticleRepository.ArticleDeleteMeta exists to avoid.
    private sealed class EmbeddingRow
    {
        public Guid Id { get; set; }
        public byte[] EmbeddingProjection { get; set; } = null!;
    }

    /// <summary>
    /// An immutable, point-in-time view of every active article's embedding projection, packed into
    /// flat arrays for SIMD scoring. Published by <see cref="EmbeddingVectorCache"/> via a volatile
    /// swap; never mutated after publication, so any snapshot a reader captured stays fully
    /// consistent for as long as the reader holds it.
    /// </summary>
    public sealed class Snapshot
    {
        private readonly Guid[] _ids;
        private readonly float[] _vectors;
        private readonly float[] _norms;
        private readonly int _dimension;

        internal Snapshot(long generation, Guid[] ids, float[] vectors, float[] norms, int dimension)
        {
            Generation = generation;
            _ids = ids;
            _vectors = vectors;
            _norms = norms;
            _dimension = dimension;
        }

        /// <summary>
        /// Builds a new immutable snapshot identical to this one except for <paramref name="id"/>'s
        /// row, which is patched in place (or appended, if this id has no existing slot). Called only
        /// by <see cref="EmbeddingVectorCache.UpdateOne"/>, which has already checked
        /// <paramref name="floatLen"/> is compatible with <see cref="Dimension"/> (equal, or this
        /// snapshot's dimension is still 0 -- meaning no row has ever carried a real vector, so
        /// establishing it fresh from this row is safe). Never mutates this instance's own arrays --
        /// copies them, exactly like <see cref="EmbeddingVectorCache.RebuildFromDb"/> never mutates a
        /// previously-published snapshot.
        /// </summary>
        internal Snapshot WithUpdatedRow(Guid id, byte[] projection, int floatLen, long generation)
        {
            // Establish the dimension the first time a non-empty vector appears in an until-now
            // all-zero (dimension 0) snapshot; otherwise keep the snapshot's existing dimension.
            int newDim = _dimension > 0 ? _dimension : floatLen;
            int existingIndex = Array.IndexOf(_ids, id);

            Guid[] newIds;
            float[] newVectors;
            float[] newNorms;

            if (existingIndex >= 0)
            {
                // Existing candidate -- the id set itself doesn't change, so it's safe to share the
                // old array (it's never mutated, only ever replaced wholesale on a future rebuild).
                newIds = _ids;
                newNorms = (float[])_norms.Clone();
                newVectors = newDim == _dimension
                    ? (float[])_vectors.Clone()
                    // Dimension just established from 0 -- every existing row was necessarily a
                    // zero-filled placeholder (a dimension of 0 means no row has ever carried a real
                    // vector), so re-packing at the new width with every old slot left zero is
                    // exactly what those rows already were.
                    : new float[_ids.Length * newDim];

                PatchRow(newVectors, newNorms, existingIndex, newDim, projection, floatLen);
            }
            else
            {
                // Brand-new candidate (first embedding this article has ever had, or the article
                // itself was created after this snapshot was published). Append one slot.
                int oldCount = _ids.Length;
                newIds = new Guid[oldCount + 1];
                _ids.CopyTo(newIds, 0);
                newIds[oldCount] = id;

                newNorms = new float[oldCount + 1];
                _norms.CopyTo(newNorms, 0);

                newVectors = new float[newIds.Length * newDim];
                if (newDim == _dimension)
                {
                    _vectors.CopyTo(newVectors, 0);
                }
                // else: dimension just established from 0 -- every prior row was already a zero
                // placeholder (see above), so leaving their slots zero-initialized in the larger
                // array is correct; there's nothing real to copy.

                PatchRow(newVectors, newNorms, oldCount, newDim, projection, floatLen);
            }

            return new Snapshot(generation, newIds, newVectors, newNorms, newDim);
        }

        // Writes one row's slot in `vectors`/`norms` at `index`, reproducing RebuildFromDb's own
        // "zero-fill a non-conforming/empty projection, score 0" behavior for floatLen == 0.
        private static void PatchRow(float[] vectors, float[] norms, int index, int dim, byte[] projection, int floatLen)
        {
            if (floatLen == dim && dim > 0)
            {
                var floats = MemoryMarshal.Cast<byte, float>(projection.AsSpan());
                floats.CopyTo(vectors.AsSpan(index * dim));
                norms[index] = MathF.Sqrt(TensorPrimitives.SumOfSquares(floats));
            }
            else
            {
                // Empty (protected-article) projection, or dim == 0 (no real vector has ever
                // existed in this snapshot) -- leave the slot zeroed and norm 0, same as
                // RebuildFromDb's handling of a non-conforming/empty candidate.
                if (dim > 0)
                {
                    vectors.AsSpan(index * dim, dim).Clear();
                }
                norms[index] = 0f;
            }
        }

        /// <summary>
        /// The invalidation generation this snapshot was built from. Compared against the cache's
        /// current generation to decide whether the snapshot is stale and needs rebuilding.
        /// </summary>
        public long Generation { get; }

        /// <summary>Number of candidate vectors in this snapshot (active articles with a projection).</summary>
        public int Count => _ids.Length;

        /// <summary>
        /// The single dimension every non-empty vector in this snapshot is packed at. A query whose
        /// <see cref="Score"/> projection length differs from this scores every candidate 0, because
        /// no packed vector shares the query's dimension -- the same outcome the pre-WP-14 scalar
        /// code produced (<c>proj.Length != queryDim =&gt; 0</c>).
        /// </summary>
        public int Dimension => _dimension;

        /// <summary>
        /// Ranks every candidate against <paramref name="queryProjection"/> by cosine similarity
        /// and returns the ids of the top <paramref name="topK"/>, best-first. Pure function over
        /// this immutable snapshot; safe to call concurrently from any number of threads, including
        /// while a rebuild publishes a different snapshot.
        ///
        /// <para>
        /// Scoring uses <see cref="TensorPrimitives"/> (SIMD-accelerated) for the dot product, and
        /// reuses each candidate's precomputed L2 norm (<see cref="_norms"/>) plus the query norm
        /// (computed once, outside the per-candidate loop) for the denominator. The pre-WP-14 loop
        /// recomputed the query norm on every candidate iteration -- a wasted cost independent of
        /// SIMD, fixed here.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Tie-breaking is stable by candidate order (the order rows came back from the rebuild
        /// SQL), matching <see cref="System.Linq.Enumerable.OrderByDescending{TSource,TKey}"/>'s
        /// stable sort that the pre-WP-14 code relied on. Implemented by sorting an index array
        /// keyed on (score descending, original index ascending); because the index is a unique
        /// tiebreaker, the result is total-stable regardless of
        /// <see cref="Array.Sort{T}(T[], Comparison{T})"/>'s own (non-stable) ordering.
        /// </remarks>
        public List<Guid> Score(float[] queryProjection, int topK)
        {
            ArgumentNullException.ThrowIfNull(queryProjection);
            if (topK <= 0 || _ids.Length == 0)
            {
                return new List<Guid>();
            }

            int count = _ids.Length;

            // Query norm computed once (the pre-WP-14 loop recomputed it on every candidate -- a
            // redundant cost independent of SIMD). When the query dimension does not match the
            // snapshot's packed dimension, no candidate can share the query's dimension, so every
            // candidate must score 0 (same as the old proj.Length != dim => 0). Setting queryNorm
            // to 0 in that case makes every denom below 0 and thus every score 0, so a single loop
            // handles both cases without indexing wrong-dimension data into a flat D-wide slot.
            float queryNorm = queryProjection.Length == _dimension
                ? MathF.Sqrt(TensorPrimitives.SumOfSquares(queryProjection))
                : 0f;

            bool dimMatches = queryProjection.Length == _dimension;

            var scores = new float[count];
            for (int i = 0; i < count; i++)
            {
                float dot = dimMatches
                    ? TensorPrimitives.Dot(queryProjection, _vectors.AsSpan(i * _dimension, _dimension))
                    : 0f;
                float denom = _norms[i] * queryNorm; // _norms[i] == 0 for mismatched/empty candidates
                scores[i] = denom > 0f ? dot / denom : 0f;
            }

            // Stable sort: higher score first; ties broken by original candidate index ascending
            // (i.e. the rebuild SQL's row order, which is what the old OrderByDescending used).
            // The unique-index tiebreaker makes the ordering total-stable regardless of Array.Sort
            // not being a stable sort itself.
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = i;
            }

            Array.Sort(indices, (a, b) =>
            {
                int c = scores[b].CompareTo(scores[a]); // descending by score
                return c != 0 ? c : a.CompareTo(b);     // ascending by index (stable tie-break)
            });

            int k = Math.Min(topK, count);
            var result = new List<Guid>(k);
            for (int i = 0; i < k; i++)
            {
                result.Add(_ids[indices[i]]);
            }

            return result;
        }

        /// <summary>
        /// WP-15: cosine score for every candidate in this snapshot, not just the top-K —
        /// <see cref="Storage.Sqlite.ArticleChunkEmbeddingRepository"/>-backed chunk scoring needs
        /// the full-document score for every article that has no chunk rows yet (the "old vectors
        /// remain a fallback until backfill" case), which a top-K cut would silently drop candidates
        /// from before the fallback merge ever sees them.
        /// </summary>
        public Dictionary<Guid, float> ScoreAll(float[] queryProjection)
        {
            ArgumentNullException.ThrowIfNull(queryProjection);
            var result = new Dictionary<Guid, float>(_ids.Length);
            if (_ids.Length == 0)
            {
                return result;
            }

            float queryNorm = queryProjection.Length == _dimension
                ? MathF.Sqrt(TensorPrimitives.SumOfSquares(queryProjection))
                : 0f;
            bool dimMatches = queryProjection.Length == _dimension;

            for (int i = 0; i < _ids.Length; i++)
            {
                float dot = dimMatches
                    ? TensorPrimitives.Dot(queryProjection, _vectors.AsSpan(i * _dimension, _dimension))
                    : 0f;
                float denom = _norms[i] * queryNorm;
                result[_ids[i]] = denom > 0f ? dot / denom : 0f;
            }

            return result;
        }
    }
}
