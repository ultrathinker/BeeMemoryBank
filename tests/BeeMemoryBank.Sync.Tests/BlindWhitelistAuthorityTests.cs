using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A blind node is never a superadmin on a receiving node, whatever a replicated whitelist event says
/// (plan 3.2, BMB-42) — even when the event comes from a genuine superadmin peer that the local
/// superadmin gate accepts. The ordinary promotion still applies, so the refusal is about the blind
/// mark, not the sender.
/// </summary>
public class BlindWhitelistAuthorityTests : IAsyncLifetime
{
    private SyncTestFixture _receiver = null!;
    private SyncTestFixture _admin = null!; // a superadmin in the receiver's whitelist

    public async Task InitializeAsync()
    {
        _receiver = new ConcreteFixture();
        await _receiver.InitializeAsync();
        await _receiver.InitService.InitializeAsync("admin", "Receiver", "pass");
        await _receiver.Session.UnlockAsync("pass");

        _admin = new ConcreteFixture();
        await _admin.InitializeAsync();
        await _admin.InitService.InitializeAsync("admin", "Admin", "pass");
        await _admin.Session.UnlockAsync("pass");

        var admin = (await _admin.NodeRepo.GetAsync())!;
        await _receiver.WhitelistRepo.CreateAsync(Row(admin.NodeId, admin.Ed25519PublicKey, isSuperadmin: true));
    }

    public async Task DisposeAsync()
    {
        await _receiver.DisposeAsync();
        await _admin.DisposeAsync();
    }

    [Fact]
    public async Task WhitelistAdd_MarkingABlindNodeSuperadmin_RecordsItAsAPlainPeer()
    {
        var blind = BlindNodeId.NewId();
        await _admin.EventLogger.LogWhitelistAddAsync(Row(blind, Ed25519Signer.GenerateKeyPair().publicKey, isSuperadmin: true));

        await _receiver.ApplyFromAsync(_admin, await LastEventAsync(EventTypes.WhitelistAdd));

        var row = await _receiver.WhitelistRepo.GetByNodeIdAsync(blind);
        row.Should().NotBeNull("the blind node itself is still a member");
        row!.IsSuperadmin.Should().BeFalse("a blind node is never a superadmin, whatever the event says");
    }

    [Fact]
    public async Task WhitelistUpdate_RaisingABlindNode_IsIgnored_WhileAnOrdinaryPromotionApplies()
    {
        var blind = BlindNodeId.NewId();
        var ordinary = Guid.NewGuid();
        await _receiver.WhitelistRepo.CreateAsync(Row(blind, Ed25519Signer.GenerateKeyPair().publicKey, isSuperadmin: false));
        await _receiver.WhitelistRepo.CreateAsync(Row(ordinary, Ed25519Signer.GenerateKeyPair().publicKey, isSuperadmin: false));

        await _admin.EventLogger.LogWhitelistUpdateAsync(blind, null, null, isSuperadmin: true);
        await _receiver.ApplyFromAsync(_admin, await LastEventAsync(EventTypes.WhitelistUpdate));
        await _admin.EventLogger.LogWhitelistUpdateAsync(ordinary, null, null, isSuperadmin: true);
        await _receiver.ApplyFromAsync(_admin, await LastEventAsync(EventTypes.WhitelistUpdate));

        (await _receiver.WhitelistRepo.GetByNodeIdAsync(blind))!.IsSuperadmin.Should().BeFalse();
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(ordinary))!.IsSuperadmin.Should().BeTrue(
            "the same sender's promotion of an ordinary node must still apply");
    }

    private async Task<SyncEvent> LastEventAsync(string type)
    {
        var evt = (await _admin.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        evt.EventType.Should().Be(type);
        return evt;
    }

    private static WhitelistEntry Row(Guid nodeId, byte[] publicKey, bool isSuperadmin) => new()
    {
        NodeId = nodeId,
        DisplayName = nodeId.ToString("N")[..6],
        Ed25519PublicKey = publicKey,
        Status = "A",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        IsSuperadmin = isSuperadmin
    };

    private class ConcreteFixture : SyncTestFixture { }
}
