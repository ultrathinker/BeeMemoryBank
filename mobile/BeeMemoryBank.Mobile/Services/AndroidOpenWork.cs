using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// What <c>App.OnStart</c> does with the database, as the open steps the vault gate runs at every open of an encrypted phone (docs/design/step-b-spec.md
/// 5.3 table 2, 5.8): 100 the schema, 200 the system folders, 800 the pragmas and the rotation auto-arm, 900 the media backfill (once per process). A
/// plain phone keeps running the same work at start, in <c>App.OnStart</c>.
/// </summary>
public static class AndroidOpenWork
{
    public static IServiceCollection AddAndroidOpenWork(this IServiceCollection services)
    {
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(100, (sp, _) => sp.GetRequiredService<MigrationRunner>().RunMigrationsAsync()));
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(200, (sp, _) => sp.GetRequiredService<FolderBootstrapper>().RunIfNeededAsync()));
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(800, async (sp, _) =>
        {
            using var conn = sp.GetRequiredService<DbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
            await PhoneRotationAutoArm.ArmAsync(conn);
        }));
        PhoneVaultLifecycle.AddOpenStep(services, new PhoneOpenWork(900, (sp, firstOpenInProcess) =>
        {
            if (!firstOpenInProcess) return Task.CompletedTask;
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var media = scope.ServiceProvider.GetRequiredService<MediaBlobBackfillService>();
                    await media.BackfillAsync();
                    await media.SweepRedundantEncFilesAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Media blob backfill error: {ex}");
                }
            });
            return Task.CompletedTask;
        }));
        return services;
    }
}
