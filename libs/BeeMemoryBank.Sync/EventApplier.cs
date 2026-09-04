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

public partial class EventApplier(
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
    FolderAccessService folderAccess,
    IDbConnectionFactory connFactory,
    IBlobRepository blobRepo,
    ILogger<EventApplier> logger)
{
    // whitelistRepoWrite is the same whitelist, just separated for read/write intent clarity
    // In reality it's the same object from DI

    public async Task<EventApplyResult> ApplyAsync(SyncEvent evt)
    {
        // Protocol version check. Version 1 events (ciphertext inline) are still applied: the log
        // holds them, and a peer that has not upgraded still emits them.
        if (!SyncProtocolVersion.CanApply(evt.ProtocolVersion))
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
            // "No ACTIVE row" covers two situations that must not be treated alike, and
            // GetByNodeIdAsync filters on status = 'A', so both arrive here as null. Look again
            // including revoked rows to tell them apart.
            //
            // Never heard of this node  -> deferrable: in a mesh the whitelist_add can easily
            //   arrive after an event it authorized, and once it lands the same event applies.
            // Known but REVOKED         -> permanent, and deliberately so. An admin revoking a peer
            //   is an answer, not a missing precondition. Deferring would keep the revoked node's
            //   backlog alive for the whole retry budget and let it apply in full if the peer were
            //   re-added inside that window — quietly resurrecting writes the revocation was meant
            //   to discard.
            var revoked = await whitelistRepo.GetByNodeIdAsync(evt.NodeId, includeDeleted: true);
            if (revoked != null)
            {
                logger.LogWarning("Event {EventId} ({Type}) rejected: originator {NodeId} is revoked",
                    evt.EventId, evt.EventType, evt.NodeId);
                throw new UnauthorizedAccessException($"Node {evt.NodeId} is revoked.");
            }

            logger.LogWarning("Event {EventId} ({Type}) rejected: originator {NodeId} not in local whitelist (relay drop? or whitelist_add still in flight)",
                evt.EventId, evt.EventType, evt.NodeId);
            throw new OriginatorNotWhitelistedException(evt.NodeId);
        }

        var sigPayload = EventSignature.BuildPayload(evt);
        if (!Ed25519Signer.Verify(node.Ed25519PublicKey, sigPayload, evt.Signature))
            throw new InvalidDataException($"Invalid Ed25519 signature for event {evt.EventId}.");

        logger.LogInformation("Applying event {EventId} {Type} from {NodeId} (lamport={Ts})",
            evt.EventId, evt.EventType, evt.NodeId, evt.LamportTs);

        // TASK 2: Override untrusted actor fields with local node info
        evt.ActorName = node.DisplayName ?? $"node:{evt.NodeId.ToString()[..8]}";
        evt.ActorType = "remote-peer";

        // Same treatment for EntityId, and for the same reason: it rides along on the wire but is
        // not covered by the signature, so a relaying peer can rewrite it on an event that still
        // verifies. See EventEntityId for the full argument — the short version is that the
        // hard-delete gate below looks entities up by this value, so an attacker who can blank it
        // resurrects deleted content, and one who can point it at a hard-deleted id makes innocent
        // events vanish. Derive it from the signed fields instead of believing the sender.
        var transportedEntityId = evt.EntityId;
        evt.EntityId = EventEntityId.Derive(evt);
        if (!string.IsNullOrEmpty(transportedEntityId) && transportedEntityId != evt.EntityId)
        {
            // Not an error: an older peer may legitimately have computed this differently, and the
            // derived value is authoritative either way. Worth a line in the log, because a
            // mismatch is also exactly what tampering looks like.
            logger.LogWarning(
                "Event {EventId} ({Type}) from {NodeId} carried entity id {Transported}, using derived {Derived}",
                evt.EventId, evt.EventType, evt.NodeId, transportedEntityId, evt.EntityId ?? "(none)");
        }

        // Hard-delete gate: if entity was hard-deleted at this or later timestamp, skip.
        var identifier = evt.EntityId;
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
            || evt.EventType == EventTypes.RestoreNetwork
            // Not cluster-state, but it raises a security notice in the admin UI. A peer that could
            // plant one at will could nag an operator into re-entering the master password, which
            // is a decent phishing primitive; only nodes already trusted with everything may.
            || evt.EventType == EventTypes.MasterPasswordChanged;
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
        // so re-applying a partially-applied event is safe — PROVIDED the apply method itself never
        // leaves half of a multi-row write committed. The article create/update paths wrap their
        // row + body + concept-tag writes in one SQLite transaction for exactly this reason (H5):
        // without it, a crash between two of those writes could commit the first, and a redelivered
        // event would then tie LWW against the row that DID commit and lose — permanently stranding
        // the article half-written instead of ever healing. See EventApplier.Article.cs.
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
            case EventTypes.MasterPasswordChanged:
                await ApplyMasterPasswordChangedAsync(evt);
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
}
