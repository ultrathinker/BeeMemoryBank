using BeeMemoryBank.BlindDesktop.MacOS.Interop;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>What Network.framework says about the network path in use now (<c>nw_path_t</c>).</summary>
/// <param name="Satisfied">The path can carry traffic (<c>nw_path_status_satisfied</c>); false for no network, or one that would need a connection first.</param>
/// <param name="Expensive">The interface is expensive (a cellular hotspot, a tethered phone): <c>nw_path_is_expensive</c>.</param>
/// <param name="Constrained">The system is in Low Data Mode for this network: <c>nw_path_is_constrained</c>.</param>
public readonly record struct MacOsNetworkPath(bool Satisfied, bool Expensive, bool Constrained);

/// <summary>Where <see cref="MacOsDeviceConditions"/> reads macOS from; a test answers by hand.</summary>
public interface IMacOsConditionsSource
{
    /// <summary>The path in use now, or null when the system has not said yet (the monitor is starting) or cannot be asked.</summary>
    MacOsNetworkPath? ReadNetworkPath();

    /// <summary>Whether Low Power Mode is on, or null when this system cannot say (it is older than macOS 12, or the call failed).</summary>
    bool? ReadLowPowerMode();
}

/// <summary>
/// The two answers the account switches ask of macOS (<see cref="IBlindDeviceConditions"/>): is the network in use METERED, and is the computer SAVING POWER.
///
/// <para><b>Metered</b>: the path monitor of Network.framework (<c>nw_path_monitor</c>) reports the path in use; a path that is <i>expensive</i> or <i>constrained</i> (Low Data Mode) is
/// metered, any other usable path is not. No usable path, or no report yet, is "cannot tell" (null): an account with the switch on waits, it never reads unknown as free.
/// Ethernet and ordinary Wi-Fi are neither expensive nor constrained, so they are allowed.</para>
///
/// <para><b>Power saving</b>: <c>NSProcessInfo.processInfo.isLowPowerModeEnabled</c> (macOS 12 and later; older systems answer "cannot tell").</para>
///
/// <para>macOS is asked at most every 2 seconds, and a source that throws answers "cannot tell": it never breaks the sync loop.</para>
/// </summary>
public sealed class MacOsDeviceConditions : IBlindDeviceConditions
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly IMacOsConditionsSource _source;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private (DateTimeOffset At, bool? Metered, bool? PowerSaving)? _last;

    /// <summary>The real thing; nothing native is touched until the first answer is asked for.</summary>
    public MacOsDeviceConditions() : this(new SystemMacOsConditionsSource())
    {
    }

    public MacOsDeviceConditions(IMacOsConditionsSource source, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    public bool? IsMetered => Read().Metered;

    public bool? IsPowerSaving => Read().PowerSaving;

    /// <summary>The mapping of a network path to "metered": null when it cannot be told.</summary>
    public static bool? Metered(MacOsNetworkPath? path)
    {
        if (path is not { } known || !known.Satisfied) return null;
        return known.Expensive || known.Constrained;
    }

    private (bool? Metered, bool? PowerSaving) Read()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_last is { } last && now - last.At < CacheFor && now >= last.At) return (last.Metered, last.PowerSaving);
            var metered = Safely(() => Metered(_source.ReadNetworkPath()));
            var power = Safely(() => _source.ReadLowPowerMode());
            _last = (now, metered, power);
            return (metered, power);
        }
    }

    private static bool? Safely(Func<bool?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException)) { return null; }
    }
}

/// <summary>
/// The real source: one <c>nw_path_monitor</c> for the process (started on the first question, on a queue of its own; its update handler is a hand-built global block, see
/// <see cref="GlobalBlock"/>) and <c>NSProcessInfo</c> through the Objective-C runtime. On any failure to set these up the source answers "cannot tell" for good and keeps the reason
/// in <see cref="Problem"/>; the app is never stopped by it.
/// </summary>
public sealed class SystemMacOsConditionsSource : IMacOsConditionsSource
{
    private delegate void PathHandler(IntPtr block, IntPtr path);

