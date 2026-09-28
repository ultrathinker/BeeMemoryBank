using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan 4.2: the PC adds a blind node (superadmin-only whitelist_add) and the hub promotes the PC
/// (whitelist_update). A phone can receive the two in either order. When the add arrives first it
/// must wait for the promotion, not be quarantined.
/// </summary>
public class DeferredSuperadminTests : IAsyncLifetime
{
    private SyncTestFixture _hub = null!;
    private SyncTestFixture _pc = null!;
    private SyncTestFixture _phone = null!;

    public async Task InitializeAsync()
    {
        _hub = await StartAsync("Hub");
        _pc = await StartAsync("PC");
        _phone = await StartAsync("Phone");

        // The phone joined through the hub: the hub is superadmin there, the PC is an ordinary peer
        // until the hub's promotion reaches the phone.
        await TrustAsync(_phone, _hub, isSuperadmin: true);
        await TrustAsync(_phone, _pc, isSuperadmin: false);
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _pc.DisposeAsync();
        await _phone.DisposeAsync();
    }

    [Fact]
    public async Task AddBeforePromotion_WaitsAndAppliesOncePromotionArrives()
    {
        var blindId = BlindNodeId.NewId();
        var (blindPub, _) = Ed25519Signer.GenerateKeyPair();
        var now = DateTime.UtcNow;
        await _pc.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = blindId,
            DisplayName = "Blind",
            Ed25519PublicKey = blindPub,
            ApiAddress = "https://blind.lan:5610",
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
        var add = await LastEventAsync(_pc, EventTypes.WhitelistAdd);

        var pcId = (await _pc.NodeRepo.GetAsync())!.NodeId;
        await _hub.EventLogger.LogWhitelistUpdateAsync(pcId, apiAddress: null, displayName: null, isSuperadmin: true);
        var promotion = await LastEventAsync(_hub, EventTypes.WhitelistUpdate);

        // Add first: refused, but as a DEFERRED failure — the sync loop keeps retrying it.
        var early = () => _phone.EventApplier.ApplyAsync(add);
        var ex = (await early.Should().ThrowAsync<OriginatorNotSuperadminException>()).Which;
        SyncFailureClassifier.Classify(ex).Should().Be(SyncFailureKind.Deferred);
        (await _phone.WhitelistRepo.GetByNodeIdAsync(blindId)).Should().BeNull();

        // The promotion lands, and the retried add now applies.
        (await _phone.EventApplier.ApplyAsync(promotion)).Should().Be(EventApplyResult.Applied);
        (await _phone.EventApplier.ApplyAsync(add)).Should().Be(EventApplyResult.Applied);

        var row = await _phone.WhitelistRepo.GetByNodeIdAsync(blindId);
        row.Should().NotBeNull("after the promotion the phone must know the blind node");
        row!.ApiAddress.Should().Be("https://blind.lan:5610");
    }

    /// <summary>
    /// A node with a legacy plaintext (v=0) identity: it signs events without a DEK, so none of the
    /// three nodes has to run Argon2 — the process-wide gate other test classes queue on.
    /// </summary>
    private static async Task<SyncTestFixture> StartAsync(string name)
    {
        var node = new ConcreteFixture();
        await node.InitializeAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        await node.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(),
            DisplayName = name,
            Ed25519PublicKey = pub,
            Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0,
            CreatedAt = DateTime.UtcNow
        });
        return node;
    }

    private static async Task TrustAsync(SyncTestFixture host, SyncTestFixture peer, bool isSuperadmin)
    {
        var identity = (await peer.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await host.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identity.NodeId,
            DisplayName = identity.DisplayName,
            Ed25519PublicKey = identity.Ed25519PublicKey,
            Status = "A",
            IsSuperadmin = isSuperadmin,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static async Task<SyncEvent> LastEventAsync(SyncTestFixture node, string type) =>
        (await node.EventLogRepo.GetAfterSequenceAsync(0)).Last(e => e.EventType == type);

    private sealed class ConcreteFixture : SyncTestFixture { }
}
