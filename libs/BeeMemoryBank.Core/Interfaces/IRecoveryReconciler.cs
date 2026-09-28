namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Keeps this full node's share of the blind-node recovery material published (BMB-43, plan 6.4 and
/// 6.8): a chain link for every DEK this node retired, and every sealed secret re-sealed under the
/// current DEK. Runs at every unlock and after a rotation; idempotent, silent when nothing is missing.
/// </summary>
public interface IRecoveryReconciler
{
    Task ReconcileAsync(CancellationToken ct = default);
}
