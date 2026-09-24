namespace BeeMemoryBank.Core.Models;

/// <summary>One chunk row (article, chunk index, int8-quantized projection, dequantization scale).</summary>
public sealed record ArticleChunkEmbeddingRow(Guid ArticleId, int ChunkIndex, byte[] Projection, float Scale);
