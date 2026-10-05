using System;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>The IOKit notification port, as the monitor uses it. A seam: the message flow is tested on a fake, the real port only on a Mac.</summary>
internal interface IPowerNotificationSource
{
    /// <summary>
    /// Registers for system power messages and returns the registration. Called ON the monitor's own thread, because the run loop that
    /// delivers the messages is that thread's. <paramref name="onMessage"/> is called from <see cref="IPowerNotificationSession.Pump"/>.
    /// </summary>
    IPowerNotificationSession Open(Action<uint, IntPtr> onMessage);
}

internal interface IPowerNotificationSession : IDisposable
{
    /// <summary>IOAllowPowerChange: lets the system go on with the power change the message announced.</summary>
    void AllowPowerChange(IntPtr notificationId);

    /// <summary>Delivers messages (on this thread) until <paramref name="stopRequested"/> is true; looks at it at least twice a second.</summary>
    void Pump(Func<bool> stopRequested);

    /// <summary>From any thread: makes <see cref="Pump"/> look at its stop flag now.</summary>
    void Wake();
}

/// <summary>
/// Lock the vault when the Mac goes to sleep. <c>IORegisterForSystemPower</c> on a thread of its own that runs a CFRunLoop; on
/// <c>kIOMessageSystemWillSleep</c> it asks the node to lock (the shell's own request, the same one the Windows monitor makes), waits for
/// it for at most <see cref="DefaultLockTimeout"/>, shows a notice (a warning when the lock did not happen), and then - always, exactly
/// once, whatever the request did - calls <c>IOAllowPowerChange</c>. Exactly an HTTP 501 answer ("this node does not offer lock-on-sleep
/// yet", an older node) is only logged, with no notice, and so is a result flagged <see cref="SleepLockResult.LogOnly"/> (the open node is
/// one this app did not start, so it holds no key to lock it); every other failure, a refused key (401/403) included, is logged and shown.
/// It never holds the Mac awake for more
/// than that timeout (the system itself gives up on an application after 30 seconds). The idle-sleep question (<c>CanSystemSleep</c>) is
/// always answered "yes": this monitor never vetoes sleep. Disposing removes the registration and ends the thread.
///
/// <para>Nothing here can be tried by putting the Mac to sleep from a test run, so the message flow is proved on a fake port and the real
/// port is only registered and unregistered. A real sleep/wake needs the owner (see the report).</para>
/// </summary>
public sealed class MacOsSleepMonitor : IPowerEventsService
{
    /// <summary>How long a sleep may wait for the node's answer to the lock request.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(4);

    private const int NotImplementedStatus = 501;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SleepLockRequest _lockNode;
    private readonly IUserNotifier _notifier;
    private readonly IPowerNotificationSource _source;
    private readonly TimeSpan _lockTimeout;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private IPowerNotificationSession? _session;
    private volatile bool _stopRequested;
    private bool _disposed;
    private bool _registered;

    public MacOsSleepMonitor(SleepLockRequest lockNode, IUserNotifier notifier)
        : this(lockNode, notifier, new IoKitPowerNotificationSource(), DefaultLockTimeout, Console.Error.WriteLine) { }

    internal MacOsSleepMonitor(SleepLockRequest lockNode, IUserNotifier notifier, IPowerNotificationSource source, TimeSpan lockTimeout, Action<string> log)
    {
        _lockNode = lockNode ?? throw new ArgumentNullException(nameof(lockNode));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _source = source;
        _lockTimeout = lockTimeout;
        _log = log;
    }

