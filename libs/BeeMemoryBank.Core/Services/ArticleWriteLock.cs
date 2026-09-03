using System.Collections.Concurrent;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Process-wide per-article write lock.
///
/// <para>
/// Article writes are read-modify-write at several layers: the MCP append/prepend/replace tools
/// fetch the body, mutate it in memory and save it back, and <see cref="ArticleService"/>'s own
/// update allocates the next version number by reading the current maximum. Neither was serialized,
/// so two concurrent writers — two agents, or an agent racing a human's web edit — could both read
/// the same body and have the second save silently discard the first's change. The version-number
/// read had a sharper edge: the unique index on (article_id, version_number) makes the loser throw
/// AFTER its metadata UPDATE has already committed, leaving a torn article (bumped timestamp, old
/// body, no event).
/// </para>
///
/// <para>
/// Static rather than injected because <see cref="ArticleService"/> is scoped and constructed
/// directly by tests: a per-instance lock would serialize nothing. Article ids are GUIDs, so keys
/// never collide across vaults on a multi-profile host.
/// </para>
///
/// <para>
/// This lock is what makes atomicity possible, not a substitute for it: it serializes concurrent
/// writers to the same article to exactly one at a time, so the one writer holding it can safely
/// open a single SQLite transaction spanning metadata, version snapshot, body and event log
/// (<see cref="ArticleService"/>'s <c>UpdateCoreAsync</c>/<c>CreateAsync</c>/<c>DeleteAsync</c>)
/// without another in-process writer interleaving mid-transaction. Crash safety itself — all of
/// those writes landing together or not at all — comes from that shared transaction, not from
/// this lock.
/// </para>
/// </summary>
public static class ArticleWriteLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Waiters;
    }

    private static readonly ConcurrentDictionary<Guid, Entry> Locks = new();

    /// <summary>
    /// Acquires the lock for <paramref name="articleId"/>. Dispose the returned handle to release.
    /// Callers must not re-enter for the same id — the lock is not reentrant, which is why the
    /// read-modify-write operations live on ArticleService next to the update they wrap rather
    /// than in callers that would then call a locking update.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(Guid articleId, CancellationToken ct = default)
    {
        Entry entry;
        // Register interest before awaiting so the release path cannot evict an entry someone is
        // about to wait on — without the waiter count, two writers could end up on two different
        // semaphores for the same article and both proceed.
        lock (Locks)
        {
            entry = Locks.GetOrAdd(articleId, _ => new Entry());
            entry.Waiters++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(articleId, entry, signal: false);
            throw;
        }

        return new Releaser(articleId, entry);
    }

    private static void Release(Guid articleId, Entry entry, bool signal)
    {
        if (signal) entry.Semaphore.Release();
        lock (Locks)
        {
            if (--entry.Waiters == 0)
                Locks.TryRemove(articleId, out _);
        }
    }

    private sealed class Releaser(Guid articleId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release(articleId, entry, signal: true);
        }
    }
}
