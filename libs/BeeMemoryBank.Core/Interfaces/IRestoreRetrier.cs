namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Minimal Core-visible surface for retrying stuck network-restore events. Lives here
/// (not in Sync) so SessionService can call it from UnlockCoreAsync without Core
/// taking a Sync dependency. The full IRestoreInitiator in Sync extends this — server
/// implementation (SnapshotRestoreService) implements both.
/// </summary>
public interface IRestoreRetrier
{
    /// <summary>
    /// Sweeps tbl_restore_event_state for rows stuck in Pending/Downloading/Applying where
    /// the originator has auto_accept_restore = true, and re-runs the accept flow. Mirrors
    /// IDekRotationApplier.RetryPendingAutoAcceptsAsync. Idempotent — safe to call from
    /// multiple triggers (unlock + sync pull).
    /// </summary>
    Task RetryPendingRestoresAsync();
}
