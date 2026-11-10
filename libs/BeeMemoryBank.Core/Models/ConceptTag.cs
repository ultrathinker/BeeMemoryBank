namespace BeeMemoryBank.Core.Models;

public class ConceptTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ConceptTagInfo
{
    public string Name { get; set; } = "";
    public int ArticleCount { get; set; }
}

/// <summary>
/// One row of an article's "related" list. <see cref="Strength"/> is 0..100 (it used to be the count of
/// shared tags); <see cref="SharedConcepts"/> holds only the RARE shared tags (a tag carried by a large
/// share of the vault links nothing and is left out), rarest first, at most three;
/// <see cref="SemanticPercent"/> is set when the two articles are also close by meaning.
/// </summary>
public class RelatedArticle
{
    /// <summary>Rows in a related list when the caller names no limit.</summary>
    public const int DefaultLimit = 7;

    /// <summary>The most rows a caller can ask for.</summary>
    public const int MaxLimit = 20;

    /// <summary>A caller-supplied limit brought into 1..<see cref="MaxLimit"/>.</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string TreePath { get; set; } = "";
    public List<string> SharedConcepts { get; set; } = [];
    public int Strength { get; set; }

    /// <summary>Cosine similarity of the two articles' vectors as a whole percent; null when they are not close by meaning.</summary>
    public int? SemanticPercent { get; set; }

    /// <summary>True when the similarity is so high (at least 95%) that the two may be copies of one another.</summary>
    public bool PossibleDuplicate { get; set; }

    /// <summary>Why this row is in the list, one short English phrase per reason (shared tags first), so every client shows the same words.</summary>
    public List<string> Reasons
    {
        get
        {
            var reasons = new List<string>();
            foreach (var tag in SharedConcepts)
                reasons.Add($"shared tag: {tag}");
            if (SemanticPercent is { } percent)
                reasons.Add($"similar meaning: {percent}%");
            if (PossibleDuplicate)
                reasons.Add("possible duplicate");
            return reasons;
        }
    }
}

public class ConceptTagWithEmbedding
{
    public string Name { get; set; } = "";
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModelVersion { get; set; }
}

public class ConceptGraphEdge
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public int Weight { get; set; }
}
