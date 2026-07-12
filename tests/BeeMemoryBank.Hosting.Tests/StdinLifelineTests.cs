using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Hosting;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Hosting.Tests;

public class StdinLifelineTests
{
    private class TestTextReader : TextReader
    {
        private readonly SemaphoreSlim _semaphore = new(0);
        private readonly Queue<string?> _lines = new();

        public void ProvideLine(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
            }
            _semaphore.Release();
        }

        public void ProvideEof()
        {
            lock (_lines)
            {
                _lines.Enqueue(null);
            }
            _semaphore.Release();
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken);
            lock (_lines)
            {
                return _lines.Dequeue();
            }
        }
    }

    [Fact]
    public async Task Start_WithImmediateEof_TriggersCallbackOnce()
    {
        // Arrange
        int callCount = 0;
        var reader = new StringReader(string.Empty);

        // Act
        using (var lifeline = StdinLifeline.Start(() => Interlocked.Increment(ref callCount), reader))
        {
            await lifeline.Completion;
        }

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task Start_BeforeEof_DoesNotTriggerCallback()
    {
        // Arrange
        int callCount = 0;
        var reader = new TestTextReader();

        // Act
        using var lifeline = StdinLifeline.Start(() => Interlocked.Increment(ref callCount), reader);

        // Provide a normal line (not EOF)
        reader.ProvideLine("keep alive line");

        // Yield execution to let background thread process the line
        await Task.Delay(50);

        // Assert
        callCount.Should().Be(0);
        lifeline.Completion.IsCompleted.Should().BeFalse();

        // Clean up: provide EOF to finish the loop
        reader.ProvideEof();
        await lifeline.Completion;

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task Start_EofThenDispose_TriggersCallbackOnlyOnce()
    {
        // Arrange
        int callCount = 0;
        var reader = new TestTextReader();

        // Act
        using (var lifeline = StdinLifeline.Start(() => Interlocked.Increment(ref callCount), reader))
        {
            reader.ProvideEof();
            await lifeline.Completion;

            callCount.Should().Be(1);

            // Dispose explicitly, should be a no-op for callback
            lifeline.Dispose();
        }

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_MultipleTimes_IsSafeAndDoesNotTriggerCallback()
    {
        // Arrange
        int callCount = 0;
        var reader = new TestTextReader();

        // Act
        var lifeline = StdinLifeline.Start(() => Interlocked.Increment(ref callCount), reader);

        lifeline.Dispose();
        lifeline.Dispose();
        lifeline.Dispose();

        // Assert
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_BeforeEof_DoesNotTriggerCallback()
    {
        // Arrange
        int callCount = 0;
        var reader = new TestTextReader();

        // Act
        using (var lifeline = StdinLifeline.Start(() => Interlocked.Increment(ref callCount), reader))
        {
            // Dispose before sending EOF
            lifeline.Dispose();

            // Background task should complete cleanly (OperationCanceledException is handled internally)
            await lifeline.Completion;
        }

        // Assert
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task CallbackThrows_DoesNotCrashBackgroundReader()
    {
        // Arrange
        var reader = new StringReader(string.Empty); // Immediate EOF
        bool callbackRan = false;

        // Act
        var lifeline = StdinLifeline.Start(() =>
        {
            callbackRan = true;
            throw new InvalidOperationException("Test exception in callback");
        }, reader);

        // Act & Assert
        // Awaiting Completion should not propagate the callback exception
        Func<Task> act = async () => await lifeline.Completion;
        await act.Should().NotThrowAsync();

        callbackRan.Should().BeTrue();
    }

    /// <summary>
    /// A reader whose ReadLineAsync blocks a REAL thread synchronously (via a manual-reset
    /// event) rather than truly suspending - simulating Console.In's behavior on a
    /// redirected/piped stdin on Windows, where the "async" read is effectively a blocking
    /// OS-level read wrapped in a Task.
    /// </summary>
    private class SyncBlockingReader : TextReader
    {
        private readonly ManualResetEventSlim _release = new(false);

        public void Release() => _release.Set();

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            // Blocks the CALLING thread for real - not a genuine suspension point.
            _release.Wait(cancellationToken);
            return new ValueTask<string?>((string?)null);
        }
    }

    [Fact]
    public async Task Start_WithBlockingReader_DoesNotStarveThreadPool()
    {
        // Regression test: a real production bug where StdinLifeline ran its read loop
        // directly on the default thread pool. Console.In.ReadLineAsync() on a
        // redirected/piped stdin is not genuinely async on Windows, so that loop could tie
        // up a scarce startup-time worker thread indefinitely, starving unrelated async
        // work scheduled around the same time (e.g. Kestrel/ASP.NET Core startup) until the
        // pool grew - which can take seconds and stalls the whole app in the meantime.
        // Reproduced directly against real Api.exe/Web.exe via manual Process.Start
        // (RedirectStandardInput=true, BMB_STDIN_LIFELINE=1): startup hung indefinitely
        // before the fix (TaskCreationOptions.LongRunning - a dedicated thread instead of a
        // pool thread) and completed in ~1-2s after.
        ThreadPool.GetMinThreads(out var originalWorkerMin, out var originalIoMin);
        try
        {
            // Force a minimal pool so a starved thread is actually observable quickly.
            ThreadPool.SetMinThreads(1, 1);

            var reader = new SyncBlockingReader();
            using var lifeline = StdinLifeline.Start(() => { }, reader);

            // Unrelated thread-pool work queued while the lifeline's blocking read is in
            // flight must still complete promptly - it must NOT be stuck behind the
            // lifeline's read waiting for the pool to grow a new thread.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => { });
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "unrelated thread-pool work must not be starved by the lifeline's blocking read");

            reader.Release();
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkerMin, originalIoMin);
        }
    }
}
