using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>
/// Moves every open removal this node drives, every <see cref="Interval"/> (plan E2: resumable after a crash or a restart). A tick reads what the nodes
/// reported and carries the process as far as it goes without a password; S4 then waits for "continue" with the administrator's password.
/// Paused under the <c>Testing</c> environment (tests call <see cref="OffboardingService.ResumeAsync"/> themselves).
/// </summary>
public sealed class OffboardingDriver(OffboardingService service, IServiceScopeFactory scopes, ILogger<OffboardingDriver> logger,
    IHostEnvironment? environment = null, IVaultGate? gate = null) : BackgroundService
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

    public bool PauseBackground { get; set; } = environment?.IsEnvironment("Testing") == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
            if (PauseBackground) continue;
            try
            {
                // A sealed node's database is read only once a door has opened it (spec 5.5).
                if (gate is not null) await gate.WaitUsableAsync(stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Moving the open removals failed this time ({Failure}); the next tick tries again.", ex.GetType().Name);
            }
        }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        // A closed sealed database (or a node not set up) has nothing to move yet.
        if (scope.ServiceProvider.GetService<IVaultGate>() is { State: not VaultState.Open and not VaultState.Plain }) return;
        var self = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        if (self is null) return;
        var store = new OffboardingStore(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
        foreach (var record in await store.ListAsync(openOnly: true))
            if (record.DriverNodeId == self && record.Origin == "local")
                await service.ResumeAsync(record.ProcessId, ct);
            else
                await service.ServeRekeyRequestsAsync(record.ProcessId, ct);   // a shared node asked for a new identity key (S1)
    }
}
