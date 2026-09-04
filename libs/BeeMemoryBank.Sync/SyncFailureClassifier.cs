using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The single place an apply failure's exception is turned into "permanent" (quarantine after a
/// handful of tries, see <see cref="SyncEventQuarantine.QuarantineThreshold"/>) or "deferred"
/// (retry for hours — <see cref="SyncEventQuarantine.DeferredQuarantineBudget"/> — before it too
/// is given up on).
///
/// <para>
/// Mirrors <see cref="ConflictResolver"/>'s shape and reason: this rule used to have exactly one
/// call site (<see cref="SyncClient"/>'s pull loop), which was fine until the DEK-rotation and
/// blob-transport paths each needed the same "is this worth retrying" answer too — three
/// hand-copied versions of one rule drift the moment someone fixes only one of them. Everything
/// funnels through here instead: an exception opts in by implementing
/// <see cref="IDeferrableSyncFailure"/>, and anything that hasn't is Permanent — the safe default
/// for an exception type nobody has reasoned about yet, matching today's behavior for it.
/// </para>
/// </summary>
public static class SyncFailureClassifier
{
    public static SyncFailureKind Classify(Exception ex) =>
        ex is IDeferrableSyncFailure ? SyncFailureKind.Deferred : SyncFailureKind.Permanent;
}
