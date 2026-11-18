using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// What every node writes when it has applied a removal rotation (reason <c>offboarding:&lt;id&gt;</c>, 2.12): a warning in its log and an
/// audit entry, so that a key change nobody on this node approved is never silent. Best effort: the rotation is applied already.
/// </summary>
public static class RemovalRotationRecord
{
    public const string AuditAction = "dek_rotation_removal_applied";

    public static async Task WriteAsync(IServiceProvider services, SyncEvent commit, DekRotationCommitPayload payload, ILogger logger)
    {
        logger.LogWarning(
            "A removal rotation was applied on this node: commit {CommitEventId} from {NodeId} ({Reason}), epoch {Epoch}. It did not wait for an administrator.",
            commit.EventId, commit.NodeId, payload.Reason, payload.NewDekEpoch);
        try
        {
            if (services.GetService<IAuditLogRepository>() is { } audit)
                await audit.LogAsync("dek_rotation", commit.EventId.ToString(), AuditAction, "system",
                    $"Removal rotation applied without an administrator; reason {payload.Reason}; from node {commit.NodeId}; epoch {payload.NewDekEpoch}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The audit entry of the removal rotation {CommitEventId} could not be written", commit.EventId);
        }
    }
}
