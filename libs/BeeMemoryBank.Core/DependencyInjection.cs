using BeeMemoryBank.Core.Embeddings;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton<SessionService>();
        services.AddSingleton<InvisibleModeService>();
        services.AddSingleton<MaintenanceModeService>();

        // Null implementations are replaced with real ones when BeeMemoryBank.Sync / Api / Cli is registered
        services.TryAddSingleton<ILamportClock, NullLamportClock>();
        services.TryAddScoped<IEventLogger, NullEventLogger>();
        services.TryAddSingleton<IActorProvider>(new NullActorProvider());

        services.AddScoped<InitializationService>();
        services.AddScoped<ArticleService>();
        services.AddScoped<KeyManagementService>();
        services.AddScoped<TreeService>();
        services.AddScoped<SearchService>();
        services.AddScoped<EmbeddingProjectionService>();
        services.AddScoped<FolderService>();
        services.AddScoped<CopyService>();
        services.AddScoped<CommentService>();
        services.AddScoped<MediaService>();
        services.AddScoped<UserService>();
        services.AddScoped<FolderAccessService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<ObsidianImportService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        services.AddScoped<RemoteAccountService>();
        services.AddScoped<RemoteEventApplier>();
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        // Resolve + verify the model up front via ModelManager (its SHA-256 result is cached on disk
        // after the first run). Only a Valid resolution hands the real path to OnnxEmbeddingGenerator;
        // Corrupt and NotFound both yield a non-existent sentinel path so the generator degrades to
        // ModelUnavailableException exactly as it already does for a missing model, and a corrupt file
        // is never loaded into the ONNX runtime. See EmbeddingModelWiring for the placeholder hash.
        var manager = new ModelManager(EmbeddingModelWiring.DefaultManifest, dataDirectory);
        var generatorPath =
            EmbeddingModelWiring.ResolveGeneratorPathAsync(manager).GetAwaiter().GetResult();
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(generatorPath));
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, byte[] modelBytes)
    {
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(modelBytes));
        return services;
    }
}