    /// <summary>True from the moment the registration is in place until the monitor is stopped.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _registered && _thread is { IsAlive: true }; }
    }

    /// <summary>The thread is gone (never started counts as gone). For the tests that check that nothing is leaked.</summary>
    internal bool ThreadHasExited
    {
        get { lock (_gate) return _thread is null || !_thread.IsAlive; }
    }

    /// <summary>
    /// Starts the monitor and returns once the registration has worked or failed. A failure is logged and shown as a warning (the vault
    /// would not be locked on sleep) and leaves <see cref="IsRunning"/> false; it does not throw.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "BmbSleepMonitor" };
            _thread.Start();
        }
        if (!_ready.Wait(StartTimeout)) _log("[MacOsSleepMonitor] The registration for sleep messages did not answer in time.");
    }

    private void Run()
    {
        IPowerNotificationSession? session = null;
        try
        {
            session = _source.Open(OnMessage);
            lock (_gate)
            {
                _session = session;
                _registered = true;
            }
            _ready.Set();
            session.Pump(() => _stopRequested);
        }
        catch (Exception ex)
        {
            _log($"[MacOsSleepMonitor] Could not watch for sleep: {ex.Message}");
            if (!_stopRequested)
                SafeNotify("Bee Memory Bank", "The vault will NOT be locked when this Mac sleeps: the sleep notification could not be set up (" + ex.Message + ").");
        }
        finally
        {
            lock (_gate)
            {
                _registered = false;
                _session = null;
            }
            try { session?.Dispose(); }
            catch (Exception ex) { _log($"[MacOsSleepMonitor] Could not close the sleep registration: {ex.Message}"); }
            _ready.Set();
        }
    }

    /// <summary>Called by the port on the monitor's thread. Nothing may escape from here: it is a native callback.</summary>
    private void OnMessage(uint messageType, IntPtr argument)
    {
        try
        {
            switch (messageType)
            {
                case IOKitPower.MessageCanSystemSleep:
                    // The system asks whether it may go to sleep on its own (idle). Never veto; not answering would only delay it.
                    Allow(argument);
                    break;

                case IOKitPower.MessageSystemWillSleep:
                    try { LockForSleep(); }
                    catch (Exception ex) { _log($"[MacOsSleepMonitor] The sleep handler failed: {ex.Message}"); }
                    finally { Allow(argument); }
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"[MacOsSleepMonitor] Error while handling power message 0x{messageType:X}: {ex.Message}");
        }
    }

    private void Allow(IntPtr argument)
    {
        try
        {
            IPowerNotificationSession? session;
            lock (_gate) session = _session;
            if (session is null) _log("[MacOsSleepMonitor] A power message arrived with no registration to answer on.");
            session?.AllowPowerChange(argument);
        }
        catch (Exception ex)
        {
            _log($"[MacOsSleepMonitor] IOAllowPowerChange failed: {ex.Message}");
        }
    }

    private void LockForSleep()
    {
        string? problem = null;
        string? logOnly = null;
        var notImplemented = false;
        var locked = false;
        try
        {
            // Not disposed: a request that outlives the timeout may still look at its token, and the source owns nothing that needs disposing.
            var cts = new CancellationTokenSource();
            var request = Task.Run(() => _lockNode(cts.Token));
            if (request.Wait(_lockTimeout))
            {
                var result = request.Result;
                // Exactly HTTP 501 means "this node does not offer lock-on-sleep (yet)": not a failure to report on every sleep. Any other
                // answer or error (4xx, 500, 502, 503, a timeout, a refused connection) is a real problem and is still shown.
                if (!result.Succeeded && result.HttpStatus == NotImplementedStatus) notImplemented = true;
                // A node this app did not start and holds no key for: the vault stays as it was, nothing the person can do at the moment of sleep.
                else if (!result.Succeeded && result.LogOnly) logOnly = result.Detail ?? "The node was not locked.";
                else if (!result.Succeeded) problem = result.Detail ?? "The node did not lock.";
                // A success without a detail is a vault that was locked; a success WITH one is "no node is open, nothing to lock".
                else locked = result.Detail is null;
            }
            else
            {
                cts.Cancel();
                problem = $"The node did not answer the lock request within {_lockTimeout.TotalSeconds:0.#} seconds.";
            }
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
            problem = inner.Message;
        }

        if (notImplemented)
        {
            // One line in the log, no banner: until the node implements the lock request, every sleep would otherwise show a warning
            // about something the person cannot act on.
            _log("[MacOsSleepMonitor] The node answered 501 to the lock request: it does not offer lock-on-sleep yet, so nothing was locked and no notice is shown.");
        }
        else if (logOnly is not null)
        {
            _log($"[MacOsSleepMonitor] The vault was not locked for sleep, and no notice is shown: {logOnly}");
        }
        else if (problem is null)
        {
            SafeNotify("Bee Memory Bank", locked
                ? "This Mac is about to sleep. The vault was locked, and the Bee Memory Bank node will be unreachable until it wakes."
                : "This Mac is about to sleep. The Bee Memory Bank node will be unreachable until it wakes.");
        }
        else
        {
            _log($"[MacOsSleepMonitor] The vault was not locked for sleep: {problem}");
            SafeNotify("Bee Memory Bank warning", "This Mac is about to sleep and the vault could not be locked first: " + problem
                + " The node will be unreachable until the Mac wakes.");
        }
    }

    private void SafeNotify(string title, string message)
    {
        try { _notifier.Notify(title, message); }
        catch (Exception ex) { _log($"[MacOsSleepMonitor] The notice could not be shown: {ex.Message}"); }
    }

    public void Dispose()
    {
        Thread? thread;
        IPowerNotificationSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopRequested = true;
            thread = _thread;
            session = _session;
        }

        try { session?.Wake(); }
        catch (Exception ex) { _log($"[MacOsSleepMonitor] Could not wake the run loop: {ex.Message}"); }

        if (thread is { IsAlive: true } && !thread.Join(StopTimeout))
            _log("[MacOsSleepMonitor] The monitor thread did not stop in time.");
    }
}

