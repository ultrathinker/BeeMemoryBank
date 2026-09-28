using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IDekRotationApplier
{
    /// <summary>
    /// False when applying a rotation needs nobody's decision on this node, so a commit is applied
    /// whatever the auto-accept flag on its originator's row says. Only a blind node's applier
    /// says so: it re-wraps nothing and has no admin to accept anything.
    /// </summary>
    bool RequiresApproval => true;

    Task AutoAcceptCommitAsync(SyncEvent commitEvent);

    /// <summary>
    /// Retries auto-accept for any tbl_dek_rotation_state rows in Committing state where the
    /// originator's whitelist entry has auto_accept_dek_rotation = true. Called after a
    /// successful UnlockAsync to recover from the case where COMMIT arrived while the session
    /// was locked.
    /// </summary>
    Task RetryPendingAutoAcceptsAsync();
}
