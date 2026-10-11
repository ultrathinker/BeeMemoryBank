namespace BeeMemoryBank.Sync.Metadata;

public static class BaselineReasons
{
    public const string Scheduled = "scheduled";
    public const string Activation = "activation";
    public const string HardDelete = "hard_delete";
    public const string LateProtocol3 = "late_protocol3";
    public const string KeyRotation = "key_rotation";
    public const string BlindReseed = "blind_reseed";
    public const string OnDemand = "on_demand";
}

/// <summary>Full nodes only (spec 7.5); EventApplier takes it as an optional dependency.</summary>
public interface IBaselineScheduler
{
    void RequestEarly(string reasonCode);
}

/// <summary>What a blind host holds, for status and my-standing (spec 10). No note count by design.</summary>
public sealed record BlindStoreSummary(
    Guid? BaselineId, Guid? BaselineProducer, DateTime? BaselineCreatedAt, int? KeyGeneration,
    long DeltaCount, long DeltaBytes, long BlobCount, long BlobBytes, long StoredBytes);

public interface IBlindStoreSummarySource
{
    Task<BlindStoreSummary> GetSummaryAsync(CancellationToken ct = default);
}
