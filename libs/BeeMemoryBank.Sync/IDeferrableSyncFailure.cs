namespace BeeMemoryBank.Sync;

/// <summary>
/// Marker for an exception thrown by <see cref="EventApplier.ApplyAsync"/> whose cause is a
/// precondition this node does not (yet) hold — not something permanently wrong with the event
/// itself. See <see cref="BlobMissingException"/>, <see cref="OriginatorNotWhitelistedException"/>
/// and <see cref="DekRotationPredecessorMissingException"/> for the three cases named in the
/// original brief: a referenced blob not yet transported, a whitelist_add not yet delivered, and a
/// DEK rotation COMMIT arriving before its PROPOSED.
///
/// <para>
/// Implement this on the exception TYPE, never inferred from a message string or an existing BCL
/// exception type used for other reasons too (e.g. plain <see cref="UnauthorizedAccessException"/>
/// also covers "known peer, not superadmin", which is permanent). <see cref="SyncFailureClassifier"/>
/// is the one place that reads this marker — see its own remarks for why that matters.
/// </para>
/// </summary>
public interface IDeferrableSyncFailure;
