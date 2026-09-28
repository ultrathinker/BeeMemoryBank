using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>Blind-node recovery services of a full Api node (BMB-43, plan 5.5 and 6).</summary>
public static class RecoveryApiServices
{
    public static IServiceCollection AddRecoveryApi(this IServiceCollection services)
    {
        services.AddSingleton<IRecoveryHost, CurrentRecoveryHost>();
        // The peers' my-standing, over the default "not known to be a superadmin" of AddRecovery.
        services.RemoveAll<IOwnStandingProvider>();
        services.AddSingleton<IOwnStandingProvider, PeerOwnStanding>();
        services.AddScoped<RecoveryBoxQueries>();
        services.AddScoped<RecoveryStatusService>();
        services.AddSingleton<StrongBoxService>();
        services.AddSingleton<RecoveryCleanupService>();
        services.AddSingleton<RecoveryTriggers>();
        services.AddHostedService<RecoveryReconcileWatcher>();
        services.AddSingleton<StateAnchorScheduler>();

        // The recovery sections of /api/blind/status, and the recovery set the blind node's backups write.
        services.AddSingleton<RecoveryAnchorStatusCache>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryAnchorStatusContributor>();
        services.AddScoped<BeeMemoryBank.Api.Services.BlindStatus.IBlindStatusContributor, RecoveryBoxesStatusContributor>();
        services.AddSingleton<BeeMemoryBank.Api.Services.BlindBackup.IRecoverySetSource, RecoverySetSource>();
        services.AddHostedService(sp => sp.GetRequiredService<StateAnchorScheduler>());

        // Restore: the blind side (codes) and the new device's side.
        services.TryAddSingleton<INodeRole, EnvironmentNodeRole>();
        services.AddScoped<BlindRestoreCodeService>();
        services.AddSingleton<RecoveryRestoreService>();
        services.AddSingleton<BlindRestoreClient>();
        services.AddSingleton<IBackupFileRestoreSource, BeeMemoryBank.Api.Services.BlindPhone.AndroidBackupRestoreSource>();
        services.AddSingleton<RestoreProgress>();
        services.AddHttpClient();
        return services;
    }
}
