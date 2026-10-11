using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Metadata;

public sealed record ActivationOutcome(bool Activated, Guid? ActivationId, string? KeyId, int? Generation, IReadOnlyList<ReadinessBlocker> Blockers);

/// <summary>
/// The owner's one-way switch (docs/design/step-a-spec.md 5.4). Under one lock: the readiness report again (refused on any
/// blocker); generation 1 of the metadata key when this node can open none; the metadata_key_set and metadata_activation
/// events, applied here first and then logged for every peer; an audit entry with ids only. From then on this node
/// declares protocol 4 and seals content. There is no way back but restoring the backup taken before (owner decision 9).
/// </summary>
public sealed class MetadataActivationService(
    MetadataReadinessService readiness,
    MetadataEventPublisher publisher,
    IMetadataKeyRing ring,
    IAuditLogRepository audit,
    ILogger<MetadataActivationService> logger)
{
    // One activation at a time per process; the activation row itself makes a second one a no-op on every node anyway.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ActivationOutcome> ActivateAsync(string actor, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var report = await readiness.GetReportAsync(askPeers: true, ct);
            if (!report.Ready)
                return new ActivationOutcome(false, null, null, null, report.Blockers);

            var key = ring.Current;
            if (key is null)
            {
                var row = await publisher.PublishNewKeyAsync(generation: 1);
                key = new MetadataKeyInfo(row.KeyId, row.Generation);
                if (!ring.CanOpen(key.KeyId))
                    throw new InvalidOperationException("The new metadata key does not open on this node.");
            }

            var activationId = Guid.NewGuid();
            await publisher.PublishActivationAsync(activationId, key);
            await audit.LogAsync("metadata", activationId.ToString("D"), "metadata_activated", "web",
                $"Metadata encryption activated by user {actor}: key {key.KeyId}, generation {key.Generation}");
            logger.LogWarning("Metadata encryption activated ({ActivationId}, key generation {Generation})", activationId, key.Generation);
            return new ActivationOutcome(true, activationId, key.KeyId, key.Generation, []);
        }
        finally
        {
            Gate.Release();
        }
    }
}
