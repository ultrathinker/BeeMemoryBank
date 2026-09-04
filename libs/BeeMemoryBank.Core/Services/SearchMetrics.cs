using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// WP-18: lightweight, in-process latency + result-count metrics for the search subsystems, so an
/// administrator can see p50/p95 latency, request volume, and coarse result-count distribution per
/// search type on the Admin page.
///
/// <para>
/// <b>Privacy is the one hard rule of this component.</b> It deliberately records ONLY timing, a
/// coarse result-count bucket, and a small fixed set of search-type labels. The <see cref="Record"/>
/// signature has no query-string, title, or content parameter on purpose: there is no code path --
/// in this type or any caller -- through which user-supplied search text can reach the in-memory
/// state or anything this component emits. The buckets ("0", "1-10", "11-100", "100+") are coarse
/// enough that they cannot reconstruct a query or reveal anything about a specific article.
/// </para>
///
/// <para>
/// <b>No observability-framework dependency.</b> This codebase did not already use
/// <c>System.Diagnostics.Metrics</c> / <c>Meter</c> / OpenTelemetry anywhere (checked before
/// writing this), so introducing one here would have created a brand-new convention for a single
/// admin page. Instead this is a plain in-memory rolling window, which is all an admin dashboard
/// needs and keeps the dependency surface at zero.
/// </para>
///
/// <para>
/// <b>Lifetime.</b> Registered as a DI singleton: the rolling windows are process-wide so every
/// request -- across all scopes and callers -- contributes to the same admin-visible numbers.
/// Thread-safe; the hot <see cref="Record"/> path takes one short lock and does O(1) work.
/// </para>
/// </summary>
public sealed class SearchMetrics
{
    /// <summary>Search-type label for <see cref="SearchService.SearchAsync"/> (metadata-only search).</summary>
    public const string MetadataSearch = "metadata";

    /// <summary>
    /// Search-type label for body-content search generally: both
    /// <see cref="SearchService.SearchWebContentAsync"/> (the web path's index-first search, with a
    /// pending-only linear fallback) and <see cref="SearchService.SearchWithContentAsync"/> (the
    /// always-complete-but-slower full linear scan it falls back on / that other callers still use
    /// directly) record under this same label — from the admin dashboard's point of view both answer
    /// the same question ("how is content search performing"), just via different internal paths.
    /// </summary>
    public const string ContentSearch = "content";

    /// <summary>Search-type label for <see cref="Storage.Sqlite.ArticleRepository.SearchByEmbeddingAsync"/> (semantic/vector search).</summary>
    public const string SemanticSearch = "semantic";

    /// <summary>
    /// Number of most-recent latency samples retained per search type for percentile computation.
    /// 2048 is large enough that p95 over the window is stable for a dashboard, small enough that
    /// the per-type ring buffer (~16 KB of doubles) and the rare sort in <see cref="GetSnapshot"/>
    /// stay trivial.
    /// </summary>
    public const int SampleWindowSize = 2048;

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, PerType> _byType = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one completed search call. Accepts ONLY timing, a coarse result-count bucket, and the
    /// fixed search-type label -- never the query string, never article titles or content. Safe to
    /// call from the hot search path; the work done here is O(1) under one short lock.
    /// </summary>
    /// <param name="searchType">One of the <c>*Search</c> constants on this type (a fixed label, not user input).</param>
    /// <param name="elapsed">Wall-clock time the search took.</param>
    /// <param name="resultCount">How many results the search returned (folded into a coarse bucket; the raw count is NOT stored).</param>
    public void Record(string searchType, TimeSpan elapsed, int resultCount)
    {
        ArgumentNullException.ThrowIfNull(searchType);

        var ms = elapsed.TotalMilliseconds;
        if (ms < 0)
        {
            // Stopwatch-based timings are non-negative in practice; clamp defensively so a bad clock
            // can never poison the percentile computation with a negative sample.
            ms = 0;
        }

        var bucket = BucketFor(resultCount);
        var per = _byType.GetOrAdd(searchType, _ => new PerType(SampleWindowSize));

        // PerType.Record is itself thread-safe (its own lock around the ring buffer), so we keep the
        // contention surface to that single object rather than a process-wide lock.
        per.Record(ms, bucket);
    }

    /// <summary>
    /// Returns a point-in-time, read-only copy of everything this component currently exposes:
    /// per search type the request count, p50/p95 latency (ms), and the per-bucket request counts.
    /// Safe to call from the (rare) admin endpoint; each type's samples are copied under its own
    /// lock and the percentile sort runs outside any lock.
    /// </summary>
    public SearchMetricsSnapshot GetSnapshot()
    {
        var perType = new Dictionary<string, SearchTypeMetrics>(_byType.Count, StringComparer.Ordinal);
        foreach (var (key, per) in _byType)
        {
            var (total, samples, buckets) = per.Snapshot();
            perType[key] = new SearchTypeMetrics(total, Percentile(samples, 50), Percentile(samples, 95), buckets);
        }
        return new SearchMetricsSnapshot(perType);
    }

