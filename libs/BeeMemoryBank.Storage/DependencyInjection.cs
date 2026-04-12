using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Storage;

public static class DependencyInjection
{
    public static IServiceCollection AddStorage(this IServiceCollection services, string dataPath)
    {
        DapperConfig.Configure();

        var factory = new DbConnectionFactory(dataPath);
        services.AddSingleton(factory);
        services.AddSingleton<MigrationRunner>();

        services.AddScoped<IArticleRepository, ArticleRepository>();
        services.AddScoped<IArticleBodyRepository, ArticleBodyRepository>();
        services.AddSingleton<IKeySlotRepository, KeySlotRepository>();
        services.AddScoped<INodeIdentityRepository, NodeIdentityRepository>();
        services.AddScoped<IWhitelistRepository, WhitelistRepository>();
        services.AddScoped<IEventLogRepository, EventLogRepository>();
        services.AddScoped<ISyncPositionRepository, SyncPositionRepository>();
        services.AddScoped<ISyncPushPositionRepository, SyncPushPositionRepository>();
        services.AddScoped<ITombstoneRepository, TombstoneRepository>();
        services.AddScoped<IConflictVersionRepository, ConflictVersionRepository>();
        services.AddScoped<IProjectionMatrixRepository, ProjectionMatrixRepository>();
        services.AddScoped<ICommentRepository, CommentRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IFolderRestrictionRepository, FolderRestrictionRepository>();
        services.AddScoped<IArticleVersionRepository, ArticleVersionRepository>();
        services.AddScoped<FolderBootstrapper>();

        return services;
    }
}
