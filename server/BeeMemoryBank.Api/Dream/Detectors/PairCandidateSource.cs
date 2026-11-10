namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The pairs the model examines, by tier: T1 duplicate candidates, T2 the three nearest with Z at least 3, T3 the
/// five nearest with Z at least 2 (DreamThresholds, plan 3.5). T1 is a threshold query and has no rank cut; T2 and
/// T3 are rank definitions and use <see cref="IDreamCorpus.NeighboursOf"/>. Each unordered pair appears once, under
/// the smaller id first, best first by similarity and then by Z; a pair seen from both sides keeps the larger Z.
/// The tiers may overlap: the examined registry of the run is what stops a pair being paid for twice.
/// </summary>
public sealed class PairCandidateSource
{
    /// <param name="tier">1..DreamThresholds.PairTierCount; anything else gives no pairs.</param>
    public IReadOnlyList<DreamPairCandidate> PairsOf(IDreamCorpus corpus, DreamRunContext ctx, int tier)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(ctx);
        var t = ctx.Thresholds;
        if (tier < 1 || tier > t.PairTierCount) return [];

        var pairs = new Dictionary<(Guid Low, Guid High), (double Similarity, double Z)>();
        if (tier == 1)
        {
            foreach (var article in corpus.Articles)
            {
                foreach (var neighbour in corpus.CandidatesOf(
                             article.Id, t.DuplicateCandidateMinSimilarity, t.DuplicateCandidateMinZ))
                {
                    Add(article.Id, neighbour);
                }
            }
        }
        else
        {
            var topK = tier == 2 ? t.Tier2TopK : t.Tier3TopK;
            if (topK <= 0) return [];

            foreach (var article in corpus.Articles)
            {
                var rank = 0;
                foreach (var neighbour in corpus.NeighboursOf(article.Id, topK))
                {
                    rank++;
                    var qualifies = tier == 2
                        ? rank <= t.Tier2TopK && neighbour.Z >= t.Tier2MinZ
                        : rank <= t.Tier3TopK && neighbour.Z >= t.Tier3MinZ;
                    if (qualifies) Add(article.Id, neighbour);
                }
            }
        }

        return pairs
            .Select(pair => new DreamPairCandidate(
                pair.Key.Low, pair.Key.High, pair.Value.Similarity, pair.Value.Z, tier))
            .OrderByDescending(pair => pair.Similarity)
            .ThenByDescending(pair => pair.Z)
            .ThenBy(pair => pair.AId.ToString("D"), StringComparer.Ordinal)
            .ThenBy(pair => pair.BId.ToString("D"), StringComparer.Ordinal)
            .ToList();

        void Add(Guid articleId, DreamNeighbour neighbour)
        {
            var key = Canonical(articleId, neighbour.Id);
            if (pairs.TryGetValue(key, out var seen))
            {
                pairs[key] = (Math.Max(seen.Similarity, neighbour.Similarity), Math.Max(seen.Z, neighbour.Z));
            }
            else
            {
                pairs[key] = (neighbour.Similarity, neighbour.Z);
            }
        }
    }

    private static (Guid Low, Guid High) Canonical(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("D"), b.ToString("D")) <= 0 ? (a, b) : (b, a);
}
