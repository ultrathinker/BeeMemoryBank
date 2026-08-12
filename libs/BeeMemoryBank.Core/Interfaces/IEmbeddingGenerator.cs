namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Generates a vector representation of text.
/// Default implementation is deterministic hash-based.
/// </summary>
public interface IEmbeddingGenerator
{
    int Dimension { get; }

    /// <summary>Embeds text meant to be indexed (article/chunk content).</summary>
    float[] Generate(string text);

    /// <summary>
    /// Embeds text meant to be searched-for: search queries, or symmetric similarity comparisons
    /// (e.g. concept-tag matching) where there's no real "document" side. Asymmetric embedding
    /// models (e.g. E5) need the two sides embedded differently to perform well; the default
    /// implementation here just delegates to <see cref="Generate"/> for generators (fakes, hash-based)
    /// that have no such distinction.
    /// </summary>
    float[] GenerateQuery(string text) => Generate(text);
}
