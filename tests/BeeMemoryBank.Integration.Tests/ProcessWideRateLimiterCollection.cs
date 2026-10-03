namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The API's rate limiter is process-wide (one static bucket per IP+path), and a few tests burn the whole bucket of
/// /api/join on purpose to see the 429. Run in parallel with the other classes that join through /api/join
/// without the internal key, that left them with a 429 at random (SnapshotJoinClientTests failed on the GitHub
/// runner of 1.0.15). A collection that disables parallelization runs alone, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideRateLimiterCollection
{
    public const string Name = "ProcessWideRateLimiter";
}
