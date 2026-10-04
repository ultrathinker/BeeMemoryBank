using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

// Swapping the live database file: pure file mechanics, no key. Shared by the restore flows of a full node and the reseed
// cutover of a blind node (BlindSeedService); split out of SnapshotService.Restore.cs, which is full-node only.
public partial class SnapshotService
{
    /// <summary>
    /// Replaces the live database file, tolerating the transient Windows lock a pooled background
    /// reader can hold on it. Microsoft.Data.Sqlite keeps a connection's physical handle open in the
    /// pool after <c>Dispose</c>, so even an idle background reader (sync scheduler, search-index
    /// read, WAL checkpoint) keeps the file open — and <c>File.Move</c>/<c>File.Copy</c> over a file
    /// another handle has open throws <see cref="UnauthorizedAccessException"/> /
    /// <see cref="IOException"/>. <c>ClearAllPools</c> force-closes those handles, but a
    /// <c>CreateConnection</c> the instant after re-locks the file, so the swap has to land in the
    /// freed window: clear the pool and swap with nothing in between, and retry with backoff. In
    /// normal operation the next background open is seconds away, so a retry wins immediately; the
    /// old single-shot <c>ClearAllPools + 200&#160;ms delay + swap</c> lost that race ~1-in-N on a
    /// busy node and surfaced as a bare 500. Reproduced deterministically under concurrent DB load.
    /// <para>Seams (<paramref name="clearPools"/>, <paramref name="delay"/>) exist so the retry
    /// logic is unit-tested without real files or timing — see SnapshotFileSwapRetryTests.</para>
    /// </summary>
    internal static async Task SwapDbFileWithRetryAsync(
        Action swap,
        Action clearPools,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        int maxAttempts = 10,
        Func<int, Task>? delay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Clear immediately before the swap, with nothing between them, so File.Move lands in
            // the instant the file is free rather than after a re-lock window.
            clearPools();
            try
            {
                swap();
                // And again after it: a connection opened in the window between the clear above and
                // the swap (a request allowed through maintenance, a background reader) went back to
                // the pool still open on the file that was just moved away or deleted. Reused, it reads
                // the old database and fails every write with SQLITE_READONLY_DBMOVED — the "read-only
                // database" a reseed hit under load (review L-merge round 3 #3).
                clearPools();
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < maxAttempts)
            {
                logger?.LogWarning(ex,
                    "Database file swap blocked (file still held open); attempt {Attempt}/{Max}, retrying",
                    attempt, maxAttempts);
                await (delay?.Invoke(attempt) ?? Task.Delay(50 * attempt));
            }
        }
    }
}
