using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Hosting;

/// <summary>
/// A background listener that monitors an input stream (by default, standard input) for end-of-stream (EOF)
/// and triggers a callback to perform a graceful shutdown.
/// </summary>
public sealed class StdinLifeline : IDisposable
{
    private readonly Action _callback;
    private readonly CancellationTokenSource _cts;
    private int _callbackTriggered;

    /// <summary>
    /// Gets the background reader task. Primarily useful for unit tests to await completion.
    /// </summary>
    public Task Completion { get; }

    private StdinLifeline(Action callback, TextReader reader)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _cts = new CancellationTokenSource();

        // Captured immediately (not re-read as `_cts.Token` inside the lambda below): the
        // lambda runs later, on a dedicated thread, and by then Dispose() may already have
        // disposed _cts - accessing _cts.Token at that point throws ObjectDisposedException.
        var token = _cts.Token;

        // Console.In.ReadLineAsync() on a redirected/piped stdin is not genuinely
        // asynchronous on Windows - it blocks a real OS thread waiting for data or EOF.
        // Running that on the default thread pool can starve it during process startup
        // (few worker threads exist yet), stalling unrelated async work - e.g. Kestrel
        // never finishing its bind, ASP.NET Core startup services never running - for as
        // long as the pool takes to inject a new thread. TaskCreationOptions.LongRunning
        // gives this loop its own dedicated thread instead of competing for pool threads.
        // CancellationToken.None here deliberately: RunLoopAsync already handles cancellation
        // internally (loop condition + catch), so the outer StartNew must not ALSO be
        // cancellable via the same token - otherwise disposing very quickly after Start()
        // can cancel this scheduling task before the dedicated thread ever runs the loop,
        // producing a canceled Completion instead of one that finishes normally.
        Completion = Task.Factory.StartNew(
            () => RunLoopAsync(reader, token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Starts a background listener on the specified <see cref="TextReader"/> (or <see cref="Console.In"/> if null).
    /// When EOF is detected, the <paramref name="callback"/> is invoked exactly once.
    /// </summary>
    /// <param name="callback">The callback to invoke when EOF is reached.</param>
    /// <param name="reader">The reader to monitor. Defaults to <see cref="Console.In"/>.</param>
    /// <returns>An instance of <see cref="StdinLifeline"/> that can be disposed to cancel the listener.</returns>
    public static StdinLifeline Start(Action callback, TextReader? reader = null)
    {
        return new StdinLifeline(callback, reader ?? Console.In);
    }

    private async Task RunLoopAsync(TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // ReadLineAsync will throw OperationCanceledException if token is cancelled
                // and returns null when EOF is encountered.
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line == null)
                {
                    TriggerCallback();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the cancellation token is triggered via Dispose.
            // Do not propagate or call callback.
        }
        catch (Exception)
        {
            // Fail-safe to avoid throwing unhandled exceptions in background threads.
        }
    }

    private void TriggerCallback()
    {
        if (Interlocked.CompareExchange(ref _callbackTriggered, 1, 0) == 0)
        {
            try
            {
                _callback();
            }
            catch (Exception)
            {
                // Prevent exception in callback from bubble-crashing the background task
            }
        }
    }

    /// <summary>
    /// Disposes the lifeline, cancelling the background task and preventing any future callback execution.
    /// </summary>
    public void Dispose()
    {
        // Mark callback as triggered to ensure it cannot run if EOF happens during/after disposal.
        Interlocked.Exchange(ref _callbackTriggered, 1);

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
