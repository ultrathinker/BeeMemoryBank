using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The backstop of docs/design/step-b-spec.md 4.3: while the sealed database is open, every 10 seconds compares its
/// <c>unlock_material_version</c> (bumped by the triggers of migration 040 inside the writing transaction) with the one the boot store
/// last mirrored and rewrites the boot store when they differ. A writer this specification did not foresee is therefore mirrored within
/// seconds. Idle on a plain node, on a closed one and on a host without a gate (the close itself rewrites, 5.4).
/// </summary>
public sealed class UnlockMaterialWatcher(UnlockMaterialMirror mirror, IVaultGate? gate = null, ILogger<UnlockMaterialWatcher>? logger = null,
    TimeSpan? interval = null) : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _interval = interval ?? DefaultInterval;

    /// <summary>One check. True when it rewrote the boot store. Never throws for a database that closed meanwhile.</summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        if (gate is not { IsSealed: true, State: VaultState.Open or VaultState.Importing }) return false;
        try
        {
            return await mirror.RewriteIfBehindAsync(ct).ConfigureAwait(false);
        }
        catch (VaultSealedException)
        {
            logger?.LogDebug("The database closed during the unlock-material check.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The unlock-material check failed; it is repeated at the next tick.");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
