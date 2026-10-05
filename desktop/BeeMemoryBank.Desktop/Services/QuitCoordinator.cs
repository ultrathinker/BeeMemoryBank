using System;
using System.Threading;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Every way out of the app goes through the same graceful stop of the node. The tray menu's Quit and the macOS application menu's Quit
/// (Cmd+Q, the Dock menu's Quit) both end here: <c>MainWindow.RealClose()</c> (which closes the node's stdin so that it shuts down and
/// closes the database) first, <c>desktop.Shutdown()</c> after. The system's own Quit does not ask the shell: it asks the application
/// lifetime to shut down, and without this class that would end the shell while the node still runs. Closing the window is not a way
/// out; it only hides the window.
///
/// <para>Idempotent: the node is stopped once and the shutdown is requested once, however many times and in whatever order these
/// are called (Quit twice, Cmd+Q after Quit, Quit from inside the lifetime's own shutdown request). A stop that fails does not keep the
/// app open: the shell goes down anyway, and the node follows (its stdin lifeline sees the shell's end).</para>
///
/// <para>Pure logic with no UI types, so the routing is tested with fakes; what the real window and menu do with it can only be seen
/// in a visible session.</para>
/// </summary>
public sealed class QuitCoordinator
{
    private readonly Action _stopNode;
    private readonly Action _shutdown;
    private readonly Action<string> _log;
    private readonly object _stopGate = new();
    private int _nodeStopRequested;
    private int _shutdownRequested;

    /// <param name="stopNode">The graceful stop of the node (<c>MainWindow.RealClose</c>). Runs on the UI thread, once.</param>
    /// <param name="shutdown">Ends the application (<c>desktop.Shutdown()</c>). Runs once.</param>
    public QuitCoordinator(Action stopNode, Action shutdown, Action<string>? log = null)
    {
        _stopNode = stopNode ?? throw new ArgumentNullException(nameof(stopNode));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _log = log ?? (message => Console.Error.WriteLine(message));
    }

    /// <summary>The node's graceful stop has been started (it runs once).</summary>
    public bool NodeStopStarted => Volatile.Read(ref _nodeStopRequested) != 0;

    /// <summary>The application's shutdown has been requested (it is requested once).</summary>
    public bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>The tray menu's Quit and the application menu's Quit item: stop the node, then end the app.</summary>
    public void Quit()
    {
        StopNode();
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        try { _shutdown(); }
        catch (Exception ex) { _log($"[QuitCoordinator] The shutdown failed: {ex.Message}"); }
    }

    /// <summary>
    /// The application lifetime's own request to shut down (Cmd+Q, the Dock menu's Quit, a logout): the node is stopped first, and the
    /// shutdown goes on - this never cancels it, so the system's Quit and a logout are not held up. A request raised by this class's own
    /// <see cref="Quit"/> finds the node already stopped and does nothing.
    /// </summary>
    public void OnShutdownRequested() => StopNode();

    private void StopNode()
    {
        // A lock, not just a flag: a second caller on another thread waits until the stop has finished instead of going on to shut down
        // while the node still runs. The same thread coming back in (the stop closes a window, the lifetime reacts, the reaction asks for a
        // stop) finds the flag set and returns at once.
        lock (_stopGate)
        {
            if (_nodeStopRequested != 0) return;
            _nodeStopRequested = 1;
            try { _stopNode(); }
            catch (Exception ex) { _log($"[QuitCoordinator] The node's graceful stop failed: {ex.Message}"); }
        }
    }
}
