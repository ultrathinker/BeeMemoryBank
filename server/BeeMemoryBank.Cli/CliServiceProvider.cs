using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli;

public static class CliServiceProvider
{
    /// <summary>
    /// Creates a DI container for CLI commands, runs migrations and initializes Lamport clock.
    /// The caller is responsible for calling Dispose() on the returned ServiceProvider.
    /// </summary>
    public static async Task<ServiceProvider> CreateAsync(string dataPath)
    {
        Directory.CreateDirectory(dataPath);

        var services = new ServiceCollection()
            .AddStorage(dataPath)
            .AddCore()
            .AddSync()
            .AddSingleton<IActorProvider>(new CliActorProvider())
            .BuildServiceProvider();

        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

            // Restore Lamport clock from DB
            var maxTs = await scope.ServiceProvider
                .GetRequiredService<IEventLogRepository>()
                .GetMaxLamportTimestampAsync();
            services.GetRequiredService<LamportClock>().Initialize(maxTs);
        }

        return services;
    }
}
