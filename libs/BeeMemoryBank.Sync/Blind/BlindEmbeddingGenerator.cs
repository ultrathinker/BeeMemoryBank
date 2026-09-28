using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The embedding generator of a blind node (plan 3.4): no model is loaded and nothing is ever
/// produced. It answers the way a node with a missing model already does —
/// <see cref="ModelUnavailableException"/> — so every caller takes its existing "no embeddings"
/// path (concept tags are stored without vectors) instead of writing zero vectors that would look
/// like real ones.
/// </summary>
public sealed class BlindEmbeddingGenerator : IEmbeddingGenerator
{
    public int Dimension => 0;

    public string Version => "none (blind node)";

    public float[] Generate(string text) =>
        throw new ModelUnavailableException("A blind node generates no embeddings.");
}
