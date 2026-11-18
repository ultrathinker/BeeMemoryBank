using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Sync.Tests;

public class NodeStateTests : SyncTestFixture
{
    private Guid _peerId;
    private byte[] _peerPriv = null!;
    private byte[] _peerPub = null!;
    private Guid _blindId;
    private byte[] _blindPriv = null!;
    private byte[] _blindPub = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (_peerPub, _peerPriv) = Ed25519Signer.GenerateKeyPair();
        _peerId = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Peer", Ed25519PublicKey = _peerPub, Status = "A",
            IsSuperadmin = false, Ownership = OwnershipKinds.Shared, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        (_blindPub, _blindPriv) = Ed25519Signer.GenerateKeyPair();
        _blindId = BlindNodeId.NewId();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _blindId, DisplayName = "Blind", Ed25519PublicKey = _blindPub, Status = "A",
            IsSuperadmin = false, Ownership = OwnershipKinds.Shared, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private static SyncEvent SignedNodeState(Guid author, byte[] priv, long lamport, NodeStatePayload payload, Guid? eventId = null)
    {
        var evt = new SyncEvent
        {
            EventId = eventId ?? Guid.NewGuid(),
            NodeId = author,
            LamportTs = lamport,
            EventType = EventTypes.NodeState,
            Payload = JsonSerializer.Serialize(payload),
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
            Signature = []
        };
        evt.Signature = Ed25519Signer.Sign(priv, EventSignature.BuildPayload(evt));
        return evt;
    }

