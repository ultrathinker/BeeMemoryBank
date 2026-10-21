using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>The states of an <c>NWPath</c> the copy tells apart. Anything iOS adds later reads as <see cref="Unknown"/>.</summary>
public enum IosPathStatus
{
    /// <summary>No answer yet, or a state this app does not know.</summary>
    Unknown,

    /// <summary>NWPath.Status.Satisfied: a network that can carry a connection.</summary>
    Satisfied,

    /// <summary>NWPath.Status.Unsatisfied: no network.</summary>
    Unsatisfied,

    /// <summary>NWPath.Status.Satisfiable: a network that needs a connection to be brought up first (a VPN on demand).</summary>
    Satisfiable,
}

/// <summary>What the Network framework last said about the network in use. <see cref="IsExpensive"/> is iOS's word for a metered network (cellular, a Personal Hotspot); <see cref="IsConstrained"/> is Low Data Mode.</summary>
public sealed record IosNetworkPath(IosPathStatus Status, bool IsExpensive, bool IsConstrained);

/// <summary>Where the last network path comes from: <c>Platforms/iOS/IosNetworkPathMonitor.cs</c> (an NWPathMonitor) on the phone, a fake in the tests.</summary>
public interface IIosNetworkPathSource
{
    /// <summary>The last path the system delivered; null until the first one arrives (it follows a start by a moment, not at once).</summary>
    IosNetworkPath? Current { get; }

    /// <summary>Starts watching the network. Safe to call again.</summary>
    void Start();

    /// <summary>Completes when a path is known, or after <paramref name="timeout"/>; never throws for the timeout. A background wake asks this before it spends a round, so that "unknown" does not wait the round out for nothing.</summary>
    Task WaitForFirstPathAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Whether the phone is in Low Power Mode, behind a seam (the real one is <c>NSProcessInfo.LowPowerModeEnabled</c>).</summary>
public interface IIosPowerSource
{
    bool? LowPowerModeEnabled { get; }
}

/// <summary>
/// The last path a platform monitor delivered, kept for the synchronous question "is the network metered": thread-safe, and a waiter can be released by the first path. The monitor's
/// callback calls <see cref="Update"/> from its own queue.
/// </summary>
public sealed class IosNetworkPathCache
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IosNetworkPath? _current;

    public IosNetworkPath? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(IosNetworkPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate) _current = path;
        _first.TrySetResult();
    }

    /// <summary>Forgets the path (the monitor was stopped: what it said is no longer current).</summary>
    public void Clear()
    {
        lock (_gate) _current = null;
    }

    public async Task WaitForFirstAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (Current is not null) return;
        try { await _first.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}

/// <summary>The rule that turns an NWPath into the one question AppCore asks.</summary>
public static class IosNetworkPathRules
{
    /// <summary>
    /// True when the network in use is expensive (cellular, Personal Hotspot) or constrained (Low Data Mode); false when it is known to be neither; null when there is no network in use to
    /// judge (no answer yet, no network, one that needs a connection first). Null is "this device cannot tell": an account with "only on unmetered networks" then waits, never uses the data plan.
    /// </summary>
    public static bool? IsMetered(IosNetworkPath? path)
    {
        if (path is null || path.Status != IosPathStatus.Satisfied) return null;
        return path.IsExpensive || path.IsConstrained;
    }
}

/// <summary>
/// The iPhone's answers to the two switches of an account (<see cref="IBlindDeviceConditions"/>): the network is metered when the path is expensive or constrained
/// (<c>NWPathMonitor</c>), and the phone is saving power in Low Power Mode (<c>NSProcessInfo.LowPowerModeEnabled</c>). Read live at each question: nothing is cached here.
/// </summary>
public sealed class IosDeviceConditions(IIosNetworkPathSource network, IIosPowerSource power) : IBlindDeviceConditions
{
    public bool? IsMetered => IosNetworkPathRules.IsMetered(network.Current);

    public bool? IsPowerSaving => power.LowPowerModeEnabled;
}
