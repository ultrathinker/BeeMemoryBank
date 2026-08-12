using BeeMemoryBank.Core.Embeddings;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// WP-15: process-wide in-memory cache of every active article's chunk embeddings, kept genuinely
/// int8-sized (never bulk-dequantized to float32) so the cache stays within the WP-15 RAM budget
/// at ~100k-article scale. Mirrors <see cref="EmbeddingVectorCache"/>'s copy-on-write snapshot
/// design (see that type's doc comment for the concurrency argument, identical here) but scores
/// against quantized bytes via <see cref="Int8Quantizer.Dot"/> instead of
/// <see cref="System.Numerics.Tensors.TensorPrimitives"/> float dot products.
///
/// <para>
/// <b>Per-article max pooling.</b> An article can have several chunk rows; its semantic score for a
/// query is the max cosine score over its own chunks (a "needle" only needs to live in ONE chunk to
/// surface the article), not an average or sum.
/// </para>
/// </summary>
public sealed class ChunkEmbeddingVectorCache
{
    private readonly DbConnectionFactory _factory;

    private volatile Snapshot? _current;
    private long _generation;
    private readonly object _buildLock = new();

    // Deliberately queries the DB directly rather than depending on
    // ArticleChunkEmbeddingRepository (which needs to call Invalidate() on this cache from its own
    // write path, exactly mirroring EmbeddingVectorCache/ArticleRepository) -- a two-way dependency
    // between the repo and its cache is not resolvable by the DI container. See
    // EmbeddingVectorCache's own doc comment for the identical reasoning.
    public ChunkEmbeddingVectorCache(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Signals that tbl_article_chunk_embedding may have changed. See <see cref="EmbeddingVectorCache.Invalidate"/>.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Returns the current snapshot, rebuilding it from a fresh SQL query first if invalidated (or
    /// never built). Never returns null: an empty snapshot is returned when no active article has a
    /// chunk row yet.
    /// </summary>
    public async Task<Snapshot> GetOrRebuildAsync()
    {
        Snapshot? current = _current;
        long gen = Interlocked.Read(ref _generation);
        if (current != null && current.Generation == gen)
        {
            return current;
        }

        return await GetOrRebuildLockedAsync();
    }

    private async Task<Snapshot> GetOrRebuildLockedAsync()
    {
        // A plain `lock` cannot guard an await; this cache rebuilds rarely enough (only on an
        // embedding write, exactly like EmbeddingVectorCache) that allowing two concurrent rebuilds
        // to occasionally both run — the second's publish simply wins — is a fine, deliberately
        // simpler tradeoff than plumbing an async-safe mutex through a cache this cold.
        long genBefore = Interlocked.Read(ref _generation);
        Snapshot snapshot = await RebuildFromDbAsync(genBefore);

        lock (_buildLock)
        {
            Snapshot? current = _current;
            if (current == null || IsNewer(snapshot, current))
            {
                _current = snapshot;
                return snapshot;
            }
            return current;
        }
    }

    // A rebuild started against a later generation should never be superseded by one still in
    // flight for an earlier generation, even if the earlier one's RebuildFromDbAsync call happens
    // to finish second.
    private static bool IsNewer(Snapshot candidate, Snapshot current) => candidate.Generation >= current.Generation;

    private async Task<Snapshot> RebuildFromDbAsync(long generation)
    {
        using var conn = _factory.CreateConnection();

        // Same predicate ArticleChunkEmbeddingRepository.GetAllForActiveArticlesAsync uses --
        // duplicated here (rather than depending on that repository) specifically to avoid the
        // circular dependency the repository's own Invalidate() call on this cache would create.
        var raw = (await conn.QueryAsync<RawRow>(
            @"SELECT c.article_id AS ArticleId, c.chunk_index AS ChunkIndex,
                     c.projection AS Projection, c.scale AS Scale
              FROM tbl_article_chunk_embedding c
              JOIN tbl_article a ON a.id = c.article_id
              WHERE a.status = 'A'
              ORDER BY c.article_id, c.chunk_index")).ToList();

        int dim = raw.Count > 0 ? raw[0].Projection.Length : 0;

        // Rows whose stored dimension doesn't match (e.g. a stale row left behind by a retired
        // model version -- tbl_article_chunk_embedding.model_version exists precisely so this can
        // eventually be detected/cleaned up) are EXCLUDED here entirely, not zero-filled. Unlike
        // EmbeddingVectorCache (which has no fallback concept -- a zero-filled "scores 0" candidate
        // there is harmless), ChunkedArticleIds below drives whether ArticleRepository.
        // SearchByChunkEmbeddingCoreAsync uses this article's chunk score OR falls back to its
        // full-document score. A dimension-mismatched row that instead got zero-filled and counted
        // as "chunked" would score exactly 0 forever and never fall back to its (dimension-correct)
        // full-document embedding -- a real, if currently unreachable, gap since this codebase ships
        // only one model version today: found during an independent adversarial review (2026-08-12).
        var matching = raw.Where(r => dim > 0 && r.Projection.Length == dim).ToList();

        var articleIds = new Guid[matching.Count];
        var vectors = new byte[matching.Count * dim];
        var scales = new float[matching.Count];
        var norms = new float[matching.Count];

        for (int i = 0; i < matching.Count; i++)
        {
            articleIds[i] = matching[i].ArticleId;
            scales[i] = matching[i].Scale;
            matching[i].Projection.CopyTo(vectors.AsSpan(i * dim));
            norms[i] = Int8Quantizer.ComputeNorm(matching[i].Projection, matching[i].Scale);
        }

        return new Snapshot(generation, articleIds, vectors, scales, norms, dim);
    }

    // Mirrors ArticleChunkEmbeddingRepository.RawRow -- kept as a separate private copy rather than
    // shared, since sharing it would require a dependency this cache deliberately avoids (see the
    // constructor's doc comment).
    private sealed class RawRow
    {
        public Guid ArticleId { get; set; }
        public int ChunkIndex { get; set; }
        public byte[] Projection { get; set; } = null!;
        public float Scale { get; set; }
    }

    /// <summary>An immutable, point-in-time view of every active article's chunk embeddings.</summary>
    public sealed class Snapshot
    {
        private readonly Guid[] _articleIds;
        private readonly byte[] _vectors;
        private readonly float[] _scales;
        private readonly float[] _norms;
        private readonly int _dimension;

        internal Snapshot(long generation, Guid[] articleIds, byte[] vectors, float[] scales, float[] norms, int dimension)
        {
            Generation = generation;
            _articleIds = articleIds;
            _vectors = vectors;
            _scales = scales;
            _norms = norms;
            _dimension = dimension;
        }

        public long Generation { get; }

        /// <summary>Total chunk rows in this snapshot (not distinct articles).</summary>
        public int ChunkCount => _articleIds.Length;

        /// <summary>
        /// The single dimension every vector in this snapshot is packed at (0 if the snapshot is
        /// empty). Callers deciding whether <see cref="ChunkedArticleIds"/> is trustworthy for a
        /// particular query MUST first compare it against that query's own projection length -- see
        /// <see cref="ChunkedArticleIds"/>'s own doc comment for why.
        /// </summary>
        public int Dimension => _dimension;

        /// <summary>
        /// Every distinct article id that has at least one chunk row in this snapshot, REGARDLESS of
        /// whether that dimension matches any particular query. A caller using this set to decide
        /// "does this article need the full-document fallback" must first check
        /// <see cref="Dimension"/> against the query projection's own length: if they don't match,
        /// <see cref="ScoreMaxPerArticle"/> would score every one of these ids 0 (the snapshot has
        /// nothing usable for a query of a different dimension), which would incorrectly withhold
        /// the full-document fallback from every chunked article for that query rather than just
        /// the ones that genuinely have no better answer. Found during an independent adversarial
        /// review (2026-08-12): see <c>ArticleRepository.SearchByChunkEmbeddingCoreAsync</c> for the
        /// caller-side fix.
        /// </summary>
        public HashSet<Guid> ChunkedArticleIds => new(_articleIds);

        /// <summary>
        /// Cosine score for every article that has at least one chunk, taking the MAX score across
        /// that article's own chunks. Returns an empty dictionary if this snapshot has no chunks
        /// (e.g. before any article has been (re)chunked since WP-15 shipped).
        /// </summary>
        public Dictionary<Guid, float> ScoreMaxPerArticle(float[] queryProjection)
        {
            ArgumentNullException.ThrowIfNull(queryProjection);
            var result = new Dictionary<Guid, float>();
            if (_articleIds.Length == 0)
            {
                return result;
            }

            bool dimMatches = queryProjection.Length == _dimension;
            float queryNorm = dimMatches ? ComputeQueryNorm(queryProjection) : 0f;

            for (int i = 0; i < _articleIds.Length; i++)
            {
                float score = 0f;
                if (dimMatches && _norms[i] > 0f && queryNorm > 0f)
                {
                    float dot = Int8Quantizer.Dot(queryProjection, _vectors.AsSpan(i * _dimension, _dimension), _scales[i]);
                    score = dot / (_norms[i] * queryNorm);
                }

                Guid id = _articleIds[i];
                if (!result.TryGetValue(id, out float best) || score > best)
                {
                    result[id] = score;
                }
            }

            return result;
        }

        private static float ComputeQueryNorm(float[] query)
        {
            float sumSquares = 0f;
            for (int i = 0; i < query.Length; i++) sumSquares += query[i] * query[i];
            return MathF.Sqrt(sumSquares);
        }
    }
}
