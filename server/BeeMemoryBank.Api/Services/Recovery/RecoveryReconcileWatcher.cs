using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// Runs reconciliation after a rotation a PEER started (plan 6.4 "at start and after rotation"). The
/// unlock catch-up covers start; an auto-accepted rotation swaps the DEK of an open session with no
/// unlock at all, so this watches the current key's fingerprint and reconciles when it changes.
/// </summary>
public class RecoveryReconcileWatcher(
    IServiceScopeFactory scopes,
    SessionService session,
    ILogger<RecoveryReconcileWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? reconciledFor = null;
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var current = CurrentFingerprint();
                if (current == null || current == reconciledFor) continue;
                // The first sighting after start is the unlock catch-up's job; only a change counts.
                if (reconciledFor != null)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IRecoveryReconciler>().ReconcileAsync(stoppingToken);
                }
                reconciledFor = current;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recovery reconciliation watcher tick failed");
            }
        }
    }

    private string? CurrentFingerprint()
    {
        if (!session.IsUnlocked) return null;
        try
        {
            var dek = session.GetMasterDek();
            try { return DekFingerprint.Of(dek); }
            finally { Array.Clear(dek); }
        }
        catch (BeeMemoryBank.Core.Exceptions.SessionLockedException)
        {
            return null;
        }
    }
}
