using System.Text.Json;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private async Task ApplyRestoreNetworkAsync(SyncEvent evt)
    {
        var payload = Deserialize<RestoreNetworkEventPayload>(evt.Payload);
        if (payload == null)
        {
            logger.LogWarning("RESTORE_NETWORK event {EventId} has invalid payload, skipping", evt.EventId);
            return;
        }

        var existing = await restoreEventStateRepo.GetAsync(evt.EventId.ToString());
        if (existing != null && existing.State != RestoreEventState.Pending)
        {
            return;
        }

        var nowIso = DateTime.UtcNow.ToString("O");
        await restoreEventStateRepo.UpsertAsync(new RestoreEventStateRow(
            evt.EventId.ToString(),
            RestoreEventState.Pending,
            null, false, null, null,
            nowIso, nowIso));

        var autoAccept = await whitelistRepo.GetAutoAcceptRestoreAsync(evt.NodeId.ToString());

        if (autoAccept)
        {
            logger.LogInformation("Auto-accepting RESTORE_NETWORK event {EventId} from {NodeId}",
                evt.EventId, evt.NodeId);
            _ = Task.Run(async () =>
            {
                try
                {
                    await restoreInitiator.AcceptRestoreAsync(evt.EventId.ToString(), payload, evt);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Auto-accept restore failed for event {EventId}", evt.EventId);
                }
            });
        }
        else
        {
            logger.LogInformation("RESTORE_NETWORK event {EventId} from {NodeId} pending manual approval",
                evt.EventId, evt.NodeId);
        }
    }

    private async Task ApplyDekRotationProposedAsync(SyncEvent evt)
    {
        var payload = Deserialize<DekRotationProposedPayload>(evt.Payload);
        if (payload == null)
        {
            logger.LogWarning("DEK_ROTATION_PROPOSED event {EventId} has invalid payload, skipping", evt.EventId);
            return;
        }

        var existing = await dekRotationStateRepo.GetAsync(evt.EventId.ToString());
        if (existing != null) return;

        var nowIso = DateTime.UtcNow.ToString("O");
        await dekRotationStateRepo.UpsertAsync(new DekRotationStateRow(
            EventId: evt.EventId.ToString(),
            State: DekRotationState.Proposed,
            ProposedEventId: null,
            RotationTs: payload.RotationTs,
            AppliedAt: null,
            ErrorMessage: null,
            LastProcessedIdArticle: null,
            LastProcessedIdArticleVersion: null,
            LastProcessedIdMedia: null,
            LastProcessedIdConflictVersion: null,
            LastProcessedIdComment: null,
            CreatedAt: nowIso,
            UpdatedAt: nowIso));

        logger.LogInformation(
            "DEK_ROTATION_PROPOSED received from {NodeId} (event_id {EventId}); waiting for COMMIT",
            evt.NodeId, evt.EventId);
    }

    private async Task ApplyDekRotationCommitAsync(SyncEvent evt)
    {
        var payload = Deserialize<DekRotationCommitPayload>(evt.Payload);
        if (payload == null)
        {
            logger.LogWarning("DEK_ROTATION_COMMIT event {EventId} has invalid payload, skipping", evt.EventId);
            return;
        }

        var existing = await dekRotationStateRepo.GetAsync(evt.EventId.ToString());
        if (existing != null) return;

        // Validate that the matching PROPOSED event exists locally before accepting the COMMIT.
        // Without this, a malicious peer with a still-trusted Ed25519 key could craft a
        // dek_rotation_commit referencing an arbitrary ProposedEventId that was never proposed,
        // bypassing the propose-then-commit protocol. (Found by Kilo R1 security review HIGH-4.)
        //
        // CRITICAL: throw (don't return) so ApplyAsync's outer pattern doesn't record the event
        // into tbl_event via AppendIfNotExistsAsync. If we returned, the COMMIT would be marked
        // processed and never retried even when PROPOSED later arrives — peer would be stuck
        // permanently out of sync. Throw → sync caller retries → eventually PROPOSED arrives
        // first and COMMIT gets accepted on the next delivery. (Found by Gemini R2 prod review.)
        var proposedRow = await dekRotationStateRepo.GetAsync(payload.ProposedEventId);
        if (proposedRow == null)
        {
            logger.LogWarning(
                "DEK_ROTATION_COMMIT {CommitId} from {NodeId} references unknown ProposedEventId {ProposedId}; deferring (will retry on next sync pull when PROPOSED arrives, or fail permanently if it never does).",
                evt.EventId, evt.NodeId, payload.ProposedEventId);
            throw new InvalidOperationException(
                $"DEK_ROTATION_COMMIT references missing ProposedEventId {payload.ProposedEventId}; deferring until PROPOSED is delivered.");
        }

        var nowIso = DateTime.UtcNow.ToString("O");
        await dekRotationStateRepo.UpsertAsync(new DekRotationStateRow(
            EventId: evt.EventId.ToString(),
            State: DekRotationState.Committing,
            ProposedEventId: payload.ProposedEventId,
            RotationTs: payload.RotationTs,
            AppliedAt: null,
            ErrorMessage: null,
            LastProcessedIdArticle: null,
            LastProcessedIdArticleVersion: null,
            LastProcessedIdMedia: null,
            LastProcessedIdConflictVersion: null,
            LastProcessedIdComment: null,
            CreatedAt: nowIso,
            UpdatedAt: nowIso));

        var autoAccept = await whitelistRepo.GetAutoAcceptDekRotationAsync(evt.NodeId.ToString());

        if (autoAccept)
        {
            logger.LogInformation("Auto-accepting DEK_ROTATION_COMMIT event {EventId} from {NodeId}",
                evt.EventId, evt.NodeId);
            _ = Task.Run(async () =>
            {
                try
                {
                    await dekRotationApplier.AutoAcceptCommitAsync(evt);
                }
                catch (SessionLockedException)
                {
                    logger.LogInformation(
                        "DEK rotation auto-accept skipped for event {EventId}: session is locked, waiting for manual accept",
                        evt.EventId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Auto-accept DEK rotation failed for event {EventId}", evt.EventId);
                }
            });
        }
        else
        {
            logger.LogInformation(
                "DEK_ROTATION_COMMIT received from {NodeId} (event_id {EventId}, proposed_event_id {Proposed}); awaiting admin accept",
                evt.NodeId, evt.EventId, payload.ProposedEventId);
        }
    }
}
