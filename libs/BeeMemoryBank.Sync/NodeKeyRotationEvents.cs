using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Builds the two events of the node identity re-key (plan WS-D D1, D2). A rotation is signed by the OLD key (<paramref name="signWithOld"/>, which
/// is how the node's signer signs today); the new seed signs only its proof.
/// </summary>
public static class NodeKeyRotationEvents
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <param name="signWithOld">Signs bytes with the node's current (old) key: the statement and then the event.</param>
    /// <param name="newSeed">The new 32-byte seed; only read here, never kept.</param>
    public static (SyncEvent Event, NodeKeyHistoryEntry Entry) Rotation(Guid nodeId, byte[] oldPublicKey, Func<byte[], byte[]> signWithOld,
        byte[] newSeed, long counter, long lamportTs, long checkpointLamportTs, IEnumerable<Guid> ownEventIds, string? reason, int protocolVersion,
        string actorName)
    {
        var newPublicKey = NodeIdentityCrypto.PublicKeyOf(newSeed);
        var statement = NodeKeyTransition.Statement(nodeId, oldPublicKey, newPublicKey, counter);
        var newProof = Ed25519Signer.Sign(newSeed, statement);
        var oldProof = signWithOld(statement);
        var packed = EventIdSet.Pack(ownEventIds);
        var payload = JsonSerializer.Serialize(new NodeKeyRotatedPayload(
            nodeId.ToString("D"), Convert.ToBase64String(oldPublicKey), Convert.ToBase64String(newPublicKey), counter,
            DateTime.UtcNow.ToString("O"), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), Convert.ToBase64String(newProof),
            Convert.ToBase64String(oldProof), checkpointLamportTs, packed), Json);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = nodeId,
            LamportTs = lamportTs,
            EventType = EventTypes.NodeKeyRotated,
            EntityId = EventEntityId.Derive(EventTypes.NodeKeyRotated, null, payload),
            Payload = payload,
            Signature = [],
            ProtocolVersion = protocolVersion,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = actorName,
        };
        evt.Signature = signWithOld(EventSignature.BuildPayload(evt));
        var entry = new NodeKeyHistoryEntry(nodeId, counter, oldPublicKey, newPublicKey, lamportTs, evt.EventId, checkpointLamportTs, packed,
            oldProof, newProof);
        return (evt, entry);
    }

    /// <summary>A superadmin's confirm of another node's rotation, signed with the confirming node's current key.</summary>
    public static SyncEvent Confirm(Guid signerNodeId, Func<byte[], byte[]> sign, Guid subject, Guid rotationEventId, long counter,
        byte[] confirmedPublicKey, long lamportTs, int protocolVersion, string actorName)
    {
        var payload = JsonSerializer.Serialize(new NodeKeyConfirmPayload(subject.ToString("D"), rotationEventId.ToString("D"), counter,
            Convert.ToBase64String(confirmedPublicKey)), Json);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = signerNodeId,
            LamportTs = lamportTs,
            EventType = EventTypes.NodeKeyConfirm,
            EntityId = EventEntityId.Derive(EventTypes.NodeKeyConfirm, null, payload),
            Payload = payload,
            Signature = [],
            ProtocolVersion = protocolVersion,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = actorName,
        };
        evt.Signature = sign(EventSignature.BuildPayload(evt));
        return evt;
    }
}
