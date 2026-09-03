namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Process-wide single-flight semaphore for operations that bulk-rewrite tbl_event:
/// CompactionService.CompactAsync and SnapshotService.ApplyNetworkRestoreAsync /
/// RestoreAsync. Both delete and re-insert large ranges of the event log; running
/// them concurrently would corrupt the journal (compaction could remove rows that
/// restore just imported, restore could clobber a checkpoint compaction is mid-write).
/// SQLite's WAL serializes writers but not the higher-level invariants these flows
/// assume.
/// <para>
/// Lives in Core rather than the API project because DEK rotation takes it too, and the
/// rotation applier is shared with hosts that have no API layer (mobile, CLI). A lock that
/// only half the writers can see is not a lock.
/// </para>
/// </summary>
public static class HeavyOperationLock
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}
