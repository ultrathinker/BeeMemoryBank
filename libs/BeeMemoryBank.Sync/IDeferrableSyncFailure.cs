namespace BeeMemoryBank.Sync;

/// <summary>
/// Marker for an exception thrown by <see cref="EventApplier.ApplyAsync"/> whose cause is a
/// precondition this node does not (yet) hold — not something permanently wrong with the event
/// itself. See <see cref="BlobMissingException"/>, <see cref="OriginatorNotWhitelistedException"/>,
/// <see cref="OriginatorNotSuperadminException"/> and <see cref="DekRotationPredecessorMissingException"/>
/// for the four current cases: a referenced blob not yet transported, a whitelist_add not yet
/// delivered, a superadmin promotion not yet delivered, and a DEK rotation COMMIT arriving before
/// its PROPOSED.
///
/// <para>
/// Implement this on the exception TYPE, never inferred from a message string or an existing BCL
/// exception type used for other reasons too (e.g. plain <see cref="UnauthorizedAccessException"/>
/// also covers "originator revoked", which is permanent). <see cref="SyncFailureClassifier"/>
/// is the one place that reads this marker — see its own remarks for why that matters.
/// </para>
/// </summary>
public interface IDeferrableSyncFailure;
