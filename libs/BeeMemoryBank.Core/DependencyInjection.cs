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
        services.AddScoped<CommentService>();
        services.AddScoped<MediaService>();
        services.AddScoped<UserService>();
        services.AddScoped<FolderAccessService>();
        services.AddScoped<ConceptTagService>();
        services.AddScoped<ObsidianImportService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, string? modelPath = null)
    {
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(modelPath));
        return services;
    }

    public static IServiceCollection AddOnnxEmbeddings(this IServiceCollection services, byte[] modelBytes)
    {
        services.AddSingleton<IEmbeddingGenerator>(_ => new OnnxEmbeddingGenerator(modelBytes));
        return services;
    }
}
