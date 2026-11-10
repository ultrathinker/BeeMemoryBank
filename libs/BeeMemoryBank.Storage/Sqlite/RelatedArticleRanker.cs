using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// One article that might belong in another's related list, with everything the ranking needs: the rare
/// tags it shares (already weighted) and, when it is also close by meaning, how close.
/// </summary>
internal sealed record RelatedCandidate(
    Guid Id,
    string Title,
    string TreePath,
    IReadOnlyList<(string Name, double Weight)> Tags,
    float? Similarity,
    float Z)
{
    public double TagScore => Tags.Sum(t => t.Weight);
}

/// <summary>
/// The scoring rules of the related list, as pure functions so they can be tested without a database.
/// <para>
/// A shared tag counts by how rare it is, <c>w = ln((N + 1) / (df + 1))</c>, so "rust" shared by four notes
/// says far more than "notes" shared by half the vault; a tag carried by more than
/// <c>max(20, 5% of N)</c> articles is a commonplace and weighs nothing. Nearness by meaning comes from the
/// article vectors (see <see cref="EmbeddingVectorCache.Snapshot.GetNeighbours"/>). Both parts are scaled to
/// 0..1 by the best row of the list being ranked and combined as <c>0.6 * tags + 0.4 * meaning</c>.
/// </para>
/// </summary>
internal static class RelatedArticleRanker
{
    public const double TagShare = 0.6;
    public const double SemanticShare = 0.4;

    /// <summary>A tag on more articles than max(this, <see cref="CommonTagFraction"/> of the vault) is a commonplace.</summary>
    public const int CommonTagFloor = 20;
    public const double CommonTagFraction = 0.05;

    /// <summary>Nearest articles read from the vector snapshot; the caller's folder filter and the trim to <see cref="NeighbourKeep"/> come after.</summary>
    public const int NeighbourPool = 50;
    public const int NeighbourKeep = 20;

    /// <summary>How far above an article's own typical similarity a neighbour must stand to count (Z score).</summary>
    public const float NeighbourMinZ = 2.0f;

    /// <summary>Similarity at which two articles are flagged as a possible duplicate.</summary>
    public const float PossibleDuplicateSimilarity = 0.95f;

    public const int MaxReasonTags = 3;

    /// <summary>True when a tag on <paramref name="df"/> of <paramref name="activeArticles"/> articles links nothing.</summary>
    public static bool IsCommonplace(int activeArticles, int df) =>
        df > Math.Max(CommonTagFloor, CommonTagFraction * activeArticles);

    /// <summary>The weight of a tag on <paramref name="df"/> articles; 0 for a commonplace.</summary>
    public static double TagWeight(int activeArticles, int df)
    {
        if (IsCommonplace(activeArticles, df)) return 0;
        return Math.Max(0, Math.Log((activeArticles + 1.0) / (df + 1.0)));
    }

    /// <summary>
    /// Scores and orders the candidates and cuts the list to <paramref name="limit"/>. A candidate whose
    /// total is zero is not related and is dropped; any other row shows at least 1.
    /// </summary>
    public static List<RelatedArticle> Rank(IReadOnlyList<RelatedCandidate> candidates, int limit)
    {
        double maxTag = 0;
        double maxZ = 0;
        foreach (var c in candidates)
        {
            maxTag = Math.Max(maxTag, c.TagScore);
            if (c.Similarity.HasValue) maxZ = Math.Max(maxZ, c.Z);
        }

        var scored = new List<(RelatedCandidate Candidate, double Total)>(candidates.Count);
        foreach (var c in candidates)
        {
            double tags = maxTag > 0 ? c.TagScore / maxTag : 0;
            double meaning = c.Similarity.HasValue && maxZ > 0 ? Math.Clamp(c.Z / maxZ, 0, 1) : 0;
            double total = TagShare * tags + SemanticShare * meaning;
            if (total > 0) scored.Add((c, total));
        }

        return scored
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Candidate.Id)
            .Take(limit)
            .Select(s => ToRow(s.Candidate, s.Total))
            .ToList();
    }

    private static RelatedArticle ToRow(RelatedCandidate c, double total) => new()
    {
        Id = c.Id,
        Title = c.Title,
        TreePath = c.TreePath,
        SharedConcepts = c.Tags
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxReasonTags)
            .Select(t => t.Name)
            .ToList(),
        Strength = Math.Clamp((int)Math.Round(total * 100, MidpointRounding.AwayFromZero), 1, 100),
        SemanticPercent = c.Similarity.HasValue
            ? Math.Clamp((int)Math.Round(c.Similarity.Value * 100, MidpointRounding.AwayFromZero), 0, 100)
            : null,
        PossibleDuplicate = c.Similarity >= PossibleDuplicateSimilarity
    };
}
