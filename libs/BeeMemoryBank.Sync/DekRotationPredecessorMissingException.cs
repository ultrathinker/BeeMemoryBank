namespace BeeMemoryBank.Sync;

/// <summary>
/// A DEK_ROTATION_COMMIT named a ProposedEventId this node has no matching PROPOSED row for yet.
/// Distinct from a generic <see cref="InvalidOperationException"/> so the sync layer can tell
/// "the PROPOSED just has not been delivered yet" — the originator issues propose-then-commit in
/// order, so PROPOSED is normally already applied or in the same pull page, and a COMMIT that
/// still outruns it will verify fine once PROPOSED lands — from a forged COMMIT referencing an
/// id that was never proposed at all, which no amount of waiting fixes.
/// </summary>
public sealed class DekRotationPredecessorMissingException(string proposedEventId)
    : InvalidOperationException(
        $"DEK_ROTATION_COMMIT references missing ProposedEventId {proposedEventId}; deferring until PROPOSED is delivered.")
    , IDeferrableSyncFailure
{
    public string ProposedEventId { get; } = proposedEventId;
}
