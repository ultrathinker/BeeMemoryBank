using System;
using System.IO;
using System.Threading;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// "Show your window", from a second start to the copy that is already running. The full app runs once per user (see the mutex in
/// <c>Program</c>); a second start used to end without a trace, so that a double-click on the Start-menu entry or the exe while the window
/// sat hidden in the tray showed nothing. Now the second start raises a named event, the first copy shows its window, and the second
/// ends. The event is the Windows way (named events do not exist in .NET on Unix); on the Mac a second launch of the app is the system's
/// "reopen" request to the running copy, handled by <see cref="ReopenHandler"/>.
/// </summary>
public sealed class ActivationSignal : IDisposable
{
    /// <summary>Named like the app's single-instance mutex (<c>BeeMemoryBank.Desktop.Mutex</c>): per user session, no <c>Global\</c>.</summary>
    public const string DefaultName = "BeeMemoryBank.Desktop.Activate";

    private readonly EventWaitHandle? _event;
    private RegisteredWaitHandle? _listener;
    private int _disposed;

    private ActivationSignal(EventWaitHandle? handle) => _event = handle;

    /// <summary>The signal of the first copy. Where named events do not exist (macOS, Linux) it is an inert object: nothing is listened for.</summary>
    public static ActivationSignal Create(string name = DefaultName)
    {
        if (!OperatingSystem.IsWindows()) return new ActivationSignal(null);
        try
        {
            return new ActivationSignal(new EventWaitHandle(false, EventResetMode.AutoReset, name));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return new ActivationSignal(null); // the app still runs; only the "show me" request of a second start is lost
        }
    }

    /// <summary>Asks the running copy to show its window. False when there is none to ask (or it cannot be reached).</summary>
    public static bool SignalRunningInstance(string name = DefaultName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a pool thread) each time a second start asks for the window; an exception in it is swallowed.</summary>
    public void Listen(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_event is null || _listener is not null) return;
        _listener = ThreadPool.RegisterWaitForSingleObject(_event, (_, timedOut) =>
        {
            if (timedOut) return;
            try { onActivate(); }
            catch (Exception) { /* a request to show the window must never end the app */ }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener?.Unregister(null);
        _event?.Dispose();
    }
}
