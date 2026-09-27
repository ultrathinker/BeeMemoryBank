using BeeMemoryBank.Api.Services.BlindConsole;
using BeeMemoryBank.Api.Services.BlindStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Container wiring for the blind node's own subsystems (BMB-54): the base contributor of
/// <c>/api/blind/status</c>, backups, the job/CPU-mode manager, the backup schedule, console
/// login, the wipe. One extension so ApiServices.cs keeps a single registration line for all of
/// it; the recovery and pairing status providers register from their own modules.
///
/// <para>Everything here is a singleton on purpose: the job manager, settings store and restic
/// runner hold process-wide state (the running job, the pause gate, the registered child
/// process), and the schedule service is an IHostedService. The core status contributor is
/// scoped: it reads repositories that are scoped themselves.</para>
/// </summary>
public static class BlindBackupServiceCollectionExtensions
{
    public static IServiceCollection AddBlindNodeServices(this IServiceCollection services, string dataPath)
    {
        services.AddScoped<IBlindStatusContributor>(
            sp => ActivatorUtilities.CreateInstance<CoreBlindStatusContributor>(sp, dataPath));
        services.AddSingleton(new BlindBackupSettingsStore(dataPath));
        services.AddSingleton(sp => new BlindJobManager(
            dataPath, sp.GetRequiredService<ILogger<BlindJobManager>>()));
        services.AddSingleton<IResticRunner>(sp => new ResticRunner(
            sp.GetRequiredService<BlindJobManager>(),
            dataPath,
            sp.GetRequiredService<ILogger<ResticRunner>>()));
        // RecoverySetBuilder (BMB-53) replaces this registration when it merges.
        services.AddSingleton<IRecoverySetSource, StubRecoverySetSource>();
        services.AddSingleton<RecoverySetWriter>();
        services.AddSingleton<ResticRepositoryLock>();
        services.AddSingleton(sp => new BlindBackupService(
            sp.GetRequiredService<Core.Interfaces.IDbConnectionFactory>(),
            sp.GetRequiredService<BlindBackupSettingsStore>(),
            sp.GetRequiredService<IResticRunner>(),
            sp.GetRequiredService<IRecoverySetSource>(),
            sp.GetRequiredService<BlindJobManager>(),
            sp.GetRequiredService<RecoverySetWriter>(),
            sp.GetRequiredService<ResticRepositoryLock>(),
            dataPath,
            sp.GetRequiredService<ILogger<BlindBackupService>>())
            .CleanupStaleStage());

        services.AddSingleton<BlindBackupStatusContributor>();
        services.AddSingleton<IBlindStatusContributor>(sp =>
            sp.GetRequiredService<BlindBackupStatusContributor>());
        services.AddSingleton(sp => new BlindConsoleAuthService(
            dataPath, sp.GetRequiredService<ILogger<BlindConsoleAuthService>>()));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<BlindWipeService>(sp, dataPath));
        services.AddHostedService<BlindBackupScheduleService>();
        return services;
    }
}
