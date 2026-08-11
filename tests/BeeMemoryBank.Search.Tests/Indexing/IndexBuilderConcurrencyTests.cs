using System.Collections.Concurrent;
using BeeMemoryBank.Search.Indexing;

namespace BeeMemoryBank.Search.Tests.Indexing;

/// <summary>
/// Exercises the concurrency requirement from the WP-10 brief: a reader thread must be able to
/// safely enumerate <see cref="IndexBuilder.GetSealedSegments"/>'s current snapshot while a
/// background writer thread concurrently adds/updates/removes documents (forcing seals and merges),
/// without ever observing a torn/inconsistent state or throwing.
///
/// <para>
/// This is a stress test, not a proof, but the design it is checking is structural rather than
/// timing-dependent: <see cref="IndexBuilder"/> publishes the sealed-segment list via copy-on-write
/// (a merge always builds a brand-new list and swaps a single volatile field reference, never
/// mutating a previously-published list or segment in place). If that held, no amount of concurrent
/// reading should ever throw <c>InvalidOperationException</c> ("collection was modified") or see a
/// segment whose backing bytes changed after publication -- which is exactly what a naive "mutate a
/// shared <c>List&lt;T&gt;</c> in place" implementation would risk under this same stress pattern.
/// </para>
/// </summary>
public class IndexBuilderConcurrencyTests
{
    // Deterministic operation counts (not wall-clock sleeps) so the test's runtime and the amount
    // of churn it applies are stable across slow/fast machines -- important since the brief asks
    // for this test to be run several times back-to-back to check for flakiness.
    private const int WriterArticleCount = 60;
    private const int WriterRoundCount = 40; // 40 rounds x 60 articles = 2400 write operations.
    private const int ReaderThreadCount = 4;

    [Fact]
    public async Task ConcurrentReadersDuringMerges_NeverObserveTornStateOrThrow()
    {
        var builder = new IndexBuilder(hotBufferSealThreshold: 15, mergeSegmentCountThreshold: 3, mergeTombstoneFractionThreshold: 0.25);
        var articleIds = Enumerable.Range(0, WriterArticleCount).Select(_ => Guid.NewGuid()).ToArray();

        var stop = new CancellationTokenSource();
        var readerExceptions = new ConcurrentBag<Exception>();
        var readerIterations = new long[1]; // boxed shared counter, bumped via Interlocked from any reader thread.

        Task[] readers = Enumerable.Range(0, ReaderThreadCount)
            .Select(_ => Task.Run(() => ReaderLoop(builder, stop.Token, readerExceptions, readerIterations)))
            .ToArray();

        Exception? writerException = null;
        try
        {
            RunWriterChurn(builder, articleIds);
        }
        catch (Exception ex)
        {
            writerException = ex;
        }
        finally
        {
            stop.Cancel();
        }

        bool completedInTime = true;
        try
        {
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            completedInTime = false;
        }

        writerException.Should().BeNull("the writer thread (add/update/remove/seal/merge) must never throw");
        completedInTime.Should().BeTrue("reader threads must observe the cancellation and exit promptly, not hang");
        readerExceptions.Should().BeEmpty("no reader should ever see a torn/inconsistent segment-list snapshot or throw while enumerating one");

        // Sanity: the readers actually did meaningful work concurrently with the writer, so a
        // passing run means something (as opposed to the writer finishing before readers even
        // started their first iteration).
        Interlocked.Read(ref readerIterations[0]).Should().BeGreaterThan(0);

        // The writer's churn must actually have exercised sealing and merging for this test to be
        // testing what it claims to.
        builder.SealCount.Should().BeGreaterThan(0);
        builder.MergeCount.Should().BeGreaterThan(0);
    }

    private static void RunWriterChurn(IndexBuilder builder, Guid[] articleIds)
    {
        var random = new Random(20260811);

        for (int round = 0; round < WriterRoundCount; round++)
        {
            foreach (Guid articleId in articleIds)
            {
                double roll = random.NextDouble();
                if (roll < 0.2 && round > 0)
                {
                    builder.RemoveDocument(articleId);
                }
                else
                {
                    string body = $"round {round} article {articleId:N} alpha bravo charlie delta echo";
                    builder.AddOrUpdateDocument(articleId, Guid.NewGuid(), body);
                }
            }
        }
    }

    private static void ReaderLoop(IndexBuilder builder, CancellationToken token, ConcurrentBag<Exception> exceptions, long[] iterations)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                IReadOnlyList<SealedSegmentSnapshot> snapshot = builder.GetSealedSegments();

                // Fully enumerate and query every segment in this snapshot -- if the underlying
                // list or any segment in it were mutated in place by a concurrent merge, this is
                // where a torn read (an exception, or a doc table entry that does not correspond to
                // the segment's own document count) would surface.
                foreach (SealedSegmentSnapshot segment in snapshot)
                {
                    int docCount = segment.Reader.DocumentCount;
                    for (int docId = 0; docId < docCount; docId++)
                    {
                        (Guid articleId, Guid _) = segment.Reader.GetDocument(docId);
                        _ = segment.IsLive(articleId);
                    }

                    _ = segment.Reader.GetPostings("alpha").ToList();
                    _ = segment.TombstoneCount;
                }

                Interlocked.Increment(ref iterations[0]);
            }
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }
}
