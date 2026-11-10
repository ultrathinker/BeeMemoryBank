namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// Cosine similarity and Z between the articles of a run (plan 3.5): for an article A, the mean and the standard
/// deviation of its similarity to every other article that has a vector, Z = (sim - mean) / std.
/// DreamCorpusBuilder makes one per run from the article vectors and answers IDreamCorpus.NeighboursOf with it.
/// <para>
/// The index keeps the vectors by reference (the run's snapshot owns them and does not change them) plus each
/// vector's norm. A row is scanned once and its mean and standard deviation are taken over the whole row, as the
/// plan defines Z; a request for the nearest <c>topK</c> then uses a bounded worst-first heap, so a small top-K
/// never sorts the whole row. <see cref="CandidatesOf"/> answers the threshold query the duplicate detectors need
/// and never sorts the row at all. A row's best <see cref="CachedPrefix"/> entries are kept for the next caller; a
/// request for a longer row computes it and returns it without keeping it whole, so one run over a large vault does
/// not hold an n-by-n matrix.
/// </para>
/// </summary>
public sealed class DreamNeighbourIndex
{
    /// <summary>How many of a row's first neighbours are kept for the next caller.</summary>
    private const int CachedPrefix = 32;

    /// <summary>The range a cosine similarity is kept in (a rounding error may push the raw quotient a hair outside it);
    /// the rule book says it from here.</summary>
    internal const double MinSimilarity = -1d;
    internal const double MaxSimilarity = 1d;

    private readonly Dictionary<Guid, Vector> _vectors = [];
    private readonly Dictionary<Guid, CacheEntry> _cache = [];
    private readonly object _gate = new();

    /// <param name="vectors">The vector of every in-scope article that has one (protected articles have none).</param>
    public DreamNeighbourIndex(IReadOnlyDictionary<Guid, float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        foreach (var (id, values) in vectors)
        {
            if (values is null || values.Length == 0) continue;
            double sumOfSquares = 0;
            foreach (var value in values) sumOfSquares += (double)value * value;
            var norm = Math.Sqrt(sumOfSquares);
            if (!double.IsFinite(norm) || norm <= 0) continue;
            _vectors[id] = new Vector(values, norm);
        }
    }

