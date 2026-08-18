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

    public async Task<List<ConceptTagInfo>> ListAsync(string? filter, int limit = 100, int offset = 0)
    {
        if (!string.IsNullOrEmpty(filter) && filter.StartsWith('~'))
        {
            var query = filter[1..].Trim();
            if (string.IsNullOrEmpty(query))
                return await repo.ListAsync(null, limit, offset);
            return await SemanticSearchAsync(query, limit, offset);
        }
        return await repo.ListAsync(filter, limit, offset);
    }

    private async Task<List<ConceptTagInfo>> SemanticSearchAsync(string query, int limit, int offset = 0)
    {
        float[] queryEmbedding;
        try
        {
            // Concept-tag matching is a symmetric similarity comparison (tag name vs. tag name),
            // not asymmetric retrieval -- E5's own model card recommends the "query: " prefix for
            // both sides in that case, so every tag embedding below also uses GenerateQuery.
            queryEmbedding = embeddingGenerator.GenerateQuery(query);
        }
        catch (ModelUnavailableException)
        {
            return await repo.ListAsync(query, limit, offset);
        }
        var allWithEmbeddings = await repo.GetWithEmbeddingsAsync();

        if (allWithEmbeddings.Count == 0)
            return await repo.ListAsync(query, limit, offset);

        // GetWithEmbeddingsAsync is vault-wide (no scope filter); GetAllAsync is scope-filtered.
        // Restrict to visible names BEFORE ranking/windowing, not after -- otherwise Skip(offset)
        // advances over the global rank instead of the caller's visible rank, and a tag the
        // caller can see but that ranks just outside the global top `limit` becomes permanently
        // unreachable through any page.
        var all = await repo.GetAllAsync();
        var visibleNames = new HashSet<string>(all.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var scored = new List<(string Name, float Score)>();
        foreach (var ct in allWithEmbeddings)
        {
            if (ct.Embedding == null) continue;
            if (!visibleNames.Contains(ct.Name)) continue;
            var ctEmbedding = BytesToFloats(ct.Embedding);
            var score = CosineSimilarity(queryEmbedding, ctEmbedding);
            scored.Add((ct.Name, score));
        }

        var topNames = scored
            .OrderByDescending(x => x.Score)
            .Skip(offset)
            .Take(limit)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                try
                {
                    var embedding = embeddingGenerator.GenerateQuery(name);
                    await repo.UpdateEmbeddingAsync(name, FloatsToBytes(embedding), ModelVersion);
                }
                catch (ModelUnavailableException)
                {
                    break;
                }
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
                try
                {
                    var embedding = embeddingGenerator.GenerateQuery(name);
                    var bytes = FloatsToBytes(embedding);
                    await repo.UpdateEmbeddingAsync(name, bytes, ModelVersion);
                }
                catch (ModelUnavailableException)
                {
                    break;
                }
            }
        }
    }

    public async Task BackfillEmbeddingsAsync()
    {
        var all = await repo.GetAllAsync();
        var withEmbeddings = await repo.GetWithEmbeddingsAsync();

        // Built with a loop (not ToDictionary) because tbl_concept_tag.name is UNIQUE but not
        // COLLATE NOCASE at the DB level -- every current write path enforces case-insensitive
        // uniqueness before insert, but older rows predating that enforcement can still collide
        // under OrdinalIgnoreCase, which made ToDictionary throw in production. Last one wins,
        // which is fine here: this is only a "do I already have a current version?" lookup, not
        // the source of truth.
        var currentVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in withEmbeddings)
        {
            currentVersions[c.Name] = c.EmbeddingModelVersion;
        }

        foreach (var concept in all)
        {
            // Needs (re)embedding if it has none yet, or its stored embedding was generated by a
            // model version that no longer matches the active one (e.g. after a model swap) --
            // dimension alone wouldn't catch a same-dimension model swap.
            if (!currentVersions.TryGetValue(concept.Name, out var storedVersion) || storedVersion != ModelVersion)
            {
                // Missing model applies uniformly to every remaining concept - let it
                // propagate rather than retry per-tag; PendingEmbeddingProcessor catches
                // this centrally to avoid per-cycle log spam.
                var embedding = embeddingGenerator.GenerateQuery(concept.Name);
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
        try
        {
            var embedding = embeddingGenerator.GenerateQuery(newName);
            await repo.UpdateEmbeddingAsync(newName, FloatsToBytes(embedding), ModelVersion);
        }
        catch (ModelUnavailableException)
        {
        }
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
