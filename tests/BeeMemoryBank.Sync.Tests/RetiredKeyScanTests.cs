using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The D3 scan a receiver runs when it applies a node's identity re-key: events of that node it already took, signed by the retired key and not
/// on the rotation's own-event list, raise <c>retired_key_unacknowledged_events</c>. Types the author prunes
/// from its own log on its own (here a recovery box event it retired) can never be on the list, so they must not count.
/// </summary>
public class RetiredKeyScanTests : SyncTestFixture
{
    private Guid _peer;
    private byte[] _peerPub = null!;
    private byte[] _peerSeed = null!;
    private long _lamport = 100;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "Receiver", Password);
        (_peerPub, _peerSeed) = Ed25519Signer.GenerateKeyPair();
        _peer = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peer, DisplayName = "Office", Ed25519PublicKey = _peerPub, Status = "A", IsSuperadmin = true, Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private SyncEvent SignedByTheOldKey(string type)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _peer, LamportTs = ++_lamport, EventType = type, Payload = JsonSerializer.Serialize(new { x = 1 }),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow, Signature = [],
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(_peerSeed, EventSignature.BuildPayload(evt));
        return evt;
    }

    [Fact]
    public async Task A_type_its_author_prunes_on_its_own_is_not_counted_and_any_other_unlisted_event_is()
    {
        // Taken before the re-key: a recovery box event the author retired (and purged from its own log) before it re-keyed, and an event of a
        // type nobody prunes. The rotation lists neither (the author no longer holds the first; the second it never wrote).
        await EventLogRepo.AppendIfNotExistsAsync(SignedByTheOldKey(EventTypes.RecoveryBoxSet), new EventRowLocal());
        await EventLogRepo.AppendIfNotExistsAsync(SignedByTheOldKey(EventTypes.ConceptTagRename), new EventRowLocal());
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (rotation, _) = NodeKeyRotationEvents.Rotation(_peer, _peerPub, b => Ed25519Signer.Sign(_peerSeed, b), newSeed, 1, ++_lamport, 0, [],
            "test", SyncProtocolVersion.Current, "Office");

        await EventApplier.ApplyAsync(rotation);

        var marks = await RetiredKeyAttention.ListAsync(Factory);
        marks.Should().ContainSingle(m => m.NodeId == _peer).Which.Count.Should().Be(1, "only the event of a type nobody prunes counts");
    }
}