    // The block's function cannot carry state: the latest path is in statics (there is one monitor per process).
    private static readonly object Gate = new();
    private static readonly PathHandler Handler = OnPath;
    private static GlobalBlock? _block;
    private static IntPtr _monitor;
    private static volatile int _path;                 // bit 0: a path was seen, bit 1: satisfied, bit 2: expensive, bit 3: constrained
    private static readonly ManualResetEventSlim FirstPath = new(false);
    private static string? _problem;

    /// <summary>Why the network side could not be set up, or null.</summary>
    public static string? Problem
    {
        get { lock (Gate) return _problem; }
    }

    public MacOsNetworkPath? ReadNetworkPath()
    {
        EnsureMonitor();
        if (Problem is not null) return null;   // the monitor could not be started: nothing will ever arrive, so nothing is waited for
        // The first report arrives a moment after the start; wait for it once (briefly) rather than answer "cannot tell" to the first question.
        if (_path == 0) FirstPath.Wait(TimeSpan.FromMilliseconds(500));
        var bits = _path;
        if ((bits & 1) == 0) return null;
        return new MacOsNetworkPath((bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0);
    }

    public bool? ReadLowPowerMode()
    {
        NativeLibraries.RequireMacOS();
        ObjectiveC.LoadFoundation();
        var processInfoClass = ObjectiveC.objc_getClass("NSProcessInfo");
        if (processInfoClass == IntPtr.Zero) return null;
        var processInfo = ObjectiveC.SendForObject(processInfoClass, ObjectiveC.sel_registerName("processInfo"));
        if (processInfo == IntPtr.Zero) return null;
        var selector = ObjectiveC.sel_registerName("isLowPowerModeEnabled");
        // macOS 12 added the property: an older system does not answer the selector.
        if (ObjectiveC.SendForBoolWithSelector(processInfo, ObjectiveC.sel_registerName("respondsToSelector:"), selector) == 0) return null;
        return ObjectiveC.SendForBool(processInfo, selector) != 0;
    }

    private static void EnsureMonitor()
    {
        lock (Gate)
        {
            if (_monitor != IntPtr.Zero || _problem is not null) return;
            try
            {
                NativeLibraries.RequireMacOS();
                var monitor = NetworkFramework.nw_path_monitor_create();
                if (monitor == IntPtr.Zero) throw new InvalidOperationException("Network.framework did not give a path monitor.");
                var queue = Dispatch.dispatch_queue_create("com.beememorybank.blind.network-path", IntPtr.Zero);
                if (queue == IntPtr.Zero) throw new InvalidOperationException("libdispatch did not give a queue.");
                _block = new GlobalBlock(Handler);
                NetworkFramework.nw_path_monitor_set_queue(monitor, queue);
                NetworkFramework.nw_path_monitor_set_update_handler(monitor, _block.Pointer);
                NetworkFramework.nw_path_monitor_start(monitor);
                _monitor = monitor;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            {
                _problem = "The network monitor could not be started (" + ex.GetType().Name + ").";
            }
        }
    }

    /// <summary>Called by the system on the monitor's queue with each new path; reads its three facts and keeps them. Never throws into the system.</summary>
    private static void OnPath(IntPtr block, IntPtr path)
    {
        try
        {
            if (path == IntPtr.Zero) return;
            var bits = 1;
            if (NetworkFramework.nw_path_get_status(path) == NetworkFramework.PathStatusSatisfied) bits |= 2;
            if (NetworkFramework.nw_path_is_expensive(path)) bits |= 4;
            if (NetworkFramework.nw_path_is_constrained(path)) bits |= 8;
            _path = bits;
            FirstPath.Set();
        }
        catch (Exception)
        {
            // An exception must not unwind into the system's thread; the previous answer stays.
        }
    }
}
