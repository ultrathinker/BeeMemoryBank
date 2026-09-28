using System.Collections.Concurrent;

namespace BeeMemoryBank.Crypto;

/// <summary>
/// "One heavy derivation at a time" (BMB-43, plan 6.6). A strong recovery box costs 512 MiB–1 GiB of
/// Argon2id memory — two or four times the whole shared budget in <see cref="KeyDerivation"/> on a
/// small machine — so it must never run next to another one, and must never take the shared budget
/// away from logins and joins. It runs here instead: on one dedicated low-priority background thread,
/// strictly FIFO, with a short queue.
///
/// <para>
/// <see cref="RecoveryBoxCrypto"/> refuses a heavy preset anywhere but on this worker, so the rule
/// cannot be bypassed by a caller that forgets. Callers hand work in and carry on; nothing that
/// answers an HTTP request waits for a result from here.
/// </para>
/// </summary>
public static class HeavyDerivationQueue
{
    private const int MaxQueued = 4;

    private static readonly BlockingCollection<Action> Work = new(new ConcurrentQueue<Action>());
    private static readonly Lazy<Thread> Worker = new(StartWorker, LazyThreadSafetyMode.ExecutionAndPublication);

    [ThreadStatic] private static bool _onWorker;

    /// <summary>True on the queue's own thread — the only place a heavy preset may be derived.</summary>
    internal static bool IsOnWorker => _onWorker;

    /// <summary>
    /// Queues <paramref name="work"/> and returns a task for its result. Throws
    /// <see cref="KdfBusyException"/> when the queue is already full, instead of letting a flood of
    /// requests hold gigabytes of pending derivations.
    /// </summary>
    /// <remarks>
    /// <b>Cancellation.</b> A queued item that has not started never starts. An item already running cannot
    /// be stopped — Konscious' Argon2 has no cancellation point — so on cancellation the returned task is
    /// cancelled AT ONCE (the caller, its locks and its UI are released) and the derivation finishes on this
    /// thread in the background, its result discarded and a key it produced wiped. The cost of that is
    /// bounded by one derivation: measured on a 4-core laptop CPU (i5-8350U, under test load)
    /// s1024t4 takes about 2.4 s and s512t6 about 1.3 s. A killable worker process per derivation
    /// would cost a process start and a second copy of the password in another process for a few seconds
    /// saved; not worth it.
    /// </remarks>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ = Worker.Value;

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Work)
        {
            if (Work.Count >= MaxQueued)
                throw new KdfBusyException();
            Work.Add(() =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try
                {
                    var result = work();
                    // Cancelled while running: nobody will read this; do not leave a key on the heap.
                    if (!tcs.TrySetResult(result) && result is byte[] key) Array.Clear(key);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        if (ct.CanBeCanceled)
            ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(() =>
        {
            _onWorker = true;
            foreach (var item in Work.GetConsumingEnumerable())
                item();
        })
        {
            IsBackground = true,
            // Economy mode by default (plan section 8): a strong box is never urgent, the
            // interactive work on the machine is.
            Priority = ThreadPriority.BelowNormal,
            Name = "bmb-heavy-kdf",
        };
        thread.Start();
        return thread;
    }
}
