namespace BeeMemoryBank.Core.Services;

/// <remarks>
/// AUDIT NOTE: No lock/volatile needed. Enter/Exit are called sequentially within a single
/// restore request handler. Bool reads are atomic in .NET. Worst case: a concurrent request
/// sees stale IsInMaintenance for one check — MaintenanceMiddleware will catch it next time.
/// </remarks>
public class MaintenanceModeService
{
    public bool IsInMaintenance { get; private set; }
    public string? Reason { get; private set; }

    public void Enter(string reason)
    {
        IsInMaintenance = true;
        Reason = reason;
    }

    public void Exit()
    {
        IsInMaintenance = false;
        Reason = null;
    }
}
