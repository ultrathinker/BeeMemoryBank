using BeeMemoryBank.BlindIos.Services;
using CoreFoundation;
using Foundation;
using Network;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The phone's answer to "is the network metered", from an <c>NWPathMonitor</c> (Network framework): the monitor reports the path in use whenever it changes, on a queue of its own, and
/// <see cref="IosNetworkPathCache"/> keeps the last one for the synchronous question AppCore asks (<see cref="IosDeviceConditions"/>). <c>IsExpensive</c> is iOS's word for cellular and
/// a Personal Hotspot, <c>IsConstrained</c> is Low Data Mode; the rule that turns them into "metered" is <see cref="IosNetworkPathRules"/>, tested on any OS. The monitor delivers its first
/// path a moment after it starts, so a background wake waits for it (<see cref="WaitForFirstPathAsync"/>) before it spends a round. Not promised: <c>NSURLSession</c>'s own cellular
/// constraints - the app uses the managed HTTP handler (UseNativeHttpHandler=false), so only this check keeps the copy off a data plan.
/// </summary>
public sealed class IosNetworkPathMonitor : IIosNetworkPathSource
{
    private readonly IosNetworkPathCache _cache = new();
    private readonly object _gate = new();
    private NWPathMonitor? _monitor;

    public IosNetworkPath? Current => _cache.Current;

    public void Start()
    {
        lock (_gate)
        {
            if (_monitor is not null) return;
            var monitor = new NWPathMonitor();
            monitor.SnapshotHandler = path => _cache.Update(Convert(path));
            monitor.SetQueue(DispatchQueue.DefaultGlobalQueue);
            monitor.Start();
            _monitor = monitor;
        }
    }

    public Task WaitForFirstPathAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Start();
        return _cache.WaitForFirstAsync(timeout, ct);
    }

    private static IosNetworkPath Convert(NWPath path) => new(
        path.Status switch
        {
            NWPathStatus.Satisfied => IosPathStatus.Satisfied,
            NWPathStatus.Unsatisfied => IosPathStatus.Unsatisfied,
            NWPathStatus.Satisfiable => IosPathStatus.Satisfiable,
            _ => IosPathStatus.Unknown,
        },
        path.IsExpensive,
        path.IsConstrained);
}
