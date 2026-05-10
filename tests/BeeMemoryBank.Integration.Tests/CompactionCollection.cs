using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Forces tests that exercise CompactionService — directly or transitively
/// (DEK rotation triggers a post-rotation compaction) — to run sequentially.
/// CompactionService has a process-wide guard against concurrent runs;
/// without serialization a background compaction from one test class can leak
/// into the next, producing "Another compaction is already in progress".
/// </summary>
[CollectionDefinition(Name)]
public sealed class CompactionCollection
{
    public const string Name = "Compaction";
}