    /// <summary>The nearest articles, best first, at most <paramref name="topK"/>; empty for an article without a vector.</summary>
    public IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK)
    {
        if (topK <= 0 || !_vectors.TryGetValue(articleId, out var self)) return [];

        lock (_gate)
        {
            if (_cache.TryGetValue(articleId, out var cached) && (cached.Complete || cached.TopK >= topK))
                return Slice(cached.Neighbours, topK);
        }

        var computed = NearestOf(articleId, self, topK);

        lock (_gate)
        {
            var keep = Math.Min(Math.Min(topK, CachedPrefix), computed.Count);
            // The cached list is the whole row only when the request covered every vector and nothing was cut
            // from the row by the prefix limit. Otherwise a longer request must compute again.
            var complete = topK >= _vectors.Count - 1 && keep == computed.Count;
            if (!_cache.TryGetValue(articleId, out var existing) || existing.TopK < keep)
                _cache[articleId] = new CacheEntry(keep, Slice(computed, keep), complete);
            else if (complete && !existing.Complete)
                _cache[articleId] = existing with { Complete = true };
        }

        return computed;
    }

    /// <summary>
    /// Every neighbour that meets either threshold: similarity at least <paramref name="minSimilarity"/> or Z at
    /// least <paramref name="minZ"/> (plan 3.6). No top-K cut is applied, so a candidate is never hidden by its
    /// rank; the qualifying rows are returned best first. The whole row is still scanned once to take its mean and
    /// standard deviation, but it is not sorted.
    /// </summary>
    public IReadOnlyList<DreamNeighbour> CandidatesOf(Guid articleId, double minSimilarity, double minZ)
    {
        if (!_vectors.TryGetValue(articleId, out var self)) return [];

        var row = ScanRow(articleId, self, out var mean, out var std);
        if (row.Count == 0) return [];

        var matches = new List<DreamNeighbour>();
        foreach (var item in row)
        {
            var z = ZScore(item.Similarity, mean, std);
            if (item.Similarity >= minSimilarity || z >= minZ)
                matches.Add(new DreamNeighbour(item.Id, item.Similarity, z));
        }
        matches.Sort(BestFirst);
        return matches;
    }

    /// <summary>The best <paramref name="topK"/> of a row, taken with a bounded heap unless the caller asked for
    /// every neighbour; only that last case orders the whole row.</summary>
    private List<DreamNeighbour> NearestOf(Guid articleId, Vector self, int topK)
    {
        var row = ScanRow(articleId, self, out var mean, out var std);
        if (row.Count == 0) return [];
        var take = Math.Min(topK, row.Count);

        if (take == row.Count)
        {
            row.Sort(BestFirstRow);
            var all = new List<DreamNeighbour>(row.Count);
            foreach (var item in row) all.Add(ToNeighbour(item, mean, std));
            return all;
        }

        // The heap holds the best `take`; its root is the worst of them, so the next worse item is evicted first.
        var heap = new PriorityQueue<(Guid Id, double Similarity), RowPriority>(WorstFirst.Instance);
        foreach (var item in row)
        {
            heap.Enqueue(item, new RowPriority(item.Similarity, item.Id.ToString("D")));
            if (heap.Count > take) heap.Dequeue();
        }

        var best = new List<DreamNeighbour>(take);
        while (heap.TryDequeue(out var item, out _)) best.Add(ToNeighbour(item, mean, std));
        best.Sort(BestFirst);
        return best;
    }

    /// <summary>Every similarity of one row and the mean and standard deviation over all of them.</summary>
    private List<(Guid Id, double Similarity)> ScanRow(Guid articleId, Vector self, out double mean, out double std)
    {
        var row = new List<(Guid Id, double Similarity)>(_vectors.Count - 1);
        foreach (var (id, other) in _vectors)
        {
            if (id == articleId) continue;
            var similarity = Similarity(self, other);
            if (double.IsNaN(similarity)) continue;
            row.Add((id, similarity));
        }

        if (row.Count == 0)
        {
            mean = 0;
            std = 0;
            return row;
        }

        double sum = 0;
        foreach (var item in row) sum += item.Similarity;
        mean = sum / row.Count;

        double variance = 0;
        foreach (var item in row) variance += (item.Similarity - mean) * (item.Similarity - mean);
        variance /= row.Count;
        std = Math.Sqrt(variance);
        return row;
    }

    private static double ZScore(double similarity, double mean, double std) =>
        std > 0 ? (similarity - mean) / std : 0;

    private static DreamNeighbour ToNeighbour((Guid Id, double Similarity) item, double mean, double std) =>
        new(item.Id, item.Similarity, ZScore(item.Similarity, mean, std));

    private static int BestFirst(DreamNeighbour x, DreamNeighbour y)
    {
        var bySimilarity = y.Similarity.CompareTo(x.Similarity);
        return bySimilarity != 0 ? bySimilarity : string.CompareOrdinal(x.Id.ToString("D"), y.Id.ToString("D"));
    }

    private static int BestFirstRow((Guid Id, double Similarity) x, (Guid Id, double Similarity) y)
    {
        var bySimilarity = y.Similarity.CompareTo(x.Similarity);
        return bySimilarity != 0 ? bySimilarity : string.CompareOrdinal(x.Id.ToString("D"), y.Id.ToString("D"));
    }

    private static double Similarity(Vector a, Vector b)
    {
        if (a.Values.Length != b.Values.Length) return double.NaN;
        double dot = 0;
        for (var i = 0; i < a.Values.Length; i++) dot += (double)a.Values[i] * b.Values[i];
        var similarity = dot / (a.Norm * b.Norm);
        return double.IsFinite(similarity) ? Math.Clamp(similarity, MinSimilarity, MaxSimilarity) : double.NaN;
    }

    private static IReadOnlyList<DreamNeighbour> Slice(IReadOnlyList<DreamNeighbour> neighbours, int topK) =>
        topK >= neighbours.Count ? neighbours : neighbours.Take(topK).ToList();

    private readonly record struct Vector(float[] Values, double Norm);

    private readonly record struct RowPriority(double Similarity, string Id);

    /// <summary>Orders a row's worst entry first: lower similarity is worse; for equal similarity the larger id is
    /// worse, because the order everyone else sees is similarity descending then id ascending.</summary>
    private sealed class WorstFirst : IComparer<RowPriority>
    {
        public static readonly WorstFirst Instance = new();

        public int Compare(RowPriority x, RowPriority y)
        {
            var bySimilarity = x.Similarity.CompareTo(y.Similarity);
            return bySimilarity != 0 ? bySimilarity : string.CompareOrdinal(y.Id, x.Id);
        }
    }

    private sealed record CacheEntry(int TopK, IReadOnlyList<DreamNeighbour> Neighbours, bool Complete);
}
