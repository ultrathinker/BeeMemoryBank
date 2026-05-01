using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public enum EventApplyResult
{
    Applied,
    SilentlyDropped,
    Skipped
}

public class EventApplier(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    IEventLogRepository eventLogRepo,
    IWhitelistRepository whitelistRepo,
    IConflictVersionRepository conflictRepo,
    ITombstoneRepository tombstoneRepo,
    IWhitelistRepository whitelistRepoWrite,
    ICommentRepository commentRepo,
    IFolderRepository folderRepo,
    ILamportClock clock,
    IMediaRepository mediaRepo,
    INodeIdentityRepository nodeIdentityRepo,
    ConceptTagService conceptTagService,
    IConceptTagRepository conceptTagRepo,
    IEmbeddingGenerator embeddingGenerator,
    HardDeleteService hardDeleteService,
    BeeMemoryBank.Core.Services.MediaStorageOptions? mediaOptions,
    IRestoreReplayShieldRepository replayShieldRepo,
    IRestoreEventStateRepository restoreEventStateRepo,
    IRestoreInitiator restoreInitiator,
    IDekRotationStateRepository dekRotationStateRepo,
    IDekRotationApplier dekRotationApplier,
    ILogger<EventApplier> logger)
{
    // whitelistRepoWrite is the same whitelist, just separated for read/write intent clarity
    // In reality it's the same object from DI

    public async Task<EventApplyResult> ApplyAsync(SyncEvent evt)
    {
        // Protocol version check
        if (evt.ProtocolVersion != 1)
            throw new NotSupportedException($"Unknown protocol version: {evt.ProtocolVersion}");

        // Fast-path idempotency: if event already processed, skip.
        // Must run before the signer check so that self-echoes (a node pulling back
        // its own event from a remote) are silently skipped instead of failing the
        // whitelist lookup (a node does not whitelist itself).
        if (await eventLogRepo.ExistsAsync(evt.EventId)) return EventApplyResult.Applied;

        // Signature verification
        var node = await whitelistRepo.GetByNodeIdAsync(evt.NodeId);
        if (node == null)
        {
            logger.LogWarning("Event {EventId} ({Type}) rejected: originator {NodeId} not in local whitelist (relay drop?)",
                evt.EventId, evt.EventType, evt.NodeId);
            throw new UnauthorizedAccessException($"Node {evt.NodeId} is not in the whitelist.");
        }

        var sigPayload = EventSignature.BuildPayload(evt);
        if (!Ed25519Signer.Verify(node.Ed25519PublicKey, sigPayload, evt.Signature))
            throw new InvalidDataException($"Invalid Ed25519 signature for event {evt.EventId}.");

        logger.LogInformation("Applying event {EventId} {Type} from {NodeId} (lamport={Ts})",
            evt.EventId, evt.EventType, evt.NodeId, evt.LamportTs);

        // TASK 2: Override untrusted actor fields with local node info
        evt.ActorName = node.DisplayName ?? $"node:{evt.NodeId.ToString()[..8]}";
        evt.ActorType = "remote-peer";

        // Hard-delete gate: if entity was hard-deleted at this or later timestamp, skip.
        var identifier = evt.EntityId ?? evt.ArticleId?.ToString();
        if (evt.EventType != EventTypes.HardDelete && !string.IsNullOrEmpty(identifier))
        {
            if (await eventLogRepo.IsHardDeletedAsync(identifier, evt.LamportTs))
            {
                logger.LogWarning("Event {EventId} refers to hard-deleted entity {Identifier}, skipping", evt.EventId, identifier);
                return EventApplyResult.SilentlyDropped;
            }
        }

        // Defensive: silently drop events whose payload contains malformed
        // tree paths (".." / "." / "//" / control chars). A peer cannot use
        // these to escape ACL prefix matching (the path is a literal string,
        // never normalised at read time), but they pollute the local
        // namespace and confuse search/list. We canonicalize-or-drop instead
        // of canonicalize-and-rewrite so peer-side history stays consistent
        // with our DB on the keys it cares about (folder ID, article ID,
        // path string used as ACL prefix). Re-author on the peer if needed.
        if (!IsTreePathPayloadValid(evt))
        {
            logger.LogWarning(
                "Event {EventId} of type {EventType} from {NodeId} has malformed tree path in payload, silently dropping",
                evt.EventId, evt.EventType, evt.NodeId);
            return EventApplyResult.SilentlyDropped;
        }

        // Replay-shield: drop events from peers whose previous events were superseded by RESTORE
        var shieldThreshold = await replayShieldRepo.GetShieldThresholdAsync(evt.NodeId.ToString());
        if (shieldThreshold.HasValue && evt.LamportTs < shieldThreshold.Value)
        {
            logger.LogWarning(
                "Dropping pre-restore event {EventId} from {NodeId} (lamport_ts={LamportTs} < shield threshold {Threshold})",
                evt.EventId, evt.NodeId, evt.LamportTs, shieldThreshold.Value);
            return EventApplyResult.SilentlyDropped;
        }

        // Shield is NOT auto-released here. A single event with forged/inflated lamport_ts could
        // bypass the shield and let subsequent zombie events (real lamport_ts < threshold) through.
        // Shield is only removed by: (a) next RESTORE_NETWORK event handler, (b) admin action, (c) compaction.

        // Authorization gate for cluster-state-modifying events. ANY whitelisted peer can
        // sign these by default; without this check a single rogue peer can revoke the
        // whole network, hard-delete arbitrary data, or trigger a destructive restore.
        // Wave 2 audit: gemini #1 (whitelist), #2 (hard-delete), #3 (restore-network).
        var requiresSuperadmin = evt.EventType == EventTypes.WhitelistAdd
            || evt.EventType == EventTypes.WhitelistRevoke
            || evt.EventType == EventTypes.WhitelistUpdate
            || evt.EventType == EventTypes.HardDelete
            || evt.EventType == EventTypes.RestoreNetwork;
        if (requiresSuperadmin && !node.IsSuperadmin)
        {
            logger.LogWarning(
                "Event {EventId} ({Type}) rejected: originator {NodeId} ({Display}) is not superadmin in local whitelist",
                evt.EventId, evt.EventType, evt.NodeId, node.DisplayName);
            throw new UnauthorizedAccessException(
                $"Event type {evt.EventType} requires superadmin privilege; node {evt.NodeId} is not authorized.");
        }

        // Strip ViaAgentName from remote events too (ActorName/Type already overridden above).
        // Otherwise an attacker could surface a misleading "Security Purge Agent" string in
        // audit logs. Wave 2 audit: gemini #6.
        evt.ViaAgentName = null;

        // Apply data changes BEFORE recording the event. This ensures crash safety:
        // if the process crashes during apply, the event is NOT in tbl_event,
        // so the next sync cycle will re-send it and apply will retry.
        // All apply methods are idempotent (LWW conflict resolution, existence checks),
        // so re-applying a partially-applied event is safe.
        clock.Update(evt.LamportTs);

        switch (evt.EventType)
        {
            case EventTypes.ArticleCreate:
                await ApplyArticleCreateAsync(evt);
                break;
            case EventTypes.ArticleUpdate:
                await ApplyArticleUpdateAsync(evt);
                break;
            case EventTypes.ArticleDelete:
                await ApplyArticleDeleteAsync(evt);
                break;
            case EventTypes.WhitelistAdd:
                await ApplyWhitelistAddAsync(evt);
                break;
            case EventTypes.WhitelistRevoke:
                await ApplyWhitelistRevokeAsync(evt);
                break;
            case EventTypes.WhitelistUpdate:
                await ApplyWhitelistUpdateAsync(evt);
                break;
            case EventTypes.CommentCreate:
                await ApplyCommentCreateAsync(evt);
                break;
            case EventTypes.CommentDelete:
                await ApplyCommentDeleteAsync(evt);
                break;
            case EventTypes.FolderCreate:
                await ApplyFolderCreateAsync(evt);
                break;
            case EventTypes.FolderRename:
                await ApplyFolderRenameAsync(evt);
                break;
            case EventTypes.FolderDelete:
                await ApplyFolderDeleteAsync(evt);
                break;
            case EventTypes.MediaCreate:
                await ApplyMediaCreateAsync(evt);
                break;
            case EventTypes.MediaDelete:
                await ApplyMediaDeleteAsync(evt);
                break;
            case EventTypes.ConceptTagRename:
                await ApplyConceptTagRenameAsync(evt);
                break;
            case EventTypes.ConceptTagMerge:
                await ApplyConceptTagMergeAsync(evt);
                break;
            case EventTypes.ConceptTagDelete:
                await ApplyConceptTagDeleteAsync(evt);
                break;
            case EventTypes.MediaLink:
                await ApplyMediaLinkAsync(evt);
                break;
            case EventTypes.HardDelete:
                await ApplyHardDeleteAsync(evt);
                break;
            case EventTypes.SnapshotCheckpoint:
                break;
            case EventTypes.RestoreNetwork:
                await ApplyRestoreNetworkAsync(evt);
                break;
            case EventTypes.DekRotationProposed:
                await ApplyDekRotationProposedAsync(evt);
                break;
            case EventTypes.DekRotationCommit:
                await ApplyDekRotationCommitAsync(evt);
                break;
            default:
                // Skip unknown event types (forward compatibility)
                break;
        }

        // Record event AFTER successful apply. INSERT OR IGNORE handles the
        // TOCTOU race: if another process already inserted this event (concurrent apply),
        // the duplicate insert is harmlessly ignored since data changes are idempotent.
        await eventLogRepo.AppendIfNotExistsAsync(evt);
        return EventApplyResult.Applied;
    }

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
                catch (InvalidOperationException ex) when (ex.Message.Contains("Session is locked"))
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

    private async Task ApplyHardDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<HardDeleteEventPayload>(evt.Payload);
        await hardDeleteService.ApplyRemoteAsync(p, evt.LamportTs, evt.NodeId, CancellationToken.None);
    }

    private async Task ApplyArticleCreateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        // Tombstone gate: article was deleted before; LWW vs delete's lamport.
        // Wave 2 audit: claude-A #2 (zombie article from out-of-order CREATE-after-DELETE).
        var tombstone = await tombstoneRepo.GetByEntityIdAsync(evt.ArticleId.Value);
        if (tombstone != null && tombstone.LamportTs >= evt.LamportTs)
        {
            logger.LogInformation("ArticleCreate {ArticleId} dropped: tombstone lamport={Tombstone} >= event lamport={Event}",
                evt.ArticleId, tombstone.LamportTs, evt.LamportTs);
            return;
        }

        // If article already exists — this is a duplicate create (rare case), apply as update
        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing != null)
        {
            await ApplyArticleUpdateAsync(evt);
            return;
        }

        var now = DateTime.UtcNow;
        var article = new Article
        {
            Id = evt.ArticleId.Value,
            Title = p.Title,
            TreePath = p.TreePath,
            Status = p.Status,
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        };

        await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
        var folder = await folderRepo.GetByPathAsync(p.TreePath);
        article.FolderId = folder?.Id;

        await articleRepo.CreateAsync(article);

        var body = PayloadToBody(evt.ArticleId.Value, p);
        await bodyRepo.UpsertAsync(body);

        await conceptTagService.SetForArticleAsync(evt.ArticleId.Value, [.. p.ConceptTags ?? []]);
    }

    private async Task ApplyArticleUpdateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        var tombstone = await tombstoneRepo.GetByEntityIdAsync(evt.ArticleId.Value);
        if (tombstone != null && tombstone.LamportTs >= evt.LamportTs)
        {
            logger.LogInformation("ArticleUpdate {ArticleId} dropped: tombstone lamport={T} >= event lamport={E}",
                evt.ArticleId, tombstone.LamportTs, evt.LamportTs);
            return;
        }

        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing == null)
        {
            // Article doesn't exist locally — create it
            await ApplyArticleCreateAsync(evt);
            return;
        }

        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
        {
            // Incoming event wins — save current as conflict_version (with metadata for recovery)
            var existingBody = await bodyRepo.GetByArticleIdAsync(existing.Id);
            if (existingBody != null && existing.LamportTs > 0)
            {
                await conflictRepo.CreateAsync(new ConflictVersion
                {
                    Id = Guid.NewGuid(),
                    ArticleId = existing.Id,
                    SourceNodeId = existingNodeId,
                    LamportTs = existing.LamportTs,
                    Ciphertext = existingBody.Ciphertext,
                    IV = existingBody.IV,
                    EncryptedDek = existingBody.EncryptedDek,
                    DekIV = existingBody.DekIV,
                    MetadataJson = JsonSerializer.Serialize(new { existing.Title, existing.TreePath }),
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                });
            }

            existing.Title = p.Title;
            existing.TreePath = p.TreePath;
            existing.Status = p.Status;
            existing.LamportTs = evt.LamportTs;
            existing.SourceNodeId = evt.NodeId;
            existing.UpdatedAt = p.UpdatedAt;

            await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
            var folder = await folderRepo.GetByPathAsync(p.TreePath);
            existing.FolderId = folder?.Id;

            await articleRepo.UpdateAsync(existing);

            var body = PayloadToBody(evt.ArticleId.Value, p);
            await bodyRepo.UpsertAsync(body);

            await conceptTagService.SetForArticleAsync(evt.ArticleId.Value, [.. p.ConceptTags ?? []]);
        }
        else
        {
            var incomingBody = PayloadToBody(evt.ArticleId.Value, p);
            await conflictRepo.CreateAsync(new ConflictVersion
            {
                Id = Guid.NewGuid(),
                ArticleId = evt.ArticleId.Value,
                SourceNodeId = evt.NodeId,
                LamportTs = evt.LamportTs,
                Ciphertext = incomingBody.Ciphertext,
                IV = incomingBody.IV,
                EncryptedDek = incomingBody.EncryptedDek,
                DekIV = incomingBody.DekIV,
                MetadataJson = JsonSerializer.Serialize(new { p.Title, p.TreePath }),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });
        }
    }

    private async Task ApplyArticleDeleteAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleDeletePayload>(evt.Payload);

        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing == null)
        {
            // Out-of-order: DELETE arrived before CREATE. Without recording a tombstone here,
            // a later CREATE would resurrect the article unconditionally — the delete would
            // be permanently lost. Mirror the comment SoftDeletePlaceholderAsync pattern by
            // writing a tombstone with the delete's lamport so a late CREATE goes through
            // the LWW gate at the top of ApplyArticleCreateAsync.
            // Wave 2 audit: claude-A #1, kilo-1 #2.
            await tombstoneRepo.CreateAsync(new Tombstone
            {
                ArticleId = evt.ArticleId.Value,
                CreatedAt = p.DeletedAt,
                ExpiresAt = p.DeletedAt.AddDays(60),
                LamportTs = evt.LamportTs,
                SourceNodeId = evt.NodeId
            });
            return;
        }
        if (existing.Status != "A") return;

        // LWW check: only delete + tombstone if incoming event wins over existing state.
        // A stale delete that loses LWW must NOT create a tombstone — otherwise it would
        // block recreation of an article ID that was never actually deleted (60-day TTL).
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await articleRepo.SoftDeleteAsync(evt.ArticleId.Value);
        await tombstoneRepo.CreateAsync(new Tombstone
        {
            ArticleId = evt.ArticleId.Value,
            CreatedAt = p.DeletedAt,
            ExpiresAt = p.DeletedAt.AddDays(60),
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId
        });
    }

    private async Task ApplyWhitelistAddAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistAddPayload>(evt.Payload);

        // Skip self — a node must never be in its own whitelist.
        // This can happen when another node adds *us* as their trusted node and
        // syncs that whitelist_add event back to us. We already know we exist
        // (tbl_node_identity); having a row in tbl_whitelist about ourselves
        // is confusing in the UI and corrupts sync position bookkeeping.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing != null)
        {
            // If previously revoked, re-activate — but NEVER replace the Ed25519 public key.
            // The key is bound to NodeId at first registration. Replacing it via a stale/replayed
            // WhitelistAdd event would allow node impersonation with a compromised old key.
            if (existing.Status == "R")
            {
                existing.DisplayName = p.DisplayName;
                existing.ApiAddress = p.ApiAddress;
                existing.CanGenerateEmbeddings = p.CanGenerateEmbeddings;
                existing.IsSuperadmin = p.IsSuperadmin;
                existing.Status = "A";
                existing.UpdatedAt = DateTime.UtcNow;
                await whitelistRepoWrite.UpdateAsync(existing);
            }
            return;
        }

        var now = DateTime.UtcNow;
        await whitelistRepoWrite.CreateAsync(new WhitelistEntry
        {
            NodeId = p.NodeId,
            DisplayName = p.DisplayName,
            Ed25519PublicKey = Convert.FromBase64String(p.PublicKeyB64),
            ApiAddress = p.ApiAddress,
            CanGenerateEmbeddings = p.CanGenerateEmbeddings,
            IsSuperadmin = p.IsSuperadmin,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private async Task ApplyWhitelistRevokeAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistRevokePayload>(evt.Payload);

        // Never revoke self via a remote event — we can't revoke ourselves.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;
        await whitelistRepoWrite.RevokeAsync(p.NodeId);
    }

    private async Task ApplyWhitelistUpdateAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistUpdatePayload>(evt.Payload);

        // Never update self via a remote event.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;

        if (p.ApiAddress != null) existing.ApiAddress = p.ApiAddress;
        if (p.DisplayName != null) existing.DisplayName = p.DisplayName;
        existing.UpdatedAt = DateTime.UtcNow;

        await whitelistRepoWrite.UpdateAsync(existing);
    }

    private async Task ApplyCommentCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentEventPayload>(evt.Payload);

        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        // Downgrade-prevention: refuse a brand-new unencrypted comment under an encrypted
        // article body. Skip this gate for existing-row paths (LWW resurrect / update),
        // which preserve the originally-encrypted payload anyway.
        if (existing == null)
        {
            var parentBody = await bodyRepo.GetByArticleIdAsync(p.ArticleId);
            if (parentBody != null && !p.Encrypted)
            {
                logger.LogWarning("Rejecting unencrypted comment for encrypted article {ArticleId} (downgrade attempt)", p.ArticleId);
                return;
            }
        }

        if (existing?.DeletedAt != null)
        {
            // Soft-deleted row (tombstone equivalent): check LWW
            // If delete's lamport >= create's lamport, dead stays dead.
            if ((existing.DeleteLamportTs ?? 0) >= evt.LamportTs)
                return;

            // Create wins: resurrect the row with real data
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        if (existing != null)
        {
            // Alive row: LWW
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
                return;
            // LWW-wins must update CONTENT too — old code only bumped lamport, leaving stale
            // text/ciphertext attached to a newer timestamp. Future comparisons would see
            // the stale content as "newer". (Wave 2 audit kilo-1 #3.)
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        // No row: create
        await commentRepo.CreateFromSyncAsync(new Comment
        {
            CommentId = p.CommentId,
            ArticleId = p.ArticleId,
            Text = p.Encrypted ? "" : p.Text,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
            IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
            Encrypted = p.Encrypted,
            LamportTs = evt.LamportTs
        });
    }

    private async Task ApplyCommentDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentDeletePayload>(evt.Payload);
        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        if (existing == null)
        {
            // Comment not on this node yet — insert placeholder ghost row so future
            // CommentCreate events can be blocked if delete has higher lamport.
            await commentRepo.SoftDeletePlaceholderAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            return;
        }

        if (existing.DeletedAt != null)
        {
            // Already soft-deleted — keep the higher lamport
            if (evt.LamportTs > (existing.DeleteLamportTs ?? 0))
                await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            return;
        }

        // Alive comment: LWW
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return; // Delete loses to existing create

        // Delete wins: soft-delete
        await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
    }

    private async Task ApplyFolderCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderCreatePayload>(evt.Payload);

        var existing = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (existing != null)
        {
            // Already exists — apply LWW: incoming wins if its timestamp is newer
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
                return; // local wins, skip

            existing.Path = p.Path;
            existing.Name = p.Name;
            existing.ParentPath = p.ParentPath;
            existing.Status = "A";
            existing.LamportTs = evt.LamportTs;
            existing.SourceNodeId = evt.NodeId;
            existing.UpdatedAt = p.UpdatedAt;
            existing.DeletedAt = null;
            await folderRepo.UpdateAsync(existing);
            return;
        }

        // Ensure parent path exists
        if (p.ParentPath != null)
            await folderRepo.EnsureExistsAsync(p.ParentPath, evt.NodeId);

        await folderRepo.CreateAsync(new Folder
        {
            Id = p.FolderId,
            Path = p.Path,
            Name = p.Name,
            ParentPath = p.ParentPath,
            Status = "A",
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        });
    }

    private async Task ApplyFolderRenameAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderRenamePayload>(evt.Payload);

        var folder = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (folder == null)
        {
            // Folder not known locally — ensure the old path exists so rename can proceed
            await folderRepo.EnsureExistsAsync(p.OldPath, evt.NodeId);
            folder = await folderRepo.GetByPathAsync(p.OldPath);
            if (folder == null) return; // cannot apply
        }

        // LWW check
        var existingNodeId = folder.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(folder.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return; // local wins, skip

        await folderRepo.RenamePathAsync(p.OldPath, p.NewPath, p.FolderId, evt.LamportTs, evt.NodeId, p.UpdatedAt);
    }

    private async Task ApplyFolderDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderDeletePayload>(evt.Payload);

        var folder = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (folder == null) return;

        if (folder.Status == "D")
        {
            if (evt.LamportTs > folder.LamportTs)
            {
                folder.LamportTs = evt.LamportTs;
                folder.SourceNodeId = evt.NodeId;
                folder.UpdatedAt = p.DeletedAt;
                await folderRepo.UpdateAsync(folder);
            }
            return;
        }

        // LWW check: skip stale delete events (matches pattern in ApplyFolderRenameAsync)
        var existingNodeId = folder.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(folder.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await folderRepo.SoftDeleteAsync(p.FolderId, p.DeletedAt);
        await articleRepo.ClearFolderIdAsync(p.FolderId);
    }

    private async Task ApplyMediaCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaEventPayload>(evt.Payload);

        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing != null)
        {
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
                return;
            // Media ciphertext is immutable; only advance LWW metadata so later
            // delete events with stale timestamps are rejected correctly.
            await mediaRepo.UpdateLamportTsAsync(p.MediaId, evt.LamportTs, evt.NodeId);
            return;
        }

        var media = new Media
        {
            Id = p.MediaId,
            ArticleId = p.ArticleId,
            FileName = p.FileName,
            ContentType = p.ContentType,
            FileSize = p.FileSize,
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64),
            IV = Convert.FromBase64String(p.IvB64),
            Status = "A",
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt
        };
        if (mediaOptions != null)
        {
            var mediaDir = mediaOptions.MediaDir;
            Directory.CreateDirectory(mediaDir);
            var filePath = Path.Combine(mediaDir, $"{p.MediaId}.enc");
            await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(p.CiphertextB64));
        }

        await mediaRepo.CreateAsync(media);
    }

    private async Task ApplyMediaDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaDeletePayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null || existing.Status == "D") return;

        // LWW check: skip stale delete events
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await mediaRepo.SoftDeleteAsync(p.MediaId);
    }

    private async Task ApplyConceptTagRenameAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagRenamePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.RenameAsync(p.OldName, p.NewName);

            try
            {
                var embedding = embeddingGenerator.Generate(p.NewName);
                var bytes = new byte[embedding.Length * 4];
                Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
                await conceptTagRepo.UpdateEmbeddingAsync(p.NewName, bytes, "hash-v1");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to regenerate embedding for renamed concept tag '{NewName}'", p.NewName);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_rename: concept '{OldName}' not found or already renamed", p.OldName);
        }
    }

    private async Task ApplyConceptTagMergeAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagMergePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.MergeAsync(p.Source, p.Target);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_merge: source '{Source}' not found or already merged", p.Source);
        }
    }

    private async Task ApplyConceptTagDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<ConceptTagDeletePayload>(evt.Payload);
        try
        {
            await conceptTagRepo.DeleteAsync(p.Name);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Skipping concept_tag_delete: concept '{Name}' not found or already deleted", p.Name);
        }
    }

    private async Task ApplyMediaLinkAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaLinkEventPayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null) return;
        if (existing.ArticleId != null) return;
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;
        await mediaRepo.LinkOrphansToArticleAsync(new[] { p.MediaId }, p.ArticleId, evt.LamportTs, evt.NodeId);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException($"Failed to deserialize payload as {typeof(T).Name}");

    private static EncryptedArticleBody PayloadToBody(Guid articleId, ArticleEventPayload p) =>
        new()
        {
            ArticleId = articleId,
            Ciphertext = Convert.FromBase64String(p.CiphertextB64),
            IV = Convert.FromBase64String(p.IvB64),
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64)
        };

    /// <summary>
    /// True unless a tree path inside the payload contains a strictly
    /// illegal segment (".." / "." / control chars / NUL). Cosmetic
    /// non-canonical input ("//" or trailing "/") IS allowed through:
    /// dropping it would permanently diverge from peers running
    /// pre-canonicalisation code whose history legitimately contains
    /// such paths (gemini review feedback). Only event types that carry
    /// user-controlled paths are checked; others pass through.
    /// </summary>
    private static bool IsTreePathPayloadValid(SyncEvent evt)
    {
        try
        {
            switch (evt.EventType)
            {
                case EventTypes.ArticleCreate:
                case EventTypes.ArticleUpdate:
                {
                    var p = JsonSerializer.Deserialize<ArticleEventPayload>(evt.Payload);
                    return !TreePathCanonicalizer.IsIllegal(p?.TreePath);
                }
                case EventTypes.FolderCreate:
                {
                    var p = JsonSerializer.Deserialize<FolderCreatePayload>(evt.Payload);
                    if (p == null) return true;
                    return !TreePathCanonicalizer.IsIllegal(p.Path)
                        && !TreePathCanonicalizer.IsIllegal(p.ParentPath);
                }
                case EventTypes.FolderRename:
                {
                    var p = JsonSerializer.Deserialize<FolderRenamePayload>(evt.Payload);
                    if (p == null) return true;
                    return !TreePathCanonicalizer.IsIllegal(p.OldPath)
                        && !TreePathCanonicalizer.IsIllegal(p.NewPath);
                }
                default:
                    return true;
            }
        }
        catch
        {
            // Bad JSON / shape — let the per-event Deserialize<T> raise its
            // own error; don't double-fail in the validator.
            return true;
        }
    }
}
