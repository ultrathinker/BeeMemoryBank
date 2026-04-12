using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Deterministic hash-based embedding generator.
/// Used as a fallback when the ONNX model is unavailable.
/// Preserves semantic properties: identical texts → identical vectors.
/// </summary>
public class HashBasedEmbeddingGenerator(int dimension = 384) : IEmbeddingGenerator
{
    public int Dimension => dimension;

    public float[] Generate(string text)
    {
        var embedding = new float[dimension];
        var words = text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

        // AUDIT NOTE: new Random(seed) is created per-word, not shared across threads.
        // Thread safety is guaranteed by local scope — no static/singleton Random instance.
        foreach (var word in words)
        {
            var wordHash = SHA256.HashData(Encoding.UTF8.GetBytes(word));
            var rng = new Random(BitConverter.ToInt32(wordHash, 0));
            for (int i = 0; i < dimension; i++)
                embedding[i] += (float)(rng.NextDouble() * 2 - 1);
        }

        // If no words — use the hash of the entire text
        if (words.Length == 0)
        {
            var textHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            var rng = new Random(BitConverter.ToInt32(textHash, 0));
            for (int i = 0; i < dimension; i++)
                embedding[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        // L2 normalization
        float norm = 0f;
        for (int i = 0; i < dimension; i++) norm += embedding[i] * embedding[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (int i = 0; i < dimension; i++) embedding[i] /= norm;

        return embedding;
    }
}
