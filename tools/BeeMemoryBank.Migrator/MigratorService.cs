using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Migrator;

public record MigrationOptions(
    string V1DbPath,
    string V2DataPath,
    string Password,
    string NodeName = "MigratedNode",
    bool DryRun = false
);

public record MigrationResult(int Migrated, int Skipped, int Failed);

public class MigratorService
{
    private readonly MigrationOptions _opts;
    private readonly TextWriter _out;

    public MigratorService(MigrationOptions opts, TextWriter? output = null)
    {
        _opts = opts;
        _out = output ?? Console.Out;
    }

    public async Task<MigrationResult> RunAsync()
    {
        DapperConfig.Configure();

        var reader = new V1Reader(_opts.V1DbPath);
        var v1Nodes    = reader.ReadNodes();
        var v1Articles = reader.ReadArticles();
        var v1Tags     = reader.ReadArticleTags();
        var nodePathMap = V1Reader.BuildNodePaths(v1Nodes);

        _out.WriteLine($"v1: {v1Nodes.Count} active nodes, {v1Articles.Count} active articles");

        if (_opts.DryRun)
        {
            foreach (var v1Article in v1Articles)
            {
                var treePath = nodePathMap.TryGetValue(v1Article.NodeId, out var p) ? p : "/Imported";
                var tags = v1Tags.TryGetValue(v1Article.Id, out var t) ? t : [];
                _out.WriteLine($"  (dry-run) \"{v1Article.Title}\" → {treePath} [{string.Join(", ", tags)}]");
            }
            return new MigrationResult(0, v1Articles.Count, 0);
        }

        Directory.CreateDirectory(_opts.V2DataPath);

        var services = new ServiceCollection()
            .AddStorage(_opts.V2DataPath)
            .AddCore()
            .AddSync()
            .BuildServiceProvider();

        // Run database migrations
        using (var scope = services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await runner.RunMigrationsAsync();
        }

        // Restore Lamport clock
        {
            using var scope = services.CreateScope();
            var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
            services.GetRequiredService<LamportClock>().Initialize(maxTs);
        }

        // Initialize node if needed
        {
            using var scope = services.CreateScope();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var existing = await nodeRepo.GetAsync();
            if (existing == null)
            {
                var initService = scope.ServiceProvider.GetRequiredService<InitializationService>();
                await initService.InitializeAsync(_opts.NodeName, _opts.Password);
                _out.WriteLine($"Node '{_opts.NodeName}' created.");
            }
            else
            {
                _out.WriteLine($"v2 node already initialized: {existing.DisplayName}");
            }
        }

        // Unlock session
        var session = services.GetRequiredService<SessionService>();
        await session.UnlockAsync(_opts.Password);

        // Create ArticleService
        var articleService = new ArticleService(
            services.GetRequiredService<IArticleRepository>(),
            services.GetRequiredService<IArticleBodyRepository>(),
            session,
            services.GetRequiredService<INodeIdentityRepository>(),
            services.GetRequiredService<ILamportClock>(),
            services.GetRequiredService<IEventLogger>(),
            services.GetRequiredService<IMediaRepository>(),
            services.GetRequiredService<IFolderRepository>(),
            services.GetRequiredService<IArticleVersionRepository>(),
            new NullActorProvider()
        );

        // Migrate articles
        int migrated = 0, skipped = 0, failed = 0;

        foreach (var v1Article in v1Articles)
        {
            var treePath = nodePathMap.TryGetValue(v1Article.NodeId, out var p) ? p : "/Imported";
            var tags = v1Tags.TryGetValue(v1Article.Id, out var t) ? t : [];

            _out.Write($"  [{migrated + skipped + failed + 1}/{v1Articles.Count}] \"{v1Article.Title}\" → {treePath} ... ");

            try
            {
                await articleService.CreateAsync(v1Article.Title, treePath, tags, v1Article.Content);
                _out.WriteLine("OK");
                migrated++;
            }
            catch (Exception ex)
            {
                _out.WriteLine($"FAIL: {ex.Message}");
                failed++;
            }
        }

        return new MigrationResult(migrated, skipped, failed);
    }
}
