namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Generates a vector representation of text.
/// Default implementation is deterministic hash-based.
/// </summary>
public interface IEmbeddingGenerator
{
    int Dimension { get; }

    /// <summary>
    /// Stable identifier for the model that produced the embeddings this generator returns
    /// (e.g. <c>"multilingual-e5-small-v1"</c>). Persisted alongside each embedding row so a
    /// future model swap can flag stale rows for re-generation -- dimension alone would miss a
    /// same-dimension model swap. Lives on the interface (not the concrete <c>OnnxEmbeddingGenerator</c>)
    /// so callers in Core (e.g. <c>ConceptTagService</c>) can read it without taking a dependency
    /// on the embedding implementation project.
    /// </summary>
    string Version { get; }

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
