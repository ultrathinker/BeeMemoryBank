using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class ConceptTagService(
    IConceptTagRepository repo,
    IEmbeddingGenerator embeddingGenerator,
    IEventLogger eventLogger)
{
    private const string ModelVersion = OnnxEmbeddingGenerator.Version;

    public async Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit = 100)
    {
        if (!string.IsNullOrEmpty(filter) && filter.StartsWith('~'))
        {
            var query = filter[1..].Trim();
            if (string.IsNullOrEmpty(query))
                return await repo.ListAsync(null, limit);
            return await SemanticSearchAsync(query, limit);
        }
        return await repo.ListAsync(filter, limit);
    }

    private async Task<List<ConceptTagInfo>> SemanticSearchAsync(string query, int limit)
    {
        var queryEmbedding = embeddingGenerator.Generate(query);
        var allWithEmbeddings = await repo.GetWithEmbeddingsAsync();

        if (allWithEmbeddings.Count == 0)
            return await repo.ListAsync(query, limit);

        var scored = new List<(string Name, float Score)>();
        foreach (var ct in allWithEmbeddings)
        {
            if (ct.Embedding == null) continue;
            var ctEmbedding = BytesToFloats(ct.Embedding);
            var score = CosineSimilarity(queryEmbedding, ctEmbedding);
            scored.Add((ct.Name, score));
        }

        var topNames = scored
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var all = await repo.GetAllAsync();
        return all
            .Where(c => topNames.Contains(c.Name))
            .OrderByDescending(c => scored.First(s => s.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Score)
            .ToList();
    }

    public async Task SetForArticleAsync(Guid articleId, List<string> conceptNames)
    {
        var existingAll = await repo.GetWithEmbeddingsAsync();
        var existingNames = new HashSet<string>(existingAll.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        await repo.SetForArticleAsync(articleId, conceptNames);

        foreach (var name in conceptNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existingNames.Contains(name))
            {
                var embedding = embeddingGenerator.Generate(name);
                await repo.UpdateEmbeddingAsync(name, FloatsToBytes(embedding), ModelVersion);
            }
        }
    }

    public async Task AddToArticleAsync(Guid articleId, List<string> conceptNames)
    {
        var existingAll = await repo.GetWithEmbeddingsAsync();
        var existingNames = new HashSet<string>(existingAll.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        await repo.AddToArticleAsync(articleId, conceptNames);

        foreach (var name in conceptNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existingNames.Contains(name))
            {
                var embedding = embeddingGenerator.Generate(name);
                var bytes = FloatsToBytes(embedding);
                await repo.UpdateEmbeddingAsync(name, bytes, ModelVersion);
            }
        }
    }

    public async Task BackfillEmbeddingsAsync()
    {
        var all = await repo.GetAllAsync();
        var withEmbeddings = await repo.GetWithEmbeddingsAsync();
        var hasEmbedding = new HashSet<string>(withEmbeddings.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var concept in all)
        {
            if (!hasEmbedding.Contains(concept.Name))
            {
                var embedding = embeddingGenerator.Generate(concept.Name);
                var bytes = FloatsToBytes(embedding);
                await repo.UpdateEmbeddingAsync(concept.Name, bytes, ModelVersion);
            }
        }
    }

    public Task<List<string>> GetByArticleIdAsync(Guid articleId) => repo.GetByArticleIdAsync(articleId);
    public Task RemoveFromArticleAsync(Guid articleId, string conceptName) => repo.RemoveFromArticleAsync(articleId, conceptName);
    public async Task RenameAsync(string name, string newName)
    {
        await repo.RenameAsync(name, newName);
        var embedding = embeddingGenerator.Generate(newName);
        await repo.UpdateEmbeddingAsync(newName, FloatsToBytes(embedding), ModelVersion);
        await eventLogger.LogConceptTagRenameAsync(name, newName);
    }

    public async Task MergeAsync(string source, string target)
    {
        await repo.MergeAsync(source, target);
        await eventLogger.LogConceptTagMergeAsync(source, target);
    }

    public async Task DeleteAsync(string name)
    {
        await repo.DeleteAsync(name);
        await eventLogger.LogConceptTagDeleteAsync(name);
    }
    public Task<List<RelatedArticle>> GetRelatedArticlesAsync(Guid articleId) => repo.GetRelatedArticlesAsync(articleId);
    public Task<List<(Guid Id, string Title, string TreePath)>> SearchByConceptAsync(string concept) => repo.SearchByConceptAsync(concept);

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f, normA = 0f, normB = 0f;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
