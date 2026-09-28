using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Writes this node's own recovery events (BMB-43): signs one, stores it through the same applier rules
/// a peer's copy goes through, then logs it for sync. Every recovery publisher — device and strong
/// boxes, retires, links, anchors, sealed secrets — funnels through here, so the local rows and what the
/// mesh receives cannot disagree.
/// </summary>
public class RecoveryEventPublisher(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ILamportClock clock,
    SessionService session,
    EventApplier applier,
    ISyncTrigger syncTrigger,
    IOwnStandingProvider standing)
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <param name="dek">
    /// The current master DEK, when the caller holds it and the session may not: a slot rewrapped in
    /// the middle of an unlock is published before the session has a key. Null = the session's.
    /// </param>
    public virtual async Task<SyncEvent> PublishAsync(string eventType, object payload, byte[]? dek = null)
    {
        // The local write below goes through ApplyOwnRecoveryEventAsync, which skips the whitelist gate a
        // peer's copy meets — so the superadmin rule for anchors and retires is enforced here instead.
        if (EventAuthorization.RequiresSuperadmin(eventType) && !await standing.IsSuperadminAsync())
            throw new InvalidOperationException(
                $"{eventType} is superadmin-only, and this node is not known to be a superadmin in the network's view.");

        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = eventType,
            EntityId = EventEntityId.Derive(eventType, null, payloadJson),
            Payload = payloadJson,
            Signature = [],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = NodeIdentityCrypto.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, () => dek != null ? (byte[])dek.Clone() : session.GetMasterDek(),
            EventSignature.BuildPayload(evt));

        // Rows first, log second — the order ApplyAsync uses for a peer's event. A crash in between
        // leaves a local row nobody was told about, which the next publication supersedes; the other
        // order could ship an event whose effect this node itself never recorded.
        await applier.ApplyOwnRecoveryEventAsync(evt);
        await eventLogRepo.AppendAsync(evt);
        syncTrigger.Signal();
        return evt;
    }
}
