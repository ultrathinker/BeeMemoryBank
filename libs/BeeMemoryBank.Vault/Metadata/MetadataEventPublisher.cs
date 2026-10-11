using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// Writes this node's own metadata control events (docs/design/step-a-spec.md 4.3, 4.4, 5.4): a new MEK generation and the
/// activation. Signs one, stores it through the same applier rules a peer's copy meets, then logs it for sync, the order
/// <c>RecoveryEventPublisher</c> uses: a crash in between leaves a row nobody was told about, never an event whose effect
/// this node itself never recorded.
/// </summary>
public sealed class MetadataEventPublisher(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ILamportClock clock,
    SessionService session,
    EventApplier applier,
    ISyncTrigger syncTrigger,
    ISyncProtocolPolicy policy)
{
    /// <summary>
    /// Creates MEK generation <paramref name="generation"/> under the session's current master DEK and publishes its
    /// metadata_key_set. The MEK itself is never kept: the key ring opens the row like any other.
    /// </summary>
    public async Task<MetadataKeyRow> PublishNewKeyAsync(int generation)
    {
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var dek = session.GetMasterDek();
        MetadataKeyRow row;
        try
        {
            var (mek, created) = MetadataKeyCrypto.Create(generation, dek);
            CryptographicOperations.ZeroMemory(mek);
            row = created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
        await PublishAsync(identity, EventTypes.MetadataKeySet, new MetadataKeySetPayload(row.KeyId, row.Generation,
            row.DekFingerprint, identity.DekEpoch, Convert.ToBase64String(row.Wrapped), Convert.ToBase64String(row.Iv),
            DateTime.UtcNow.ToString("O")));
        return row;
    }

    /// <summary>Publishes metadata_activation naming <paramref name="key"/>; applying it switches this node (spec 5.4).</summary>
    public async Task<SyncEvent> PublishActivationAsync(Guid activationId, MetadataKeyInfo key)
    {
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        return await PublishAsync(identity, EventTypes.MetadataActivation, new MetadataActivationPayload(
            activationId.ToString("D"), key.KeyId, key.Generation, DateTime.UtcNow.ToString("O")));
    }

    private async Task<SyncEvent> PublishAsync(NodeIdentity identity, string eventType, object payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType());
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = eventType,
            Payload = payloadJson,
            Signature = [],
            // A control event, clear at every protocol; stamped with what this node declares now (3 at activation).
            ProtocolVersion = policy.Declared,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, session.GetMasterDek, EventSignature.BuildPayload(evt));
        await applier.ApplyOwnMetadataEventAsync(evt);
        await eventLogRepo.AppendAsync(evt);
        syncTrigger.Signal();
        return evt;
    }
}
