using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Infrastructure.AutoUnlock;

/// <summary>The open step <c>StaleServerSlot</c> (docs/design/step-b-spec.md 11.2, order 50, every open): deletes a stale server slot of a sealed node at the first unlock.</summary>
public sealed class StaleServerSlotOpenStep(ServerAutoUnlockService service) : IVaultOpenStep
{
    public const int StepOrder = 50;

    public int Order => StepOrder;

    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => service.RetireStaleSlotsInOpenDatabaseAsync(ct);
}

/// <summary>After a node reset the enable record and a node-created key file go with the slot (spec 11.2 item 7).</summary>
public sealed class ServerAutoUnlockResetHook(ServerAutoUnlockService service) : INodeResetHook
{
    public Task AfterVaultWipedAsync(CancellationToken ct) => service.DisableLocalAsync(ct);
}

/// <summary>
/// Runs the start step of the server checkbox once, when the host starts (after the startup tasks, which have run the migrations and the
/// OS auto-unlock step). A host that calls <see cref="ServerAutoUnlockService.RunAtStartAsync"/> from its own start sequence registers with
/// <c>startAtHostStart: false</c>.
/// </summary>
public sealed class ServerAutoUnlockStartup(ServerAutoUnlockService service, IServiceScopeFactory scopes, ILogger<ServerAutoUnlockStartup>? logger = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var outcome = await service.RunAtStartAsync(scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>(), cancellationToken).ConfigureAwait(false);
            if (outcome == ServerAutoUnlockStartOutcome.Unlocked)
                logger?.LogInformation("Session unlocked by 'start unlocked after a restart'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "'Start unlocked after a restart' failed at startup (non-fatal); manual unlock required.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ServerAutoUnlockServiceCollectionExtensions
{
    /// <summary>
    /// Registers "start unlocked after a restart" for servers (docs/design/step-b-spec.md 11.2): the role of this process (read once from
    /// <c>BMB_HOST_ROLE</c>), the service, the stale-slot open step, the reset hook and - unless the host runs the step itself - a hosted service
    /// that runs it at start. Registering creates nothing on disk and reads no key file. Not registered on a blind host.
    /// </summary>
    public static IServiceCollection AddServerAutoUnlock(this IServiceCollection services, string dataPath, bool startAtHostStart = true)
    {
        // A nullable so that "not registered" is distinguishable from the default value; read once, at the first request for it.
        services.TryAdd(ServiceDescriptor.Singleton(typeof(HostRole?), _ => (HostRole?)HostRoles.FromEnvironment()));
        services.TryAddSingleton<IServerKeyFileStore, PhysicalServerKeyFileStore>();
        services.TryAddSingleton(sp => new ServerAutoUnlockService(
            sp.GetRequiredService<IKeySlotRepository>(),
            sp.GetRequiredService<SessionService>(),
            dataPath,
            sp.GetService<HostRole?>() ?? HostRole.Server,
            sp.GetService<IDbConnectionFactory>(),
            sp.GetService<IUnlockMaterialMirror>(),
            sp.GetService<ISealedAutoUnlockHost>(),
            sp.GetService<BootStore>(),
            sp.GetService<IServiceScopeFactory>(),
            sp.GetService<ILogger<ServerAutoUnlockService>>(),
            sp.GetService<IServerKeyFileStore>()));
        services.TryAddSingleton<IServerAutoUnlockLocalState>(sp => sp.GetRequiredService<ServerAutoUnlockService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVaultOpenStep, StaleServerSlotOpenStep>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INodeResetHook, ServerAutoUnlockResetHook>());
        if (startAtHostStart) services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ServerAutoUnlockStartup>());
        return services;
    }
}
