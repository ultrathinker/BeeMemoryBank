using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Serializes every test class that reaches <c>HeavyOperationLock.Instance</c> — the single
/// process-wide semaphore shared by compaction, snapshot restore, network restore and DEK
/// rotation, because all four bulk-rewrite tbl_event and must never overlap.
///
/// Serialization is not optional here: <c>CompactionService.ExecuteAsync</c> takes the lock with
/// <c>WaitAsync(0)</c> and throws "Another compaction is already in progress" the instant it is
/// held, while restore takes the SAME lock and holds it for the length of a full restore. Two
/// unrelated test classes running in parallel are enough to fail the compaction one.
///
/// This used to be named for compaction alone, and the restore classes were left out of it on that
/// reading — which is exactly the flake it exists to prevent. If a test triggers restore,
/// compaction, or DEK rotation, it belongs in this collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HeavyOperationCollection
{
    public const string Name = "HeavyOperation";
}
