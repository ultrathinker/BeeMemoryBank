using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Direct tests for the per-article write lock.
///
/// <para>
/// These exist because the service-level concurrency tests cannot reproduce the race they guard
/// against: the in-memory SQLite harness serializes writes on its own and every await completes
/// synchronously, so the read-modify-write window never actually opens there. Those tests pin the
/// BEHAVIOR of append/prepend/replace; these pin the mutual exclusion the behavior depends on
/// under real concurrency, and they fail if the lock stops locking.
/// </para>
/// </summary>
public class ArticleWriteLockTests
{
    [Fact]
    public async Task OnlyOneHolderAtATimeForTheSameArticle()
    {
        var id = Guid.NewGuid();
        int inside = 0, maxObserved = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            using var handle = await ArticleWriteLock.AcquireAsync(id);
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxObserved, now);
            await Task.Delay(2);   // a real await inside the critical section
            Interlocked.Decrement(ref inside);
        }));

        maxObserved.Should().Be(1, "the lock must admit one writer per article at a time");
    }

    [Fact]
    public async Task DifferentArticlesDoNotBlockEachOther()
    {
        var a = await ArticleWriteLock.AcquireAsync(Guid.NewGuid());
        try
        {
            // Would hang on a global lock rather than a per-article one.
            var other = ArticleWriteLock.AcquireAsync(Guid.NewGuid());
            var finished = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));
            finished.Should().BeSameAs(other, "a write to one article must not wait on another");
            (await other).Dispose();
        }
        finally
        {
            a.Dispose();
        }
    }

    [Fact]
    public async Task ReleasingLetsTheNextWaiterIn()
    {
        var id = Guid.NewGuid();
        var first = await ArticleWriteLock.AcquireAsync(id);

        var second = ArticleWriteLock.AcquireAsync(id);
        second.IsCompleted.Should().BeFalse("the article is held");

        first.Dispose();

        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(second);
        (await second).Dispose();
    }

    [Fact]
    public async Task DoubleDisposeReleasesOnlyOnce()
    {
        var id = Guid.NewGuid();
        var handle = await ArticleWriteLock.AcquireAsync(id);
        handle.Dispose();
        handle.Dispose();   // must not hand out a second permit

        var a = await ArticleWriteLock.AcquireAsync(id);
        var b = ArticleWriteLock.AcquireAsync(id);
        b.IsCompleted.Should().BeFalse("a double dispose must not inflate the semaphore count");

        a.Dispose();
        (await b).Dispose();
    }

    [Fact]
    public async Task ACancelledWaiterDoesNotStrandTheArticle()
    {
        var id = Guid.NewGuid();
        var held = await ArticleWriteLock.AcquireAsync(id);

        using var cts = new CancellationTokenSource();
        var blocked = ArticleWriteLock.AcquireAsync(id, cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => blocked).Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();

        // The entry must be usable again — a cancelled waiter that failed to decrement its
        // registration would leak the entry, and one that released the semaphore it never took
        // would corrupt the count.
        var next = ArticleWriteLock.AcquireAsync(id);
        var finished = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(next);
        (await next).Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}
