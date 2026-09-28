using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// <see cref="IDekRotationApplier"/> of a blind node (plan 3.4). There is nothing to re-wrap: the
/// node holds no DEK, and every row it stores stays sealed under whichever key sealed it. What it
/// keeps from a rotation is what EventApplier already stored — the commit in the log (relayed to
/// others) and the recovery material the recovery events carry — so this only closes the local
/// rotation state row, which would otherwise wait in Committing for an accept that never comes.
/// </summary>
public sealed class BlindDekRotationApplier(
    IDekRotationStateRepository stateRepo,
    ILogger<BlindDekRotationApplier> logger) : IDekRotationApplier
{
    /// <summary>
    /// Nobody sits at a blind node to accept a rotation, and the per-peer auto-accept flag defaults
    /// to off: gated on it, every commit would stay Committing for good.
    /// </summary>
    public bool RequiresApproval => false;

    public async Task AutoAcceptCommitAsync(SyncEvent commitEvent)
    {
        await stateRepo.UpdateStateAsync(commitEvent.EventId.ToString(), DekRotationState.Applied);
        logger.LogInformation("DEK rotation {EventId} recorded on blind node; nothing to re-wrap", commitEvent.EventId);
    }

    /// <summary>
    /// Closes every rotation left in Committing (called at startup, see BlindRoleStartup): a build
    /// that closed them in a background task could lose that task to a crash after the commit was
    /// already in the log, and nothing else would ever close the row.
    /// </summary>
    public async Task RetryPendingAutoAcceptsAsync()
    {
        foreach (var row in await stateRepo.GetByStateAsync(DekRotationState.Committing))
        {
            await stateRepo.UpdateStateAsync(row.EventId, DekRotationState.Applied);
            logger.LogWarning("DEK rotation {EventId} was left in Committing; recorded as applied now", row.EventId);
        }
    }
}