/// <summary>The real IOKit port: <c>IORegisterForSystemPower</c>, a notification port whose source is on the calling thread's run loop.</summary>
internal sealed class IoKitPowerNotificationSource : IPowerNotificationSource
{
    public IPowerNotificationSession Open(Action<uint, IntPtr> onMessage)
    {
        NativeLibraries.RequireMacOS();
        return new Session(onMessage);
    }

    private sealed class Session : IPowerNotificationSession
    {
        // Kept in a field: the native side only holds a function pointer, so the delegate must stay reachable as long as it can be called.
        private readonly IOKitPower.ServiceInterestCallback _callback;
        private readonly object _gate = new();
        private IntPtr _port;
        private uint _rootPort;
        private uint _notifier;
        private IntPtr _runLoop;
        private IntPtr _source;
        private bool _disposed;

        public Session(Action<uint, IntPtr> onMessage)
        {
            // Nothing may escape from a native callback (an unhandled exception there ends the process).
            _callback = (_, _, messageType, argument) =>
            {
                try { onMessage(messageType, argument); }
                catch (Exception) { }
            };

            _rootPort = IOKitPower.IORegisterForSystemPower(IntPtr.Zero, out _port, _callback, out _notifier);
            if (_rootPort == 0)
                throw new InvalidOperationException("IORegisterForSystemPower did not return a connection to the power manager.");

            try
            {
                _source = IOKitPower.IONotificationPortGetRunLoopSource(_port);
                if (_source == IntPtr.Zero) throw new InvalidOperationException("The power notification port has no run loop source.");
                _runLoop = RunLoop.CFRunLoopGetCurrent();
                RunLoop.CFRunLoopAddSource(_runLoop, _source, RunLoop.CommonModes);
            }
            catch
            {
                Close(removeSource: false);
                throw;
            }
        }

        public void AllowPowerChange(IntPtr notificationId)
        {
            var status = IOKitPower.IOAllowPowerChange(_rootPort, notificationId);
            if (status != IOKitPower.Success) throw new InvalidOperationException($"IOAllowPowerChange returned 0x{status:X}.");
        }

        public void Pump(Func<bool> stopRequested)
        {
            // A bounded turn instead of "run until CFRunLoopStop": a stop request that arrives before the loop is running would be lost.
            while (!stopRequested())
                RunLoop.CFRunLoopRunInMode(RunLoop.DefaultMode, 0.5, returnAfterSourceHandled: false);
        }

        public void Wake()
        {
            lock (_gate)
            {
                // After Dispose the run loop may be gone with its thread; never touch it then.
                if (!_disposed && _runLoop != IntPtr.Zero) RunLoop.CFRunLoopStop(_runLoop);
            }
        }

        public void Dispose() => Close(removeSource: true);

        private void Close(bool removeSource)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            // The order of Apple's own sample: remove the source, deregister, close the root domain connection, destroy the port.
            if (removeSource && _runLoop != IntPtr.Zero && _source != IntPtr.Zero)
                RunLoop.CFRunLoopRemoveSource(_runLoop, _source, RunLoop.CommonModes);
            if (_notifier != 0) IOKitPower.IODeregisterForSystemPower(ref _notifier);
            if (_rootPort != 0) IOKitPower.IOServiceClose(_rootPort);
            if (_port != IntPtr.Zero) IOKitPower.IONotificationPortDestroy(_port);
            _rootPort = 0;
            _port = IntPtr.Zero;
            GC.KeepAlive(_callback);
        }
    }
}
