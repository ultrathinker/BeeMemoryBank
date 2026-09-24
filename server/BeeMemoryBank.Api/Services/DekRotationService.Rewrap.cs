using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;

namespace BeeMemoryBank.Api.Services;

public partial class DekRotationService
{
    /// <summary>
    /// Shared destructive core for both initiator Accept and peer AutoAccept paths.
    ///
    /// <para>
    /// The rewrap itself lives in <see cref="DekRewrapper"/> under BeeMemoryBank.Sync. Every host
    /// must run exactly this code: a host that skips the rewrap stays on the retired DEK forever
    /// and cannot read anything that arrives after a peer rotates. Keeping a second copy of a
    /// routine that re-wraps every key in the vault was not an acceptable alternative, so the
    /// server delegates to exactly the same code every other host runs.
    /// </para>
    /// </summary>
    private async Task<(int agentsDeleted, int slotsDeleted, RewrapTally tally)> RewrapDestructiveCoreAsync(
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null,
        string? chainEncryptedNewDekB64 = null,
        string? chainIvB64 = null)
    {
        // chat.db lives outside the rewrap transaction. Its remaining legacy rows (sealed directly
        // under the master DEK) are moved onto the node chat key now, while the session still holds
        // the outgoing DEK; the chat key itself is then carried forward inside the transaction.
        // Mandatory: if anything is left unmoved this throws DekRotationPreconditionException and
        // the rotation stops here, before the transaction, with nothing changed.
        _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 18,
            isInitiator ? "Moving chat history onto the node chat key..." : "Auto-accept: moving chat history onto the node chat key...");
        using (var hookScope = _scopeFactory.CreateScope())
            await DekRewrapper.RunPreRewrapHooksAsync(hookScope.ServiceProvider, _logger);

        return await DekRewrapper.RewrapAllAsync(
            _connFactory, _sessionService,
            oldDek, newDek, newEpoch, commitEventId,
            isInitiator, initiatorSlotId, newWrappedSlotDek, newWrappedSlotIv,
            chainEncryptedNewDekB64, chainIvB64,
            progress: (step, pct, msg) =>
            {
                if (step == DekRotationFlowStep.Completed)
                {
                    // Not yet: the node is still in maintenance mode here and answers 503 to every
                    // request. A caller that acts on "Completed" must be able to use the node, so
                    // the terminal step is published by PublishCompleted once the outer accept path
                    // has left maintenance mode.
                    _pendingCompletedMessage = msg;
                    _progress.Update(DekRotationFlowStep.Finalizing, 95, "Rotation committed; leaving maintenance mode...");
                    return;
                }
                _progress.Update(step, pct, msg);
            });
    }

    // Completion message held back by RewrapDestructiveCoreAsync until maintenance mode is off.
    private volatile string? _pendingCompletedMessage;

    /// <summary>
    /// Publishes the terminal Completed step. Called by the outer accept paths only after
    /// <see cref="MaintenanceModeService.Exit"/>, so "Completed" is never observable while the node
    /// still answers 503.
    /// </summary>
    private void PublishCompleted()
    {
        var msg = _pendingCompletedMessage ?? "DEK rotation completed.";
        _pendingCompletedMessage = null;
        _progress.Update(DekRotationFlowStep.Completed, 100, msg);
        _progress.ClearError();
    }
}
