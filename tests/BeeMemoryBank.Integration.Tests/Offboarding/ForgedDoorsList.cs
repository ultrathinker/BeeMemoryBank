using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.SharedDoors;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The forged doors list: somebody with a stolen backup of the accounts node holds its identity seed and the people's door
/// descriptors; they build a <c>superadmin_doors_set</c> "of the accounts node" with every real person, a door of their own (a password they chose)
/// and a huge version, sealed to a shared node and signed with the accounts node's key.
/// </summary>
internal static class ForgedDoorsList
{
    public static async Task<SyncEvent> BuildAsync(BmbWebApplicationFactory accounts, byte[] accountsSeed, BmbWebApplicationFactory shared,
        long version, string plantedUser, string plantedPassword)
    {
        Guid accountsId, sharedId;
        byte[] sharedPub;
        SyncEvent real;
        long lamport;
        using (var scope = accounts.Services.CreateScope())
        {
            accountsId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            real = (await log.GetAllAfterSequenceAsync(0, 5000)).Where(e => e.EventType == EventTypes.SuperadminDoorsSet && e.NodeId == accountsId)
                .OrderByDescending(e => e.LamportTs).First();
            lamport = await log.GetMaxLamportTimestampAsync() + 10;
        }
        using (var scope = shared.Services.CreateScope())
        {
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            sharedId = identity.NodeId;
            sharedPub = identity.Ed25519PublicKey;
        }
        var sharedSeed = await RemovalWiringTests.SeedAsync(shared);
        var realDoc = SuperadminDoorsCrypto.TryDecryptForNode(JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(real.Payload)!, real.EventId,
            accountsId, sharedId, sharedSeed)!;
        Array.Clear(sharedSeed);

        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(plantedPassword, salt);
        byte[] doorPk;
        using (var pair = DoorBox.DeriveKeyPair(kek, salt)) doorPk = (byte[])pair.PublicKey.Clone();
        Array.Clear(kek);
        var planted = new SuperadminDoorDescriptor(Guid.NewGuid(), plantedUser, "Planted", null, Convert.ToBase64String(salt),
            CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, Convert.ToBase64String(doorPk));
        var doc = new SuperadminDoorsDocument(version, DateTime.UtcNow.ToString("O"), [.. realDoc.People, planted]);

        var eventId = Guid.NewGuid();
        var listKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(doc);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length + 16];
        using (var aes = new System.Security.Cryptography.AesGcm(listKey, 16))
            aes.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length), SuperadminDoorsCrypto.BuildListAad(version, accountsId));
        var envs = DekEnvelope.Build(listKey, eventId, [new DekEnvelope.Recipient(sharedId, sharedPub)]);
        Array.Clear(listKey);
        var payload = new SuperadminDoorsSetPayload(version, doc.IssuedAt, Convert.ToBase64String(ct), Convert.ToBase64String(nonce),
            new DekEnvelopesPayload(envs.EphemeralPublicKeyB64, envs.Peers.ToDictionary(k => k.Key, k => new DekEnvelopeBox(k.Value.WrappedB64, k.Value.NonceB64))));
        var evt = new SyncEvent
        {
            EventId = eventId, NodeId = accountsId, LamportTs = lamport, EventType = EventTypes.SuperadminDoorsSet,
            Payload = JsonSerializer.Serialize(payload), Signature = [], ProtocolVersion = real.ProtocolVersion, CreatedAt = DateTime.UtcNow,
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(accountsSeed, EventSignature.BuildPayload(evt));
        return evt;
    }

    /// <summary>The forged list reaches the shared node (relayed by a device that still syncs with it) and is applied there.</summary>
    public static async Task DeliverAsync(BmbWebApplicationFactory shared, SyncEvent forged, Guid relayedBy)
    {
        using var scope = shared.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(forged,
            new BeeMemoryBank.Sync.Metadata.ApplyContext(ReceivedFrom: relayedBy, ReceivedSeq: 1));
        await scope.ServiceProvider.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
    }
}
