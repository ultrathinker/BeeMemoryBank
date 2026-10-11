using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

// Swapping the live database file: pure file mechanics, no key. Shared by the restore flows of a full node and the reseed
// cutover of a blind node (BlindSeedService); split out of SnapshotService.Restore.cs, which is full-node only.
public partial class SnapshotService
{
    /// <summary>The longest a replacement of the live database waits for the connections in use to be returned (test seam).</summary>
    internal TimeSpan LiveSwapDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every replacement of the live vault database goes through here, plain node and encrypted node alike (review round 1, H3): the factory's file-switch
    /// barrier first (<see cref="DbConnectionFactory.BeginQuiesce"/>: from now on a connection that wants the file waits at the factory), then the wait for
    /// every connection still in use to be returned (<see cref="DbConnectionFactory.WaitDrainedAsync"/>, which closes the idle pooled handles too), then the
    /// swap (<see cref="SwapDbFileWithRetryAsync"/>), then the barrier is lifted and the next connection opens the new file. Without it a connection opened
    /// before the switch would go on reading, and writing, the file that was moved away (Linux), or keep the swap failing (Windows). A node whose
    /// connections cannot be drained in time, or which another flow is switching, is left exactly as it was.
    /// <para>
    /// It is not <see cref="BeeMemoryBank.Storage.Sealed.ISealableConnections.CloseForSealAsync"/>: that ends the key-bearing generation, and only a gate
    /// open starts the next one, while a restore keeps the node's key and the boot-store hooks that follow the swap read the new file through this factory.
    /// </para>
    /// </summary>
    private async Task ReplaceLiveDatabaseAsync(Action swap)
    {
        IDisposable barrier;
        try { barrier = _connFactory.BeginQuiesce(); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("The database is being switched by another operation of this node. Try again in a minute. Nothing was changed.", ex);
        }
        using (barrier)
        {
            try { await _connFactory.WaitDrainedAsync(LiveSwapDrainTimeout); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The database is still in use by other work of this node. Try again in a minute. Nothing was changed.");
            }
            await SwapDbFileWithRetryAsync(swap, Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools, _logger);
        }
    }

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
