namespace BeeMemoryBank.Sync.Metadata;

public sealed record HeldEventSummary(
    int Held, int HeldLocked, int HeldKey, int HeldOrder,
    DateTime? OldestHeldAt, long AppliedThroughSequence, long HeadSequence, int QuarantinedAfterReceipt);

/// <summary>Received-but-not-applied events of this node (spec 6.6, 6.8). A blind host always reports none.</summary>
public interface IHeldEventStatus
{
    Task<bool> AnyHeldAsync(CancellationToken ct = default);
    Task<HeldEventSummary> GetSummaryAsync(CancellationToken ct = default);
}

/// <summary>Asks the drain (spec 6.7) to run soon; a no-op where nothing can be held.</summary>
public interface IHeldEventDrain
{
    void RequestDrain();
}
