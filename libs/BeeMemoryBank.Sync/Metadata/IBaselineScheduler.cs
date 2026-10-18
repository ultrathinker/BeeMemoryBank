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

/// <summary>The hold of <see cref="IBaselineScheduler.PauseAsync"/> for a scheduler that makes no baselines of its own: nothing to release.</summary>
public static class BaselineHold
{
    public static readonly IDisposable None = new NoHold();

    private sealed class NoHold : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>Full nodes only (spec 7.5); EventApplier takes it as an optional dependency.</summary>
public interface IBaselineScheduler
{
    void RequestEarly(string reasonCode);

    /// <summary>
    /// Holds the scheduler off until the returned hold is disposed: it returns once no baseline is being made, and no baseline is started meanwhile
    /// (a request that comes in stays pending and is served after the release). A baseline works from a plain staging copy of the database, which
    /// is what the setup of a node, about to encrypt it, must not find in its data folder.
    /// </summary>
    Task<IDisposable> PauseAsync(CancellationToken ct = default) => Task.FromResult(BaselineHold.None);
}

/// <summary>What a blind host holds, for status and my-standing (spec 10). No note count by design.</summary>
public sealed record BlindStoreSummary(
    Guid? BaselineId, Guid? BaselineProducer, DateTime? BaselineCreatedAt, int? KeyGeneration,
    long DeltaCount, long DeltaBytes, long BlobCount, long BlobBytes, long StoredBytes);

public interface IBlindStoreSummarySource
{
    Task<BlindStoreSummary> GetSummaryAsync(CancellationToken ct = default);
}
