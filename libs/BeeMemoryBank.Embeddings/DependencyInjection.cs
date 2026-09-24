using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Embeddings;

/// <summary>
/// DI helpers for the embedding subsystem: registers the ONNX-backed
/// <see cref="IEmbeddingGenerator"/> together with the embedding-side services
/// (<see cref="EmbeddingProjectionService"/>, <see cref="HybridSearchService"/>,
/// <see cref="ArticleChunker"/>) and resolves the sentencepiece + ONNX model on disk.
///
/// <para>
/// <c>AddOnnxEmbeddings</c> must run BEFORE <c>AddSync</c>: <see cref="BeeMemoryBank.Sync.DependencyInjection.AddSync"/>
/// calls <see cref="AddEmbeddingServices"/> internally, which registers the embedding-side services
/// that depend on the <see cref="IEmbeddingGenerator"/> resolved here. Without this order,
/// <see cref="EmbeddingProjectionService"/> fails to resolve at the first semantic-search call.
/// </para>
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Resolves the multilingual-e5-small ONNX model under <paramref name="dataDirectory"/> via
    /// <see cref="ModelManager"/> and registers a singleton <see cref="OnnxEmbeddingGenerator"/>
    /// keyed on <see cref="IEmbeddingGenerator"/>. Corrupt/missing models degrade to a sentinel
    /// path so the generator throws <c>ModelUnavailableException</c> on first use, the same way a
    /// missing model does.
    /// </summary>
    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var manager = new ModelManager(EmbeddingModelWiring.DefaultManifest, dataDirectory);
        var generatorPath =
            EmbeddingModelWiring.ResolveGeneratorPathAsync(manager).GetAwaiter().GetResult();
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(generatorPath));
        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="OnnxEmbeddingGenerator"/> from an in-memory model blob —
    /// the mobile / portable paths, where the model bytes arrive from the bundle rather than a
    /// separately downloaded file.
    /// </summary>
    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, byte[] modelBytes)
    {
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(modelBytes));
        return services;
    }

    /// <summary>
    /// Registers the embedding-side services: <see cref="EmbeddingProjectionService"/> (orchestrates
    /// projection-matrix lifecycle + per-article chunk projections), the singleton
    /// <see cref="ArticleChunker"/> that <see cref="EmbeddingProjectionService"/> and any direct
    /// caller share, and <see cref="HybridSearchService"/> (composes <see cref="BeeMemoryBank.Core.Services.SearchService"/>
    /// with semantic search via <see cref="EmbeddingProjectionService"/>). The chunker loads the
    /// embedded sentencepiece vocabulary once per process.
    /// </summary>
    public static IServiceCollection AddEmbeddingServices(this IServiceCollection services)
    {
        services.AddSingleton(_ => ArticleChunker.CreateDefault());
        services.AddScoped<EmbeddingProjectionService>();
        services.AddScoped<HybridSearchService>();
        return services;
    }
}