using System.Text.Json;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public enum EventApplyResult
{
    Applied,
    SilentlyDropped,
    Skipped,
    /// <summary>
    /// Stored durably, not applied yet (docs/design/step-a-spec.md 6.2): the vault is locked, the event's key is not here, or
    /// an earlier row is held. Every caller treats it as accepted: the event is here, and the drain applies it later.
    /// </summary>
    Held
}

/// <summary>What <see cref="EventApplier.ApplyHeldAsync"/> did with one held row (spec 6.3, 6.7).</summary>
public enum HeldApplyOutcome
{
    /// <summary>Applied (or settled by a gate that drops it, as an unlocked node would have).</summary>
    Applied,
    /// <summary>Still cannot apply now (locked, key missing, an earlier row held, a deferrable failure): the drain stops here.</summary>
    StillHeld,
    /// <summary>Does not open with the key this node holds, or a handler refused it for good: state 3, the drain goes on.</summary>
    Quarantined,
    /// <summary>The row is not held (already applied by another drain, or not there).</summary>
    NotHeld,
}

public sealed record HeldApplyResult(HeldApplyOutcome Outcome, string? HoldReason = null, Exception? Failure = null);

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
    ILogger<EventApplier> logger,
    // Step A (docs/design/step-a-spec.md 5.6, 6.7, 5.5): the protocol this node declares, the drain of held events and the
    // hosts' effects of the activation. AddNodeSync registers the first two on every host; observers are optional.
    ISyncProtocolPolicy protocolPolicy,
    IHeldEventDrain heldEventDrain,
    IEnumerable<IMetadataActivationObserver> activationObservers,
    // What this host does with a verified event (spec 6.3): a full node opens sealed events (the Vault's opener), every blind
    // host relays them. There is deliberately no default: a host that registers none fails when the applier is resolved.
    IEnvelopeOpener envelopeOpener,
    // Full nodes only (spec 7.5): asked for an early baseline after a hard delete or a late protocol-3 content event.
    IBaselineScheduler? baselineScheduler = null)
{
    // whitelistRepoWrite is the same whitelist, just separated for read/write intent clarity
    // In reality it's the same object from DI

    /// <summary>Who signed an event and what this node thought of it (spec 6.5): the key, the superadmin flag, the name.</summary>
    private sealed record Originator(byte[] PublicKey, bool IsSuperadmin, string? DisplayName);

    /// <param name="context">Who delivered the event (stored as its origin), and for a restore replay the originators and
    /// event keys to use instead of this node's whitelist and key ring (spec 12).</param>
    public async Task<EventApplyResult> ApplyAsync(SyncEvent evt, ApplyContext? context = null)
    {
        // A blind reseed swaps the database file under this process: it holds applies off and waits
        // for the ones running, so none lands in the file being replaced (EventWriteGate).
        using var _ = await EventWriteGate.Instance.EnterAsync();
        // Scoped to this one apply: the mark lets nested writes of the apply itself pass, and is
        // given back when the apply is done — the caller's flow waits at the gate like any other.
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();
        return await ApplyCoreAsync(evt, context);
    }

    private async Task<EventApplyResult> ApplyCoreAsync(SyncEvent evt, ApplyContext? context)
    {
        // Protocol version check. Version 1 events (ciphertext inline) are still applied: the log
        // holds them, and a peer that has not upgraded still emits them. Version 4 (sealed content,
        // clear control) is read by every 2.6 node, activated or not: gossip may deliver it first.
        if (!SyncProtocolVersion.CanApply(evt.ProtocolVersion))
            throw new NotSupportedException($"Unknown protocol version: {evt.ProtocolVersion}");

        // The blind-node rules (EventInvariants): a blind node never authors anything (plan 3.2) —
        // it holds no DEK, so nothing it could sign is content, and its key sits in a plain file on
        // a box that is easier to steal than a PC — and no rotation seals the DEK for one. Refused
        // for EVERY type, including ones this build does not know, which would otherwise be stored
        // and relayed by the default branch below; before the whitelist lookup, since an unknown
        // blind id must not be parked as "whitelist_add still in flight" (deferred, retried for
        // hours) when the answer can never change; and before the idempotency shortcut, so an event
        // a pre-fix build already stored is not waved through as "already applied".
        if (EventInvariants.Violation(evt) is { } violation)
        {
            logger.LogWarning("Event {EventId} ({Type}) from {NodeId} rejected: {Reason}",
                evt.EventId, evt.EventType, evt.NodeId, violation);
            throw new UnauthorizedAccessException(violation);
        }

        // Fast-path idempotency: if event already processed, skip.
        // Must run before the signer check so that self-echoes (a node pulling back
        // its own event from a remote) are silently skipped instead of failing the
        // whitelist lookup (a node does not whitelist itself). A row in any state counts: a held
        // or relayed event is here, and storing it twice would apply it twice.
        if (await eventLogRepo.ExistsAsync(evt.EventId)) return EventApplyResult.Applied;

        // Signature verification
        var originator = await ResolveOriginatorAsync(evt, context);
        var sigPayload = EventSignature.BuildPayload(evt);
        if (!Ed25519Signer.Verify(originator.PublicKey, sigPayload, evt.Signature))
            throw new InvalidDataException($"Invalid Ed25519 signature for event {evt.EventId}.");

        // The protocol-4 wire rules (spec 3.2, 3.7): a sealed event is protocol 4, has no article id and a well-formed
        // envelope; a content type in clear at protocol 4 is malformed. Checked by every host, a blind one included,
        // before anything is stored: a relay must not pass on what no full node will ever open.
        if (SealedEnvelope.WireViolation(evt) is { } malformed)
        {
            logger.LogWarning("Event {EventId} ({Type}) from {NodeId} is malformed: {Reason}",
                evt.EventId, evt.EventType, evt.NodeId, malformed);
            throw new InvalidDataException($"Event {evt.EventId}: {malformed}");
        }

        logger.LogInformation("Applying event {EventId} {Type} from {NodeId} (lamport={Ts})",
            evt.EventId, evt.EventType, evt.NodeId, evt.LamportTs);

        var receipt = new EventRowLocal(OriginPeer: context?.ReceivedFrom, OriginSeq: context?.ReceivedSeq);
        var outcome = await envelopeOpener.OpenAsync(evt, context);
        SyncEvent view;
        switch (outcome.Disposition)
        {
            case EnvelopeDisposition.Relay:
                // A blind host: storing and relaying the original bytes is everything it ever does with a sealed event.
                StampActor(evt, originator);
                await eventLogRepo.AppendIfNotExistsAsync(evt, receipt with { ApplyState = EventApplyStates.Relayed });
                return EventApplyResult.Applied;
            case EnvelopeDisposition.Hold:
                StampActor(evt, originator);
                await HoldAsync(evt, outcome.Reason ?? HoldReasons.Locked, originator, receipt);
                return EventApplyResult.Held;
            case EnvelopeDisposition.Drop:
                logger.LogInformation("Event {EventId} ({Type}) from {NodeId} accepted and not stored: {Reason}",
                    evt.EventId, evt.EventType, evt.NodeId, outcome.Reason);
                return EventApplyResult.SilentlyDropped;
            case EnvelopeDisposition.Opened:
                view = outcome.View ?? throw new InvalidOperationException($"The opener opened {evt.EventId} without a view.");
                break;
            default:
                view = outcome.View ?? evt;
                break;
        }

        // Content applies in log order (spec 6.4 rule 2): while any earlier row waits, this one waits behind it, a
        // protocol-3 one included. Control events never wait (rule 1): whitelist, recovery material, keys and the
        // activation apply at receipt, locked or not.
        if (EventClasses.IsSealable(view.EventType) && await eventLogRepo.AnyHeldAsync())
        {
            StampActor(evt, originator);
            await HoldAsync(evt, HoldReasons.Order, originator, receipt);
            return EventApplyResult.Held;
        }

        return await ApplyViewAsync(evt, view, originator, receipt, heldSequence: null);
    }

    /// <summary>
    /// Applies the drain's next row (spec 6.3 <c>ApplyHeldAsync</c>, 6.7), inside the same <see cref="EventWriteGate"/> as
    /// <see cref="ApplyAsync"/>. The whitelist is not asked again: the superadmin gate uses the verdict stored at receipt
    /// (spec 6.5), so a revocation applied while the row waited cannot make this node diverge from one that was unlocked.
    /// </summary>
    public async Task<HeldApplyResult> ApplyHeldAsync(long localSequence, CancellationToken ct = default)
    {
        using var _ = await EventWriteGate.Instance.EnterAsync(ct);
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();

        var row = await eventLogRepo.GetHeldAsync(localSequence);
        if (row is null) return new HeldApplyResult(HeldApplyOutcome.NotHeld);
        // Order (spec 6.4): an earlier held row goes first, whoever asks.
        if ((await eventLogRepo.GetHeldSequencesAsync(0, 1)).FirstOrDefault() is var first and > 0 && first < localSequence)
            return new HeldApplyResult(HeldApplyOutcome.StillHeld, HoldReasons.Order);

        var evt = row.Event;
        EnvelopeOutcome outcome;
        try
        {
            outcome = await envelopeOpener.OpenAsync(evt, null, ct);
        }
        catch (InvalidDataException ex)
        {
            // Relayed already, so it stays in the log; it is never applied (spec 6.3).
            await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.QuarantinedAfterReceipt);
            logger.LogWarning("Held event {EventId} does not open with the key this node holds; quarantined", evt.EventId);
            return new HeldApplyResult(HeldApplyOutcome.Quarantined, Failure: ex);
        }

        switch (outcome.Disposition)
        {
            case EnvelopeDisposition.Hold:
                var reason = outcome.Reason ?? HoldReasons.Locked;
                if (reason != row.HoldReason) await eventLogRepo.SetHoldReasonAsync(localSequence, reason);
                return new HeldApplyResult(HeldApplyOutcome.StillHeld, reason);
            case EnvelopeDisposition.Relay:
                await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.Relayed);
                return new HeldApplyResult(HeldApplyOutcome.Applied);
            case EnvelopeDisposition.Drop:
                await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.Applied);
                return new HeldApplyResult(HeldApplyOutcome.Applied);
        }

        var view = outcome.View ?? evt;
        if (!EventClasses.IsSealable(view.EventType))
        {
            await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.QuarantinedAfterReceipt);
            return new HeldApplyResult(HeldApplyOutcome.Quarantined,
                Failure: new InvalidDataException($"Held event {evt.EventId} is not a content event."));
        }

        var known = await whitelistRepo.GetByNodeIdAsync(evt.NodeId, includeDeleted: true);
        var originator = new Originator([], row.OriginSuperadmin, known?.DisplayName);
        try
        {
            var result = await ApplyViewAsync(evt, view, originator, receipt: null, heldSequence: localSequence);
            if (result == EventApplyResult.SilentlyDropped)
            {
                // A gate dropped it, as it would have on an unlocked node; the row was relayed while it waited and stays,
                // settled.
                await eventLogRepo.MarkHeldAppliedAsync(localSequence,
                    ReferenceEquals(view, evt) ? null : view.EventType, ReferenceEquals(view, evt) ? null : view.ArticleId,
                    view.EntityId, evt.ActorType, evt.ActorName);
            }
            return new HeldApplyResult(HeldApplyOutcome.Applied);
        }
        catch (SessionLockedException ex)
        {
            return new HeldApplyResult(HeldApplyOutcome.StillHeld, HoldReasons.Locked, ex);
        }
        catch (OriginatorNotSuperadminException ex)
        {
            // Deferrable at receipt (the promotion may still be travelling), but here the verdict is the one stored at
            // receipt and never changes: waiting cannot help.
            await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.QuarantinedAfterReceipt);
            return new HeldApplyResult(HeldApplyOutcome.Quarantined, Failure: ex);
        }
        catch (Exception ex) when (SyncFailureClassifier.Classify(ex) == SyncFailureKind.Deferred)
        {
            return new HeldApplyResult(HeldApplyOutcome.StillHeld, row.HoldReason, ex);
        }
        catch (Exception ex)
        {
            await eventLogRepo.SetApplyStateAsync(localSequence, EventApplyStates.QuarantinedAfterReceipt);
            logger.LogWarning("Held event {EventId} failed to apply ({Error}); quarantined", evt.EventId, ex.GetType().Name);
            return new HeldApplyResult(HeldApplyOutcome.Quarantined, Failure: ex);
        }
    }

    /// <summary>
    /// The originator's key and flags (spec 6.3 step 4): from the restore replay's own list when one is given, else this
    /// node's whitelist with today's answers for an unknown, unconfirmed or revoked node.
    /// </summary>
    private async Task<Originator> ResolveOriginatorAsync(SyncEvent evt, ApplyContext? context)
    {
        if (context?.Originators is { } originators)
        {
            if (originators(evt.NodeId) is { } info) return new Originator(info.PublicKey, info.IsSuperadmin, null);
            throw new OriginatorNotWhitelistedException(evt.NodeId);
        }

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
            // A row a restore kept unconfirmed is not an answer yet: defer, it applies once confirmed.
            if (revoked is { Status: BeeMemoryBank.Core.Models.WhitelistStatuses.Unconfirmed })
                throw new OriginatorUnconfirmedException(evt.NodeId);
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
        return new Originator(node.Ed25519PublicKey, node.IsSuperadmin, node.DisplayName);
    }

    /// <summary>Override untrusted actor fields with local node info.</summary>
    private static void StampActor(SyncEvent evt, Originator originator)
    {
        evt.ActorName = originator.DisplayName ?? $"node:{evt.NodeId.ToString()[..8]}";
        evt.ActorType = "remote-peer";
        // Strip ViaAgentName from remote events too. Otherwise an attacker could surface a misleading
        // "Security Purge Agent" string in audit logs.
        evt.ViaAgentName = null;
    }

    /// <summary>
    /// Stores a verified event held (spec 6.4, 6.5): the original bytes, which this node serves onward like any row, the
    /// reason it waits, and the receipt verdict the drain will use.
    /// </summary>
    private async Task HoldAsync(SyncEvent evt, string reason, Originator originator, EventRowLocal receipt)
    {
        evt.EntityId = null;
        await eventLogRepo.AppendIfNotExistsAsync(evt, receipt with
        {
            ApplyState = EventApplyStates.Held, HoldReason = reason, OriginSuperadmin = originator.IsSuperadmin,
        });
        logger.LogInformation("Event {EventId} from {NodeId} held ({Reason})", evt.EventId, evt.NodeId, reason);
        heldEventDrain.RequestDrain();
    }

    /// <summary>
    /// Everything after the signature and the opening, on the event as protocol 3 would carry it (spec 6.3
    /// <c>ApplyViewAsync</c>): the entity id, the hard-delete gate, the path check, the replay shield, the superadmin gate,
    /// the handler. The row written is the ORIGINAL event (the bytes that relay onward) with the view's type and article as
    /// local columns; a held row is updated in place.
    /// </summary>
    private async Task<EventApplyResult> ApplyViewAsync(SyncEvent original, SyncEvent view, Originator originator,
        EventRowLocal? receipt, long? heldSequence)
    {
        var opened = !ReferenceEquals(original, view);

        // Override untrusted actor fields with local node info
        StampActor(original, originator);
        if (opened) StampActor(view, originator);

        // Same treatment for EntityId, and for the same reason: it rides along on the wire but is
        // not covered by the signature, so a relaying peer can rewrite it on an event that still
        // verifies. See EventEntityId for the full argument — the short version is that the
        // hard-delete gate below looks entities up by this value, so an attacker who can blank it
        // resurrects deleted content, and one who can point it at a hard-deleted id makes innocent
        // events vanish. Derive it from the signed fields instead of believing the sender. A sealed
        // event's view carries none: its entity is derived from the opened fields.
        var transportedEntityId = opened ? null : view.EntityId;
        view.EntityId = EventEntityId.Derive(view);
        if (!string.IsNullOrEmpty(transportedEntityId) && transportedEntityId != view.EntityId)
        {
            // Not an error: an older peer may legitimately have computed this differently, and the
            // derived value is authoritative either way. Worth a line in the log, because a
            // mismatch is also exactly what tampering looks like.
            logger.LogWarning(
                "Event {EventId} ({Type}) from {NodeId} carried a foreign entity id; using the derived one",
                view.EventId, view.EventType, view.NodeId);
        }

        // Hard-delete gate: if entity was hard-deleted at this or later timestamp, skip. Logged by event id only: the
        // identifier can be a folder path.
        var identifier = view.EntityId;
        if (view.EventType != EventTypes.HardDelete && !string.IsNullOrEmpty(identifier))
        {
            if (await eventLogRepo.IsHardDeletedAsync(identifier, view.LamportTs))
            {
                logger.LogWarning("Event {EventId} ({Type}) refers to a hard-deleted entity, skipping",
                    view.EventId, view.EventType);
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
        if (!IsTreePathPayloadValid(view))
        {
            logger.LogWarning(
                "Event {EventId} of type {EventType} from {NodeId} has malformed tree path in payload, silently dropping",
                view.EventId, view.EventType, view.NodeId);
            return EventApplyResult.SilentlyDropped;
        }

        // Replay-shield: drop events from peers whose previous events were superseded by RESTORE
        var shieldThreshold = await replayShieldRepo.GetShieldThresholdAsync(original.NodeId.ToString());
        if (shieldThreshold.HasValue && original.LamportTs < shieldThreshold.Value)
        {
            logger.LogWarning(
                "Dropping pre-restore event {EventId} from {NodeId} (lamport_ts={LamportTs} < shield threshold {Threshold})",
                original.EventId, original.NodeId, original.LamportTs, shieldThreshold.Value);
            return EventApplyResult.SilentlyDropped;
        }

        // Shield is NOT auto-released here. A single event with forged/inflated lamport_ts could
        // bypass the shield and let subsequent zombie events (real lamport_ts < threshold) through.
        // Shield is only removed by: (a) next RESTORE_NETWORK event handler, (b) admin action, (c) compaction.

        // Authorization gate for cluster-state-modifying events. ANY whitelisted peer can
        // sign these by default; without this check a single rogue peer can revoke the
        // whole network, hard-delete arbitrary data, or trigger a destructive restore.
        //
        // The classification lives in EventAuthorization, not inline here, so it cannot drift and
        // so a newly added event type has to be deliberately classified — EventAuthorizationGuardTests
        // fails the build if any EventTypes constant is left unclassified. See that class for why
        // DEK rotation is gated elsewhere rather than here. Runs on the opened view: a sealed hard
        // delete is a hard delete.
        if (EventAuthorization.RequiresSuperadmin(view.EventType) && !originator.IsSuperadmin)
        {
            logger.LogWarning(
                "Event {EventId} ({Type}) rejected: originator {NodeId} is not superadmin in local whitelist",
                view.EventId, view.EventType, view.NodeId);
            throw new OriginatorNotSuperadminException(view.NodeId, view.EventType);
        }

        // Apply data changes BEFORE recording the event. This ensures crash safety:
        // if the process crashes during apply, the event is NOT in tbl_event,
        // so the next sync cycle will re-send it and apply will retry.
        // All apply methods are idempotent (LWW conflict resolution, existence checks),
        // so re-applying a partially-applied event is safe — PROVIDED the apply method itself never
        // leaves half of a multi-row write committed. The article create/update paths wrap their
        // row + body + concept-tag writes in one SQLite transaction for exactly this reason:
        // without it, a crash between two of those writes could commit the first, and a redelivered
        // event would then tie LWW against the row that DID commit and lose — permanently stranding
        // the article half-written instead of ever healing. See EventApplier.Article.cs.
        // A held row works the same way: applied first, then marked; a crash in between applies it
        // again from the drain.
        clock.Update(original.LamportTs);

        var evt = view;
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
            case EventTypes.RecoveryBoxSet: await ApplyRecoveryBoxSetAsync(evt); break;
            case EventTypes.RecoveryBoxRetire: await ApplyRecoveryBoxRetireAsync(evt); break;
            case EventTypes.RetiredLinkSet: await ApplyRetiredLinkSetAsync(evt); break;
            case EventTypes.StateAnchor: await ApplyStateAnchorAsync(evt); break;
            case EventTypes.SealedSecretSet: await ApplySealedSecretSetAsync(evt); break;
            case EventTypes.MetadataKeySet: await ApplyMetadataKeySetAsync(evt); break;
            case EventTypes.MetadataActivation: await ApplyMetadataActivationAsync(evt); break;
            default:
                // Skip unknown event types (forward compatibility)
                break;
        }

        // Record event AFTER successful apply. INSERT OR IGNORE handles the
        // TOCTOU race: if another process already inserted this event (concurrent apply),
        // the duplicate insert is harmlessly ignored since data changes are idempotent.
        // A recovery_box_set is the exception: its apply logs it in the same transaction as the rows, and only
        // while its box is active here, so an inactive box's material is never in the log (F7).
        // A sealed row keeps the opened view's type and article id as local columns (spec 3.8): this node's own
        // activity views read them, the wire never does.
        if (heldSequence is { } sequence)
        {
            await eventLogRepo.MarkHeldAppliedAsync(sequence, opened ? view.EventType : null, opened ? view.ArticleId : null,
                view.EntityId, original.ActorType, original.ActorName);
        }
        else if (view.EventType != EventTypes.RecoveryBoxSet)
        {
            original.EntityId = view.EntityId;
            await eventLogRepo.AppendIfNotExistsAsync(original, (receipt ?? new EventRowLocal()) with
            {
                LocalType = opened ? view.EventType : null,
                LocalArticleId = opened ? view.ArticleId : null,
            });
        }

        // Blind copies get content only sealed or inside a baseline (spec 5.7, owner decision 7): a hard delete, or a
        // protocol-1..3 content event applied after activation, makes the next baseline early.
        if (view.EventType == EventTypes.HardDelete)
            baselineScheduler?.RequestEarly(BaselineReasons.HardDelete);
        else if (EventClasses.IsClearContent(view.ProtocolVersion, view.EventType) && protocolPolicy.SealsMetadata)
            baselineScheduler?.RequestEarly(BaselineReasons.LateProtocol3);
        return EventApplyResult.Applied;
    }
}