    [Fact]
    public async Task AuthorMismatch_IsRejectedByInvariantAndApplier()
    {
        var other = Guid.NewGuid();
        var payload = new NodeStatePayload(other.ToString("D"), "2.12.0", "linux", NodeRuntimeKinds.Server,
            NodeStateDraft.IdentityFingerprint(_peerPub), null);
        var evt = SignedNodeState(_peerId, _peerPriv, 10, payload);

        EventInvariants.Violation(evt).Should().NotBeNull();
        var act = () => EventApplier.ApplyAsync(evt);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await EventLogRepo.ExistsAsync(evt.EventId)).Should().BeFalse();
    }

    [Fact]
    public async Task BlindNode_AllowedWithoutDekFp_AndRefusedWithDekFp()
    {
        var okPayload = new NodeStatePayload(_blindId.ToString("D"), "2.12.0", "linux", NodeRuntimeKinds.Blind,
            NodeStateDraft.IdentityFingerprint(_blindPub), null, ["low-disk"]);
        var okEvt = SignedNodeState(_blindId, _blindPriv, 10, okPayload);

        EventInvariants.Violation(okEvt).Should().BeNull();
        (await EventApplier.ApplyAsync(okEvt)).Should().Be(EventApplyResult.Applied);

        var store = new NodeStateStore(Factory);
        var stored = await store.GetAsync(_blindId);
        stored.Should().NotBeNull();
        stored!.RuntimeKind.Should().Be(NodeRuntimeKinds.Blind);
        stored.DekFp.Should().BeNull();
        stored.AttentionFlags.Should().ContainSingle("low-disk");

        var badPayload = okPayload with { DekFp = new string('a', 64) };
        var badEvt = SignedNodeState(_blindId, _blindPriv, 11, badPayload);
        EventInvariants.Violation(badEvt).Should().NotBeNull();
        var act = () => EventApplier.ApplyAsync(badEvt);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task OutOfOrderDelivery_NewestLamportOrEventIdWins()
    {
        var fp = NodeStateDraft.IdentityFingerprint(_peerPub);
        var newer = SignedNodeState(_peerId, _peerPriv, 20, new NodeStatePayload(_peerId.ToString("D"), "2.12.1", "linux", NodeRuntimeKinds.Server, fp, "dek2"));
        var older = SignedNodeState(_peerId, _peerPriv, 15, new NodeStatePayload(_peerId.ToString("D"), "2.12.0", "linux", NodeRuntimeKinds.Desktop, fp, "dek1"));

        await EventApplier.ApplyAsync(newer);
        await EventApplier.ApplyAsync(older);

        var store = new NodeStateStore(Factory);
        var stored = await store.GetAsync(_peerId);
        stored.Should().NotBeNull();
        stored!.AppVersion.Should().Be("2.12.1");
        stored.DekFp.Should().Be("dek2");
        stored.LamportTs.Should().Be(20);
    }

    [Fact]
    public void Capabilities_AdvertiseOwnershipAndNodeState_AndNeverWithholdNodeState()
    {
        MetadataCapabilities.Advertised.Should().Contain(MetadataCapabilities.OwnershipV1);
        MetadataCapabilities.Advertised.Should().Contain(MetadataCapabilities.NodeStateV1);
        MetadataCapabilities.IsWithheldFrom(EventTypes.NodeState, [MetadataCapabilities.EnvelopeV1]).Should().BeFalse();
        MetadataCapabilities.IsWithheldFrom(EventTypes.NodeState, []).Should().BeFalse();
        MetadataCapabilities.IsWithheldFrom(EventTypes.NodeEndpoints, [MetadataCapabilities.EnvelopeV1]).Should().BeTrue();
    }

    private sealed class StubContributor(Func<NodeStateDraft> build) : INodeStateContributor
    {
        public Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
        {
            var src = build();
            draft.AppVersion = src.AppVersion;
            draft.Platform = src.Platform;
            draft.RuntimeKind = src.RuntimeKind;
            draft.IdentityKeyFp = src.IdentityKeyFp;
            draft.DekFp = src.DekFp;
            draft.AttentionFlags.Clear();
            draft.AttentionFlags.AddRange(src.AttentionFlags);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuthSigner(byte[] privKey) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] payload) => Ed25519Signer.Sign(privKey, payload);
    }

    [Fact]
    public async Task Publisher_SignsWithNodeAuthSigner_AndUpdatesLocalNodeStateTable()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var customIdentity = new NodeIdentity
        {
            NodeId = Guid.NewGuid(),
            DisplayName = "LocalTestNode",
            Ed25519PublicKey = pub,
            Ed25519PrivateKey = [],
            CreatedAt = DateTime.UtcNow,
        };
        await NodeRepo.CreateAsync(customIdentity);

        var fp = NodeStateDraft.IdentityFingerprint(pub);
        var publisher = new NodeStatePublisher(
            NodeRepo,
            EventLogRepo,
            Clock,
            new StubAuthSigner(priv),
            EventApplier,
            new SyncTrigger(),
            ProtocolPolicy,
            [new StubContributor(() => new NodeStateDraft
            {
                AppVersion = "2.12.0",
                Platform = NodePlatform.Linux,
                RuntimeKind = NodeRuntimeKinds.Server,
                IdentityKeyFp = fp,
                DekFp = "dek-v1",
            })]);

        var draft = (await publisher.BuildDraftAsync())!;
        var evt = await publisher.PublishAsync(draft);

        evt.EventType.Should().Be(EventTypes.NodeState);
        EventClasses.Control.Should().Contain(EventTypes.NodeState);
        var stored = await new NodeStateStore(Factory).GetAsync(customIdentity.NodeId);
        stored.Should().NotBeNull();
        stored!.RuntimeKind.Should().Be(NodeRuntimeKinds.Server);
        stored.DekFp.Should().Be("dek-v1");
    }

    [Fact]
    public async Task UnknownEventType_IsStoredInEventLog_ForForwardCompatRelay()
    {
        var unknown = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = _peerId,
            LamportTs = 25,
            EventType = "future_node_telemetry_v9",
            Payload = """{"hello":"world"}""",
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
            Signature = [],
        };
        unknown.Signature = Ed25519Signer.Sign(_peerPriv, EventSignature.BuildPayload(unknown));

        (await EventApplier.ApplyAsync(unknown)).Should().Be(EventApplyResult.Applied);
        var pulled = await EventLogRepo.GetAfterSequenceAsync(0, 100);
        pulled.Should().ContainSingle(e => e.EventId == unknown.EventId && e.EventType == "future_node_telemetry_v9");
    }
}
