using System.Collections.Concurrent;

namespace BeeMemoryBank.Sync.Search;

/// <summary>
/// Process-lifetime, in-memory-only state shared across every <see cref="PendingIndexProcessor"/>
/// cycle for coordinating the search index's unlock warm-start and full-rebuild lifecycle.
/// Deliberately holds no DB/session dependencies -- everything here is a plain flag or a mapping
/// that only makes sense for as long as this process has been running -- so it is safe to register
/// as a DI singleton even though the repositories/stores that actually do I/O are scoped.
/// </summary>
public sealed class SearchIndexRuntimeState
{
    // 0 = not yet attempted, 1 = attempted (either successfully warm-started from disk, or a full
    // rebuild was triggered instead). Guards every PendingIndexProcessor cycle from re-running the
    // whole "enumerate the manifest and try to load each segment" dance after the first cycle
    // already resolved it one way or the other -- mirrors
    // EmbeddingProjectionService.EnsureProjectionMatrixAsync's own "already initialized, return
    // immediately" idempotency check, just backed by an in-memory flag instead of a DB row because
    // there is no persisted "warm start already happened" state to check (IndexBuilder's adopted
    // segments ARE the state, and they live only in this process's memory).
    private int _warmStartAttempted;

    // IndexBuilder's own internal, process-lifetime-only SealedSegment.Id -> the Guid it was
    // persisted under via EncryptedSegmentStore. Populated when a segment is sealed-and-stored, or
    // adopted from disk during warm-start. Consulted when IndexBuilder reports a
    // SegmentTombstoneEvent against one of these internal ids, to know which on-disk segment's
    // durable tombstone row to write. A segment that later gets merged away simply stops appearing
    // in any future tombstone report (its internal id no longer names a live segment inside
    // IndexBuilder), so no separate cleanup of this map is needed for that case.
    private readonly ConcurrentDictionary<int, Guid> _persistedSegmentIds = new();

    // Guards against two overlapping "the persisted index is untrustworthy" triggers (e.g. two
    // segments both failing to load in the same warm-start pass) launching two redundant
    // concurrent full rebuilds. Deliberately not BeeMemoryBank.Api's HeavyOperationLock: that type
    // is internal to the Api assembly and not reachable from BeeMemoryBank.Sync, and per wp-11.md
    // this is a narrower, self-contained concern anyway (scoped to just this WP's own rebuild
    // trigger) that does not need to be unified with the Api-layer coordination primitive that
    // guards snapshot restore / DEK rotation.
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    /// <summary>
    /// Atomically claims the warm-start attempt for this process. Returns true exactly once (for
    /// the first caller); every subsequent call returns false without doing anything, so callers
    /// can invoke this unconditionally every cycle the same way
    /// EnsureProjectionMatrixAsync is invoked unconditionally every embedding-processing cycle.
    /// </summary>
    public bool TryBeginWarmStart() => Interlocked.CompareExchange(ref _warmStartAttempted, 1, 0) == 0;

    /// <summary>
    /// WP-18: read-only diagnostic accessor exposing whether the unlock warm-start has already been
    /// attempted this process lifetime (either it loaded persisted segments, or it fell back to a
    /// full rebuild). Lets the admin metrics surface distinguish "index not yet warm-started" from
    /// "warm-started / building from pending" without exposing any of the internal coordination
    /// state. Narrowly scoped: a boolean view of the same flag <see cref="TryBeginWarmStart"/>
    /// already guards -- no new mutable state.
    /// </summary>
    public bool IsWarmStartAttempted => Volatile.Read(ref _warmStartAttempted) != 0;

    public void RegisterPersistedSegment(int internalSegmentId, Guid persistedSegmentId) =>
        _persistedSegmentIds[internalSegmentId] = persistedSegmentId;

    public bool TryGetPersistedSegmentId(int internalSegmentId, out Guid persistedSegmentId) =>
        _persistedSegmentIds.TryGetValue(internalSegmentId, out persistedSegmentId);

    public void ClearPersistedSegmentIds() => _persistedSegmentIds.Clear();

    /// <summary>Coordination primitive for TriggerFullRebuildAsync -- see the field doc above.</summary>
    public SemaphoreSlim RebuildLock => _rebuildLock;
}
