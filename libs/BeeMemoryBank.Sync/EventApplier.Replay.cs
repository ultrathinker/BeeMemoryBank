using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// A stored event that never passed a Live check, signed by a retired identity key of its author that the author's rotation does not acknowledge
///: never applied. <see cref="LateEventReplay"/> quarantines the row and raises the retired-key attention flag.
/// </summary>
public sealed class RetiredKeyUnacknowledgedException(Guid eventId, Guid nodeId)
    : UnauthorizedAccessException($"Stored event {eventId} is signed by a retired identity key of node {nodeId} that the node's rotation does not acknowledge.");

public partial class EventApplier
{
    /// <summary>
    /// Applies again an event this node stored earlier without applying it - its type was unknown to the build that received it
    /// (<see cref="LateEventReplay"/>). Through the same checks as a delivery, except the idempotency shortcut (the row is in the log on
    /// purpose): the blind-node invariants, the originator as the whitelist knows it NOW, its signature, the wire rules, the opener, then the
    /// handler. The row itself is not written again. Handlers are idempotent, so a replay of an event that did apply changes nothing. Throws as a
    /// delivery would; a deferrable failure is retried by the caller later.
    /// <para>
    /// The signature rule: with <paramref name="liveCheck"/> (an event a 2.10 or 2.11 build stored) the Live rule - that build
    /// ran no Live check, so the event never earned the package rule; a retired key signs it only above the rotation's checkpoint and on its list,
    /// and an event only the package rule would admit throws <see cref="RetiredKeyUnacknowledgedException"/>. Without it (an event a 2.12 or later
    /// build stored as unknown, after its Live check at receipt) the rule for an event the log already holds
    /// (<see cref="NodeEventVerifier.ReceiptModeAsync"/>: the package rule, so an event signed before its author re-keyed still verifies).
    /// </para>
    /// </summary>
    public async Task<EventApplyResult> ReplayStoredAsync(SyncEvent evt, CancellationToken ct = default, bool liveCheck = false)
    {
        using var _ = await EventWriteGate.Instance.EnterAsync(ct);
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();

        if (EventInvariants.Violation(evt) is { } violation)
            throw new UnauthorizedAccessException(violation);
        var originator = await ResolveOriginatorAsync(evt, null);
        var context = new ApplyContext(KeyMode: liveCheck ? NodeKeyMode.Live : await NodeEventVerifier.ReceiptModeAsync(eventLogRepo, evt));
        if (!await VerifyOriginatorSignatureAsync(evt, originator, context))
        {
            if (liveCheck && await VerifyOriginatorSignatureAsync(evt, originator, new ApplyContext(KeyMode: NodeKeyMode.Package)))
                throw new RetiredKeyUnacknowledgedException(evt.EventId, evt.NodeId);
            throw new InvalidDataException($"Invalid Ed25519 signature for stored event {evt.EventId}.");
        }
        if (SealedEnvelope.WireViolation(evt) is { } malformed)
            throw new InvalidDataException($"Event {evt.EventId}: {malformed}");

        var outcome = await envelopeOpener.OpenAsync(evt, null, ct);
        switch (outcome.Disposition)
        {
            case EnvelopeDisposition.Relay:
            case EnvelopeDisposition.Drop:
                return EventApplyResult.Applied;
            case EnvelopeDisposition.Hold:
                // A sealed event that does not open now (a locked vault): the replay waits for the next start.
                throw new SessionLockedException("The vault is locked: the stored event is applied at a later start.");
        }
        var view = outcome.View ?? evt;
        logger.LogInformation("Replaying stored event {EventId} {Type} from {NodeId} (lamport={Ts})", evt.EventId, view.EventType, evt.NodeId, evt.LamportTs);
        return await ApplyViewAsync(evt, view, originator, receipt: null, heldSequence: null);
    }
}
