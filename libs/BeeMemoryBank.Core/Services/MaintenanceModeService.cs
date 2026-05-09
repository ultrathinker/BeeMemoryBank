namespace BeeMemoryBank.Core.Services;

/// <remarks>
/// AUDIT NOTE (revised after Kilo R1 security review HIGH-2): originally a single bool flag
/// without synchronization. Safe under the original assumption that only ONE flow at a time
/// (restore) entered maintenance mode. After DEK rotation landed, both rotation and restore
/// share this service AND share HeavyOperationLock — but HeavyOperationLock guards different
/// operations (rotation Accept vs restore Apply) and the auto-accept peer rotation path enters
/// maintenance from EventApplier, which is a different code path than the restore flow.
///
/// Now: refcounted. Enter increments; only the first Enter sets IsInMaintenance and Reason.
/// Exit decrements; only the last Exit clears them. Concurrent enter+exit pairs nest correctly.
/// </remarks>
public class MaintenanceModeService
{
    private readonly object _lock = new();
    private int _refCount;
    private string? _reason;

    public bool IsInMaintenance => Volatile.Read(ref _refCount) > 0;
    public string? Reason { get { lock (_lock) { return _reason; } } }

    public void Enter(string reason)
    {
        lock (_lock)
        {
            _refCount++;
            // Reason wins on the FIRST enter; nested enters from a different operation
            // shouldn't overwrite the original reason text. (We could concatenate, but the
            // 503 response only fits one short string anyway.)
            _reason ??= reason;
        }
    }

    public void Exit()
    {
        lock (_lock)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount == 0) _reason = null;
        }
    }
}
