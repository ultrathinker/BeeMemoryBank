using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// What activation does on a full node (docs/design/step-a-spec.md 5.5): an early baseline is requested, and the cached join snapshot,
/// plain until now, is dropped so the next joiner gets the encrypted one. Registered on every full-role host; a blind host has its own.
/// </summary>
public sealed class FullNodeActivationObserver(
    IBaselineScheduler scheduler, SnapshotJoinCache joinCache, ILogger<FullNodeActivationObserver> logger) : IMetadataActivationObserver
{
    public Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default)
    {
        joinCache.Invalidate();
        scheduler.RequestEarly(BaselineReasons.Activation);
        logger.LogInformation("Metadata activation {ActivationId} seen: baseline requested, join snapshot cache dropped", activation.ActivationId);
        return Task.CompletedTask;
    }
}
