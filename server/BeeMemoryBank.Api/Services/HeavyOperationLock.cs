namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Process-wide single-flight semaphore for operations that bulk-rewrite tbl_event:
/// CompactionService.CompactAsync and SnapshotService.ApplyNetworkRestoreAsync /
/// RestoreAsync. Both delete and re-insert large ranges of the event log; running
/// them concurrently would corrupt the journal (compaction could remove rows that
/// restore just imported, restore could clobber a checkpoint compaction is mid-write).
/// SQLite's WAL serializes writers but not the higher-level invariants these flows
/// assume.
/// </summary>
internal static class HeavyOperationLock
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}
