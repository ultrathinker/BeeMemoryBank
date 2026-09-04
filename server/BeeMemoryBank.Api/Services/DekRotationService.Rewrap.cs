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
    /// The rewrap itself lives in <see cref="DekRewrapper"/> under BeeMemoryBank.Sync. It was
    /// API-private until a review found that mobile and CLI nodes therefore fell back to a no-op
    /// applier — they logged a warning, stayed on the retired DEK forever, and could not read
    /// anything that arrived after a peer rotated. Keeping a second copy of a routine that
    /// re-wraps every key in the vault was not an acceptable alternative, so the server delegates
    /// to exactly the same code every other host now runs.
    /// </para>
    /// </summary>
    private Task<(int agentsDeleted, int slotsDeleted)> RewrapDestructiveCoreAsync(
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null,
        string? chainEncryptedNewDekB64 = null,
        string? chainIvB64 = null)
    {
        return DekRewrapper.RewrapAllAsync(
            _connFactory, _sessionService,
            oldDek, newDek, newEpoch, commitEventId,
            isInitiator, initiatorSlotId, newWrappedSlotDek, newWrappedSlotIv,
            chainEncryptedNewDekB64, chainIvB64,
            progress: (step, pct, msg) =>
            {
                _progress.Update(step, pct, msg);
                if (step == DekRotationFlowStep.Completed) _progress.ClearError();
            });
    }
}
