using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IDekRotationApplier
{
    Task AutoAcceptCommitAsync(SyncEvent commitEvent);

    /// <summary>
    /// Retries auto-accept for any tbl_dek_rotation_state rows in Committing state where the
    /// originator's whitelist entry has auto_accept_dek_rotation = true. Called after a
    /// successful UnlockAsync to recover from the case where COMMIT arrived while the session
    /// was locked. (Found by Claude R2 prod review CRIT-1.)
    /// </summary>
    Task RetryPendingAutoAcceptsAsync();
}
