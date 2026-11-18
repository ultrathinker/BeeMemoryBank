using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.12: the settings of the phone apps show whose device this is - read from the whitelist events other nodes wrote about it, since a node
/// has no row of its own - and only when the bank has more than one person who holds the key (a one-person bank is never shown it).
/// </summary>
public class DeviceOwnerLineTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _node = null!;
    private Guid _self;
    private long _lamport = 10;

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "Anna's phone", "pass");
        _self = (await _node.NodeRepo.GetAsync())!.NodeId;
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    /// <summary>A whitelist event another node wrote about this one, as the applier stores it.</summary>
    private async Task EventAboutSelfAsync(string eventType, string? ownership, Guid? person, string? label, Guid? author = null)
    {
        object payload = eventType == EventTypes.WhitelistAdd
            ? new WhitelistAddPayload(_self, "Anna's phone", Convert.ToBase64String(new byte[32]), null, false, true, Ownership: ownership, OwnerPersonId: person, OwnerLabel: label)
            : new WhitelistUpdatePayload(_self, null, null, Ownership: ownership, OwnerPersonId: person, OwnerLabel: label);
        using var conn = _node.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at, apply_state) " +
            "VALUES (@id, @author, @lamport, @type, @payload, X'00', @now, @applied)",
            new
            {
                id = Guid.NewGuid().ToString(), author = (author ?? Guid.NewGuid()).ToString(), lamport = ++_lamport, type = eventType,
                payload = JsonSerializer.Serialize(payload, payload.GetType()), now = DateTime.UtcNow.ToString("O"), applied = EventApplyStates.Applied,
            });
    }

    private async Task PeerAsync(string ownership, Guid? person, bool superadmin = true)
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "laptop", Ed25519PublicKey = new byte[32], Status = "A", IsSuperadmin = superadmin,
            Ownership = ownership, OwnerPersonId = person, OwnerLabel = person is null ? null : "someone",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private Task<DeviceOwnerLine?> ReadAsync() => DeviceOwnerLine.ReadAsync(_node.Factory, _node.WhitelistRepo, _self);

    [Fact]
    public async Task With_two_people_holding_the_key_a_personal_device_shows_its_owner()
    {
        var anna = Guid.NewGuid();
        await EventAboutSelfAsync(EventTypes.WhitelistAdd, OwnershipKinds.Personal, anna, "Anna");
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());

        (await ReadAsync()).Should().Be(new DeviceOwnerLine(OwnershipKinds.Personal, "Anna"));
    }

    [Fact]
    public async Task A_one_person_bank_is_never_shown_whose_its_devices_are()
    {
        var anna = Guid.NewGuid();
        await EventAboutSelfAsync(EventTypes.WhitelistAdd, OwnershipKinds.Personal, anna, "Anna");
        await PeerAsync(OwnershipKinds.Personal, anna);
        await PeerAsync(OwnershipKinds.Shared, null);
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid(), superadmin: false);   // a blind copy of an ordinary user: not a person who holds the key

        (await ReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_newest_event_wins_and_a_shared_device_says_so()
    {
        await EventAboutSelfAsync(EventTypes.WhitelistAdd, OwnershipKinds.Personal, Guid.NewGuid(), "Anna");
        await EventAboutSelfAsync(EventTypes.WhitelistUpdate, OwnershipKinds.Shared, null, null);
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());

        (await ReadAsync()).Should().Be(new DeviceOwnerLine(OwnershipKinds.Shared, null));
    }

    [Fact]
    public async Task Nothing_is_shown_when_nothing_or_unknown_is_known_or_only_this_node_said_it()
    {
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());
        (await ReadAsync()).Should().BeNull("no event says anything about this node");

        await EventAboutSelfAsync(EventTypes.WhitelistAdd, OwnershipKinds.Personal, Guid.NewGuid(), "Me", author: _self);
        (await ReadAsync()).Should().BeNull("a node may not say whose it is itself");

        await EventAboutSelfAsync(EventTypes.WhitelistUpdate, OwnershipKinds.Unknown, null, null);
        (await ReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_long_label_is_shortened_for_the_settings_row()
    {
        await EventAboutSelfAsync(EventTypes.WhitelistAdd, OwnershipKinds.Personal, Guid.NewGuid(), new string('A', 300));
        await PeerAsync(OwnershipKinds.Personal, Guid.NewGuid());

        (await ReadAsync())!.OwnerLabel!.Length.Should().Be(83);
    }
}
