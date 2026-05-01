namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Generates a vector representation of text.
/// Default implementation is deterministic hash-based.
/// </summary>
public interface IEmbeddingGenerator
{
    int Dimension { get; }
    float[] Generate(string text);
}
