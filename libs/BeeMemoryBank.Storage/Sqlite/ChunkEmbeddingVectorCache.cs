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

        var articleIds = new Guid[raw.Count];
        var vectors = new byte[raw.Count * dim];
        var scales = new float[raw.Count];
        var norms = new float[raw.Count];

        for (int i = 0; i < raw.Count; i++)
        {
            articleIds[i] = raw[i].ArticleId;
            scales[i] = raw[i].Scale;
            if (dim > 0 && raw[i].Projection.Length == dim)
            {
                raw[i].Projection.CopyTo(vectors.AsSpan(i * dim));
                norms[i] = Int8Quantizer.ComputeNorm(raw[i].Projection, raw[i].Scale);
            }
            // else: a row whose stored dimension doesn't match (e.g. a stale row from a retired
            // model version) is zero-filled and scores 0, the same mismatched-dimension handling
            // EmbeddingVectorCache uses.
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

        /// <summary>Every distinct article id that has at least one chunk row in this snapshot.</summary>
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
