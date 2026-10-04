using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Storage;

/// <summary>
/// The full node's storage registrations: everything every node needs (<see cref="NodeDependencyInjection.AddNodeStorage"/>, in the
/// shared Storage) plus the repositories and stores that only a node holding the master key uses.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddStorage(this IServiceCollection services, string dataPath)
    {
        services.AddNodeStorage(dataPath);

        // Encrypted-at-rest search index segments. Files live in a sibling directory next
        // to the sqlite DB (dataPath may itself be a directory or a ".db" file path -- mirror
        // DbConnectionFactory's own handling of both shapes rather than duplicating its logic).
        var segmentsDirectory = Path.Combine(
            Path.GetExtension(dataPath)?.Equals(".db", StringComparison.OrdinalIgnoreCase) == true
                ? Path.GetDirectoryName(dataPath) ?? dataPath
                : dataPath,
            "search-index-segments");
        services.AddScoped<SegmentManifestRepository>();
        services.AddScoped<SegmentTombstoneRepository>();
        services.AddScoped(sp => new EncryptedSegmentStore(
            sp.GetRequiredService<SegmentManifestRepository>(),
            sp.GetRequiredService<Core.Services.SessionService>(),
            segmentsDirectory));

        services.AddScoped<IArticleChunkEmbeddingRepository, ArticleChunkEmbeddingRepository>();
        services.AddSingleton<IRetiredMasterDekStore, RetiredMasterDekStore>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IFavoriteRepository, FavoriteRepository>();
        services.AddScoped<IProjectionMatrixRepository, ProjectionMatrixRepository>();
        services.AddScoped<IArticleVersionRepository, ArticleVersionRepository>();

        return services;
    }
}
