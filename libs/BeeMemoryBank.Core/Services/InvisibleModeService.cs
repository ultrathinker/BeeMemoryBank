namespace BeeMemoryBank.Core.Services;

/// <remarks>
/// AUDIT NOTE: No volatile/Interlocked needed. This singleton bool is read in HTTP request
/// scopes and in SyncScheduler (every 60s). The JIT will not cache a property read across
/// method boundaries. Worst case on stale read: one extra sync cycle runs or is skipped.
/// This is acceptable for a non-critical UX toggle.
/// </remarks>
public class InvisibleModeService
{
    public bool IsInvisible { get; set; }
}
