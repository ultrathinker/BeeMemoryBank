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

    /// <summary>
    /// Finishes a rotation this node started and could not finish at once (its new metadata key was not published yet, or the process
    /// ended after the DEK commit): the completion audit, the <c>Completed</c> step and the post-rotation compaction, from the durable
    /// record the rotation's own transaction wrote. Called after the publication has been retried at every unlock; the applier also
    /// repeats it by itself. Idempotent, never throws, does nothing where there is nothing to finish. A host that never starts a
    /// rotation has nothing to do.
    /// </summary>
    Task FinishPendingCompletionAsync() => Task.CompletedTask;
}
