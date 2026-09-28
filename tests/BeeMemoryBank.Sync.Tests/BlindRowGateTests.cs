using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The applier's superadmin gate with a POLLUTED row: the receiving node's whitelist says a blind node
/// is superadmin (an older build, a snapshot). A superadmin-only event correctly signed by that blind
/// node must still be refused (plan 3.2, BMB-42).
/// </summary>
public class BlindRowGateTests : IAsyncLifetime
{
    private SyncTestFixture _receiver = null!;

    public async Task InitializeAsync()
    {
        _receiver = new ConcreteFixture();
        await _receiver.InitializeAsync();
        await _receiver.InitService.InitializeAsync("admin", "Receiver", "pass");
        await _receiver.Session.UnlockAsync("pass");
    }

    public async Task DisposeAsync() => await _receiver.DisposeAsync();

    [Fact]
    public async Task SuperadminOnlyEvent_FromABlindNodeMarkedSuperadmin_IsRefused()
    {
        var blind = BlindNodeId.NewId();
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        var victim = Guid.NewGuid();
        using (var conn = _receiver.Factory.CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("O");
            // Written around the repository, the way a snapshot or an old build would have left it.
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
                  VALUES (@id, 'blind', @key, 'A', 1, @now, @now), (@victim, 'victim', @vkey, 'A', 0, @now, @now)",
                new { id = blind.ToString().ToUpperInvariant(), key = publicKey, victim = victim.ToString().ToUpperInvariant(), vkey = new byte[32], now });
        }

        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = blind,
            LamportTs = 10,
            EventType = EventTypes.WhitelistRevoke,
            Payload = JsonSerializer.Serialize(new WhitelistRevokePayload(victim)),
            Signature = [],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow,
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));

        var apply = () => _receiver.EventApplier.ApplyAsync(evt);

        await apply.Should().ThrowAsync<UnauthorizedAccessException>(
            "a blind node never has authority over cluster state, whatever its row says");
        (await _receiver.WhitelistRepo.GetByNodeIdAsync(victim))!.Status.Should().Be("A");
    }

    private class ConcreteFixture : SyncTestFixture { }
}
