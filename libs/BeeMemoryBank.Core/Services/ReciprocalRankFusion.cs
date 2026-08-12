namespace BeeMemoryBank.Core.Services;

/// <summary>
/// WP-16: Reciprocal Rank Fusion — combines several independently-ranked id lists (e.g. a BM25
/// keyword ranking and a cosine-similarity semantic ranking) into one ranking, without needing the
/// two sources' scores to be on comparable scales (BM25 scores and cosine similarities have
/// nothing in common numerically; RRF sidesteps that by fusing on RANK POSITION, not raw score).
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>
    /// The standard RRF constant (60, per the original Cormack/Clarke/Buettcher paper this
    /// technique comes from). Dampens the advantage of rank #1 over #2 relative to using raw
    /// <c>1/rank</c>, so one source's single best hit doesn't dominate every other candidate a
    /// second source ranks consistently well.
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuses <paramref name="rankedLists"/> (each already sorted best-first) into one ranking and
    /// returns the top <paramref name="topK"/> ids. An id absent from a given list contributes 0
    /// from that list — it is not penalized beyond simply not benefiting from that source's signal.
    /// Ties are broken by id ascending, for determinism.
    /// </summary>
    public static List<Guid> Combine(IReadOnlyList<IReadOnlyList<Guid>> rankedLists, int topK, int k = DefaultK)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        if (topK <= 0)
        {
            return [];
        }

        var scores = new Dictionary<Guid, float>();
        foreach (IReadOnlyList<Guid> list in rankedLists)
        {
            for (int i = 0; i < list.Count; i++)
            {
                float contribution = 1f / (k + i + 1); // rank is 1-based
                scores[list[i]] = scores.GetValueOrDefault(list[i]) + contribution;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();
    }
}
