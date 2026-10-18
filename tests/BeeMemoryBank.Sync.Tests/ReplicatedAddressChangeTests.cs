using BeeMemoryBank.Core.Models;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307 fix round (F7): an address change that ARRIVES from another node resets what the loop learned about that peer, exactly as one made here does - the
/// failure streak, the pause, the closed window and the contact record of the old address are not the new address's (the applier tells the loop,
/// <see cref="IPeerStateResetter"/>), and a rename or a repeat of the same address does not.
/// </summary>
public class ReplicatedAddressChangeTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _origin = null!;
    private Fixture _receiver = null!;
    private readonly Guid _third = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _origin = new Fixture();
        await _origin.InitializeAsync();
        await _origin.InitService.InitializeAsync("admin", "Origin", "pass");
        await _origin.Session.UnlockAsync("pass");
        _receiver = new Fixture();
        await _receiver.InitializeAsync();
        await _receiver.InitService.InitializeAsync("admin", "Receiver", "pass");
        await _receiver.Session.UnlockAsync("pass");

        var originId = (await _origin.NodeRepo.GetAsync())!;
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = originId.NodeId, DisplayName = "Origin", Ed25519PublicKey = originId.Ed25519PublicKey, IsSuperadmin = true,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        foreach (var f in new[] { _origin, _receiver })
            await f.WhitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = _third, DisplayName = "Third", Ed25519PublicKey = new byte[32], ApiAddress = "http://old.local",
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
    }

    public async Task DisposeAsync()
    {
        await _origin.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private async Task ApplyLastAsync() =>
        await _receiver.ApplyFromAsync(_origin, (await _origin.EventLogRepo.GetAfterSequenceAsync(0)).Last());

    [Fact]
    public async Task A_replicated_address_change_tells_the_loop_to_forget_the_peer()
    {
        await _origin.EventLogger.LogWhitelistUpdateAsync(_third, "http://new.local", null);

        await ApplyLastAsync();

        (await _receiver.WhitelistRepo.GetByNodeIdAsync(_third))!.ApiAddress.Should().Be("http://new.local");
        _receiver.PeerState.Changed.Should().Equal(_third);
    }

    [Fact]
    public async Task A_replicated_rename_or_the_same_address_again_does_not()
    {
        await _origin.EventLogger.LogWhitelistUpdateAsync(_third, null, "Renamed");
        await ApplyLastAsync();
        await _origin.EventLogger.LogWhitelistUpdateAsync(_third, "HTTP://OLD.LOCAL/", null);
        await ApplyLastAsync();

        _receiver.PeerState.Changed.Should().BeEmpty("nothing about where the peer is changed");
    }
}
