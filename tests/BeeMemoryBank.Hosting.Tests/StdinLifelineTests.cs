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
}