    /// <summary>Folds a raw result count into one of the four coarse, privacy-safe buckets.</summary>
    internal static ResultBucket BucketFor(int resultCount)
    {
        if (resultCount <= 0) return ResultBucket.Zero;
        if (resultCount <= 10) return ResultBucket.OneToTen;
        if (resultCount <= 100) return ResultBucket.ElevenToHundred;
        return ResultBucket.OverHundred;
    }

    /// <summary>
    /// Nearest-rank percentile over the (already-copied) sample window. The array is sorted in
    /// place; the caller passes a fresh copy each time so this never mutates shared state.
    /// Returns 0 for an empty window. <paramref name="percentile"/> is a value in [0, 100].
    /// </summary>
    private static double Percentile(double[] samples, double percentile)
    {
        if (samples.Length == 0) return 0.0;
        Array.Sort(samples);
        // Nearest-rank: rank in [1, n] = ceil(p/100 * n), then convert to 0-indexed. Clamped to the
        // valid range so p=100 (and tiny windows) can never read past the end.
        var rank = (int)Math.Ceiling(percentile / 100.0 * samples.Length);
        if (rank < 1) rank = 1;
        if (rank > samples.Length) rank = samples.Length;
        return samples[rank - 1];
    }

    /// <summary>
    /// Mutable, thread-safe per-search-type accumulators: a fixed-capacity ring buffer of latency
    /// samples (for percentiles), a monotonic total request count, and four coarse bucket counts.
    /// </summary>
    private sealed class PerType(int windowSize)
    {
        private readonly double[] _samples = new double[windowSize];
        private int _head;     // next write index into the ring buffer
        private int _count;    // number of valid samples currently held (<= windowSize)
        private long _totalCount;
        private long _bZero, _bOneToTen, _bElevenToHundred, _bOverHundred;
        private readonly object _instanceLock = new();

        public void Record(double ms, ResultBucket bucket)
        {
            lock (_instanceLock)
            {
                _samples[_head] = ms;
                _head = (_head + 1) % _samples.Length;
                if (_count < _samples.Length) _count++;
                _totalCount++;
                switch (bucket)
                {
                    case ResultBucket.Zero: _bZero++; break;
                    case ResultBucket.OneToTen: _bOneToTen++; break;
                    case ResultBucket.ElevenToHundred: _bElevenToHundred++; break;
                    case ResultBucket.OverHundred: _bOverHundred++; break;
                }
            }
        }

        /// <summary>
        /// Returns (total request count, a fresh copy of the valid sample window, bucket counts).
        /// The sample copy is computed under the lock (a short copy of at most <see cref="SampleWindowSize"/>
        /// doubles); the caller does the O(n log n) percentile sort outside the lock.
        /// </summary>
        public (long Total, double[] Samples, ResultBuckets Buckets) Snapshot()
        {
            lock (_instanceLock)
            {
                var copy = new double[_count];
                // For percentile computation we only need the multiset, not insertion order. When the
                // buffer is not yet full the valid region is [0, _count); once full, _count == Length
                // and every slot is valid. In both cases copying the first _count elements is a
                // correct sample of the window.
                Array.Copy(_samples, copy, _count);
                return (_totalCount, copy,
                    new ResultBuckets(_bZero, _bOneToTen, _bElevenToHundred, _bOverHundred));
            }
        }
    }
}

/// <summary>Coarse, privacy-safe result-count bucket. The raw count is never stored.</summary>
internal enum ResultBucket
{
    Zero,
    OneToTen,
    ElevenToHundred,
    OverHundred
}

/// <summary>
/// The four coarse result-count bucket counts exposed to the admin page. Field names match the
/// labels rendered in the UI ("0", "1-10", "11-100", "100+").
/// </summary>
public readonly record struct ResultBuckets(
    [property: JsonPropertyName("0")] long Zero,
    [property: JsonPropertyName("1-10")] long OneToTen,
    [property: JsonPropertyName("11-100")] long ElevenToHundred,
    [property: JsonPropertyName("100+")] long OverHundred);

/// <summary>Per-search-type rollup in a <see cref="SearchMetricsSnapshot"/>.</summary>
public sealed record SearchTypeMetrics(
    long Count,
    double P50Ms,
    double P95Ms,
    ResultBuckets Buckets);

/// <summary>
/// Read-only, point-in-time view of everything <see cref="SearchMetrics"/> exposes: a map from
/// search-type label to its <see cref="SearchTypeMetrics"/>. Contains only counts and timings.
/// </summary>
public sealed record SearchMetricsSnapshot(IReadOnlyDictionary<string, SearchTypeMetrics> BySearchType);
