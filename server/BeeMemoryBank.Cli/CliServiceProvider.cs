using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            // Console-backed logging at Warning+ level. Surfaces lazy-rewrap warnings,
            // signature mismatches, and other security-relevant signals directly to the
            // operator running the CLI. Previously we used .AddLogging() with no provider
            // (null sink) — security audits flagged that intentional warnings would
            // disappear silently. Info+ would be too chatty for a CLI.
            .AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                              .SetMinimumLevel(LogLevel.Warning))
            .AddStorage(dataPath)
            .AddCore()
            .AddOnnxEmbeddings()
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
